package com.panelos.panelhome.data.transport

import android.os.Process
import java.io.EOFException
import java.io.InputStream
import java.io.IOException
import java.io.OutputStream
import java.net.SocketTimeoutException
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.CompletableFuture
import java.util.concurrent.BlockingQueue
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/**
 * Carries the logical PanelOS byte streams over one physical accessory stream
 * using PLMX v2.
 *
 * The tablet is always the accessory role: it adopts the session nonce the host
 * generates and answers HELLO_ACK. Any valid HELLO is adopted, because a bulk
 * pipe is FIFO - a stale HELLO from the previous session always precedes the
 * current one, so taking the newest match is both safe and self-correcting.
 *
 * Three costs are deliberately engineered out of the steady state and must stay
 * out: fragments are sliced rather than copied, a frame that fits the scratch
 * buffer is one write syscall, and received payloads come from a bounded pool
 * with one explicit owner.
 */
internal class MultiplexedTransportSession(
    private val physicalInput: InputStream,
    private val physicalOutput: OutputStream,
    private val closePhysical: () -> Unit,
    private val metrics: TransportMetrics,
    fragmentBytes: Int = MultiplexFrame.DEFAULT_FRAGMENT_BYTES,
    private val log: (String) -> Unit = {},
    resyncLimits: PlmxResyncLimits = PlmxResyncLimits(),
    /** A newer host greeting took over. The descriptor is untouched. */
    private val onSuperseded: (String) -> Unit = {},
    /** Exactly one terminal report per session, whatever ended it. */
    private val onClosed: (Throwable?) -> Unit = {},
    private val limits: CarrierLimits = CarrierLimits.CABLE,
    private val threadId: () -> Int = { Process.myTid() },
) : AutoCloseable {
    private val profile = metrics.io
    private val fragment = MultiplexFrame.sanitizeFragmentBytes(fragmentBytes).let {
        if (limits.wifi) minOf(it, CarrierLimits.FRAME_BYTES) else it
    }
    private val budget = CarrierBudget(limits)
    private val queueGate = Any()
    private val openRate = CarrierOpenRate()
    private val running = AtomicBoolean()
    private val controlWrites = LinkedBlockingQueue<OutboundWrite>()
    private val videoWrites = ArrayBlockingQueue<OutboundWrite>(1)
    private val writeReady = Semaphore(0)
    private val channels = ConcurrentHashMap<LogicalChannel, MuxConnection>()
    private val wifiStreams = ConcurrentHashMap.newKeySet<MuxConnection>()
    private val payloads = PayloadBufferPool(fragment, MAX_POOLED_PAYLOADS, profile)
    private val reader = PlmxStreamReader(physicalInput, profile, STAGING_BYTES, resyncLimits)
    private val txSequences = IntArray(MAX_CHANNEL_ID + 1)
    private val rxSequences = IntArray(MAX_CHANNEL_ID + 1)
    private val rxSeen = BooleanArray(MAX_CHANNEL_ID + 1)

    /** Owned by the writer thread only. */
    private val headerScratch = ByteArray(PlmxFrame.HEADER_SIZE)
    private val frameScratch = ByteArray(PlmxFrame.HEADER_SIZE + fragment)

    @Volatile private var failure: Throwable? = null
    @Volatile private var state = PlmxSessionState.DISCONNECTED

    var sessionId: PlmxSessionId = PlmxSessionId.EMPTY
        private set

    val isHealthy get() = running.get() && state == PlmxSessionState.ESTABLISHED
    val sessionState get() = state
    val fragmentBytes get() = fragment
    val carrierLiveBytes get() = budget.liveBytes

    /**
     * Completes the PLMX v2 handshake on the calling thread before either loop
     * starts, so the reader never has to distinguish a handshake from channel
     * traffic and a dirty stream is resolved once, up front.
     */
    fun start() {
        check(running.compareAndSet(false, true)) { "Multiplexed session already started" }
        metrics.sessionStarted()
        state = PlmxSessionState.OPENING_PHYSICAL_TRANSPORT
        try {
            state = PlmxSessionState.SEARCHING_FOR_HANDSHAKE
            val hello = reader.searchHandshake(PlmxFrameType.HELLO) { true }
            if (hello.capabilities and CAPABILITY_MULTIPLEXING == 0) {
                throw TransportProtocolException("Peer does not support multiplexing")
            }
            if (reader.discardedBytes > 0) {
                metrics.resynchronized(reader.discardedBytes)
                log(
                    "[PLMX] state=SearchingForHandshake discarded=${reader.discardedBytes} bytes " +
                        "foundMagic=true version=${PlmxFrame.VERSION} session=${hello.sessionId.prefix}",
                )
            }
            sessionId = hello.sessionId
            state = PlmxSessionState.HANDSHAKING
            writeHandshake(PlmxFrameType.HELLO_ACK)
        } catch (error: Throwable) {
            metrics.handshakeFailed()
            state = PlmxSessionState.CLOSING
            closeWith(
                if (error is IOException) error
                else TransportDisconnectedException("PLMX handshake failed", error),
            )
            throw if (error is IOException) error
            else TransportDisconnectedException("PLMX handshake failed", error)
        }
        state = PlmxSessionState.ESTABLISHED
        log("[PLMX] session established role=accessory session=${sessionId.prefix} version=${PlmxFrame.VERSION}")
        Thread(::readerLoop, "PanelOS-USB-reader").apply { isDaemon = true }.start()
        Thread(::writerLoop, "PanelOS-USB-writer").apply { isDaemon = true }.start()
    }

    fun open(channel: LogicalChannel): PanelByteConnection {
        require(channel != LogicalChannel.TRANSPORT)
        limits.validate(channel, 0)
        check(isHealthy) { "Transport session is disconnected" }
        val connection = synchronized(channels) {
            check(isHealthy) { "Transport session is disconnected" }
            if (limits.wifi) {
                if (channels.containsKey(channel)) throw TransportProtocolException("Duplicate WiFi OPEN")
                openRate.record(channel)
            }
            MuxConnection(channel).also {
                check(channels.putIfAbsent(channel, it) == null) { "$channel is already open" }
                if (limits.wifi) wifiStreams.add(it)
            }
        }
        try {
            send(channel, PlmxFrameType.OPEN, EMPTY, 0, 0)
        } catch (error: Exception) {
            channels.remove(channel, connection)
            throw error
        }
        return connection
    }

    private fun writeHandshake(type: PlmxFrameType) {
        val payload = PlmxFrame.encodeHello(CAPABILITIES, sessionId)
        PlmxFrame.encodeHeader(
            frameScratch,
            LogicalChannel.TRANSPORT,
            type,
            payload.size,
            nextSequence(LogicalChannel.TRANSPORT),
            sessionId.tag,
        )
        payload.copyInto(frameScratch, PlmxFrame.HEADER_SIZE)
        val total = PlmxFrame.HEADER_SIZE + payload.size
        physicalOutput.write(frameScratch, 0, total)
        physicalOutput.flush()
        profile.physicalWrite(total)
        profile.frameSent(coalesced = true)
    }

    private fun nextSequence(channel: LogicalChannel): Int {
        val index = channel.id
        txSequences[index] = txSequences[index] + 1
        return txSequences[index]
    }

    /**
     * Enqueues one frame and parks until the writer thread has put it on the
     * wire. That contract is what lets [payload] be referenced by slice instead
     * of copied: the caller cannot reuse the array while it is still pending.
     *
     * The wait is **bounded**. USB has no end of stream, so after a cable pull
     * the writer thread can sit inside one `write()` below the USB stack and
     * never come back; an unbounded wait here then propagates that hang to
     * whoever asked for the write. That is how a detach used to freeze the panel
     * outright - the teardown ran on the main thread, asked for one courtesy
     * FIN, and never returned.
     */
    private fun send(
        channel: LogicalChannel,
        type: PlmxFrameType,
        payload: ByteArray,
        offset: Int,
        length: Int,
    ) {
        val write = enqueue(channel, type, payload, offset, length, copyPayload = false)
        try {
            write.completion.get(WRITE_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)
        } catch (interrupted: InterruptedException) {
            Thread.currentThread().interrupt()
            throw TransportDisconnectedException("Transport write interrupted", interrupted)
        } catch (timeout: TimeoutException) {
            closeWith(
                TransportDisconnectedException(
                    "Physical USB write did not complete within " + WRITE_TIMEOUT_MILLIS + "ms",
                    timeout,
                ),
            )
            throw TransportDisconnectedException("Physical USB write stalled", timeout)
        } catch (error: Exception) {
            throw TransportDisconnectedException("Transport write failed", error.cause ?: error)
        }
    }

    /**
     * Queues a frame without waiting for it.
     *
     * Used for the frames that are courtesies rather than obligations - FIN on
     * close, HELLO_ACK from the reader thread. The payload is copied because the
     * caller is not parked on the write, so the slice contract [send] relies on
     * does not hold; every such payload is tiny.
     */
    private fun enqueue(
        channel: LogicalChannel,
        type: PlmxFrameType,
        payload: ByteArray,
        offset: Int,
        length: Int,
        copyPayload: Boolean,
    ): OutboundWrite = if (limits.wifi) synchronized(queueGate) {
        enqueueCore(channel, type, payload, offset, length, copyPayload)
    } else enqueueCore(channel, type, payload, offset, length, copyPayload)

    private fun enqueueCore(
        channel: LogicalChannel,
        type: PlmxFrameType,
        payload: ByteArray,
        offset: Int,
        length: Int,
        copyPayload: Boolean,
    ): OutboundWrite {
        if (!running.get()) throw TransportDisconnectedException("Transport is disconnected")
        val reservation = try { budget.reserve(true, channel, length) }
        catch (error: Throwable) { closeWith(error); throw error }
        try {
            val write = if (copyPayload) {
                OutboundWrite(
                    channel,
                    type,
                    payload.copyOfRange(offset, offset + length),
                    0,
                    length,
                    System.nanoTime(),
                )
            } else {
                OutboundWrite(channel, type, payload, offset, length, System.nanoTime())
            }
            write.reservation = reservation
            if (channel == LogicalChannel.VIDEO) {
                // The single-slot queue is the video backpressure: the caller waits
                // for the writer to take the previous frame. Bounded, because that
                // wait is on the same stalled writer thread everything else is.
                if (!(if (limits.wifi) videoWrites.offer(write) else videoWrites.offer(write, WRITE_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS))) {
                    if (limits.wifi) throw TransportProtocolException("WiFi video queue exceeded")
                    write.completion.completeExceptionally(
                        TransportDisconnectedException("Video write queue did not drain"),
                    )
                    metrics.videoDrop()
                    return write
                }
                metrics.setQueueDepth(channel, videoWrites.size)
            } else {
                controlWrites.put(write)
                metrics.setQueueDepth(LogicalChannel.CONTROL, controlWrites.size)
            }
            writeReady.release()
            return write
        } catch (error: Throwable) { reservation?.close(); if (limits.wifi) closeWith(error); throw error }
    }

    private fun writerLoop() {
        profile.bindWriterThread(threadId())
        try {
            while (running.get()) {
                writeReady.acquire()
                val write = controlWrites.poll() ?: videoWrites.poll() ?: continue
                try {
                    metrics.setQueueDepth(
                        if (write.channel == LogicalChannel.VIDEO) LogicalChannel.VIDEO else LogicalChannel.CONTROL,
                        if (write.channel == LogicalChannel.VIDEO) videoWrites.size else controlWrites.size,
                    )
                    PlmxFrame.encodeHeader(
                        headerScratch,
                        write.channel,
                        write.type,
                        write.length,
                        nextSequence(write.channel),
                        sessionId.tag,
                    )
                    val coalesced = write.length <= fragment
                    if (coalesced) {
                        // One syscall and one USB transfer per logical frame.
                        headerScratch.copyInto(frameScratch, 0)
                        write.payload.copyInto(
                            frameScratch,
                            PlmxFrame.HEADER_SIZE,
                            write.offset,
                            write.offset + write.length,
                        )
                        val total = PlmxFrame.HEADER_SIZE + write.length
                        physicalOutput.write(frameScratch, 0, total)
                        profile.physicalWrite(total)
                    } else {
                        physicalOutput.write(headerScratch, 0, PlmxFrame.HEADER_SIZE)
                        physicalOutput.write(write.payload, write.offset, write.length)
                        profile.physicalWrite(PlmxFrame.HEADER_SIZE + write.length)
                    }
                    physicalOutput.flush()
                    profile.frameSent(coalesced)
                    metrics.sent(
                        write.channel,
                        write.length,
                        System.nanoTime() - write.enqueuedAt,
                        PlmxFrame.HEADER_SIZE + write.length,
                    )
                    write.completion.complete(Unit)
                } catch (error: Throwable) { write.completion.completeExceptionally(error); throw error }
                finally { write.reservation?.close() }
            }
        } catch (error: Throwable) {
            closeWith(TransportDisconnectedException("Physical USB write failed", error))
        }
    }

    private fun readerLoop() {
        profile.bindReaderThread(threadId())
        try {
            while (running.get()) {
                val header = try {
                    reader.readHeader()
                } catch (error: TransportProtocolException) {
                    // A truncated or spliced frame means the stream is no longer
                    // aligned. Search forward for the next genuine handshake
                    // instead of tearing down the accessory descriptor, which is
                    // the fragile part of the Android lifecycle.
                    if (limits.wifi) throw error
                    resyncAfterFramingError(error)
                    continue
                }
                limits.validate(header.channel, header.payloadLength)
                val reservation = budget.reserve(false, header.channel, header.payloadLength)
                val payload = try {
                    if (limits.wifi) PooledPayload(ByteArray(header.payloadLength), header.payloadLength, null, reservation)
                    else payloads.rent(header.payloadLength)
                } catch (error: Throwable) { reservation?.close(); throw error }
                var accepted = false
                try {
                    reader.readFully(payload.bytes, 0, header.payloadLength)
                    profile.frameReceived()
                    // A HELLO is checked before the session filter, because by
                    // definition it carries a tag this session does not have
                    // yet. Filtering it as stale is what previously made a
                    // reconnect invisible to the tablet.
                    if (!limits.wifi && header.channel == LogicalChannel.TRANSPORT && header.type == PlmxFrameType.HELLO) {
                        readoptSession(header, payload)
                        continue
                    }
                    if (!isCurrentSession(header)) continue
                    metrics.received(
                        header.channel,
                        header.payloadLength,
                        PlmxFrame.HEADER_SIZE + header.payloadLength,
                    )
                    if (!validateSequence(header)) continue
                    accepted = dispatch(header, payload)
                } finally {
                    // Ownership moves to the logical channel only when the frame
                    // was queued for a consumer; every other path recycles here.
                    if (!accepted) payload.release()
                }
            }
        } catch (error: Throwable) {
            closeWith(
                if (error is TransportDisconnectedException) error
                else TransportDisconnectedException("Physical USB disconnected mid-frame", error),
            )
        }
    }

    /**
     * The newest HELLO always wins.
     *
     * A host that restarts sends a new greeting over a pipe this side never saw
     * disconnect - USB has no end of stream, so the accessory cannot tell that
     * its peer went away. Treating any valid HELLO as a session reset is what
     * makes reconnect work without reopening the accessory descriptor, which is
     * the fragile part of the Android lifecycle.
     */
    private fun readoptSession(header: PlmxFrameHeader, payload: PooledPayload) {
        val hello = PlmxFrame.readHandshake(header, payload.bytes, 0, PlmxFrameType.HELLO)
            ?: throw TransportProtocolException("Malformed HELLO after the session was established")
        if (hello.sessionId.contentEquals(sessionId)) {
            // A duplicate greeting for the session already in force is
            // idempotent: re-acknowledge rather than tear anything down.
            writeHandshakeAck()
            return
        }
        if (hello.capabilities and CAPABILITY_MULTIPLEXING == 0) {
            throw TransportProtocolException("Peer does not support multiplexing")
        }
        log(
            "[PLMX] session=${sessionId.prefix} superseded by session=${hello.sessionId.prefix}; " +
                "re-adopting and resetting channels",
        )
        // Everything scoped to the previous session dies with it. Nothing is
        // carried forward: channels, queues and receive sequences all reset, so
        // the application performs its own handshake again on the new session.
        channels.values.forEach {
            it.finish(TransportDisconnectedException("The transport session was superseded"))
        }
        channels.clear()
        // Only receive state resets. Transmit sequences keep climbing because
        // the writer thread owns them and the new peer has no expectation yet.
        rxSeen.fill(false)
        rxSequences.fill(0)
        sessionId = hello.sessionId
        metrics.resynchronized(0)
        writeHandshakeAck()
        onSuperseded(sessionId.prefix)
    }

    private fun resyncAfterFramingError(cause: Throwable) {
        state = PlmxSessionState.RECOVERING
        log("[PLMX] session=${sessionId.prefix} lost frame alignment (${cause.message}); state=SearchingForHandshake")
        val hello = reader.searchHandshake(PlmxFrameType.HELLO) { true }
        if (reader.discardedBytes > 0) metrics.resynchronized(reader.discardedBytes)
        val header = PlmxFrameHeader(
            LogicalChannel.TRANSPORT,
            PlmxFrameType.HELLO,
            PlmxFrame.HELLO_PAYLOAD_SIZE,
            0,
            hello.sessionId.tag,
        )
        readoptSession(header, PooledPayload(PlmxFrame.encodeHello(hello.capabilities, hello.sessionId), PlmxFrame.HELLO_PAYLOAD_SIZE, null))
        state = PlmxSessionState.ESTABLISHED
    }

    /**
     * Queued rather than written directly: the writer thread owns the stream.
     *
     * It is queued *without waiting*, because the caller is the reader thread.
     * Parking the reader on the writer would mean one stalled physical write
     * also stops the session from ever noticing that it stalled.
     */
    private fun writeHandshakeAck() {
        val payload = PlmxFrame.encodeHello(CAPABILITIES, sessionId)
        enqueue(
            channel = LogicalChannel.TRANSPORT,
            type = PlmxFrameType.HELLO_ACK,
            payload = payload,
            offset = 0,
            length = payload.size,
            copyPayload = true,
        )
    }

    /**
     * A frame tagged for a different session belongs to a predecessor whose
     * bytes were still in the pipe. Dropping it is the whole point of v2: under
     * v1 it would have been decoded as this session's and killed the channel.
     */
    private fun isCurrentSession(header: PlmxFrameHeader): Boolean {
        if (header.sessionTag == sessionId.tag) return true
        metrics.staleFrameDropped()
        return false
    }

    /**
     * Control and Assets are strictly ordered: the carrier is a reliable
     * in-order pipe, so a gap there means corruption and must fail the session.
     * Video is monotonic only, because latest-frame-wins upstream legitimately
     * leaves holes; old or duplicate video is dropped rather than fatal.
     */
    private fun validateSequence(header: PlmxFrameHeader): Boolean {
        val index = header.channel.id
        val sequence = header.sequence
        if (!rxSeen[index]) {
            rxSeen[index] = true
            rxSequences[index] = sequence
            return true
        }
        val previous = rxSequences[index]
        val delta = sequence - previous
        if (header.channel == LogicalChannel.VIDEO) {
            if (delta <= 0) {
                metrics.staleFrameDropped()
                return false
            }
            if (delta > 1) metrics.sequenceGap()
            rxSequences[index] = sequence
            return true
        }
        if (delta != 1) {
            throw TransportProtocolException(
                "${header.channel} sequence $sequence broke strict ordering after $previous",
            )
        }
        rxSequences[index] = sequence
        return true
    }

    /** Returns true when [payload] was handed to a consumer and must not be recycled. */
    private fun dispatch(header: PlmxFrameHeader, payload: PooledPayload): Boolean {
        if (header.channel == LogicalChannel.TRANSPORT) {
            // The handshake completed before the loops started, so a transport
            // frame here means the peer restarted mid-stream.
            throw TransportProtocolException("Unexpected ${header.type} after the session was established")
        }
        return when (header.type) {
            PlmxFrameType.OPEN -> {
                synchronized(channels) {
                    if (limits.wifi && !running.get()) throw TransportDisconnectedException("Session closed")
                    if (limits.wifi) {
                        if (channels.containsKey(header.channel)) throw TransportProtocolException("Duplicate WiFi OPEN")
                        openRate.record(header.channel)
                    }
                    val existing = channels.remove(header.channel)
                    existing?.finish(TransportDisconnectedException("Logical channel was replaced"))
                    val opened = MuxConnection(header.channel)
                    channels[header.channel] = opened
                    if (limits.wifi) wifiStreams.add(opened)
                }
                false
            }
            PlmxFrameType.FIN -> {
                val closing = channels.remove(header.channel)
                closing?.finish(null)
                false
            }
            PlmxFrameType.DATA -> {
                val connection = channels[header.channel]
                    ?: throw TransportProtocolException("Data arrived for unopened ${header.channel}")
                if (connection.receive(payload)) {
                    true
                } else {
                    channels.remove(header.channel, connection)
                    metrics.videoDrop()
                    connection.finish(
                        TransportDisconnectedException(
                            "Video consumer congestion closed only the video channel",
                        ),
                    )
                    enqueue(header.channel, PlmxFrameType.FIN, EMPTY, 0, 0, copyPayload = false)
                    false
                }
            }
            else -> throw TransportProtocolException("Unexpected PLMX frame type ${header.type}")
        }
    }

    private fun closeWith(error: Throwable?) {
        if (!running.compareAndSet(true, false)) return
        state = PlmxSessionState.CLOSING
        if (error == null) {
            log("[PLMX] session=${sessionId.prefix} closed by request")
        } else {
            val root = generateSequence(error, Throwable::cause).last()
            log(
                "[PLMX] session=${sessionId.prefix} failed in state=$state: " +
                    "${error::class.java.simpleName}: ${error.message}" +
                    " (root ${root::class.java.simpleName}: ${root.message});" +
                    " framesTx=${profile.framesTx} framesRx=${profile.framesRx}" +
                    " reads=${profile.readCalls} writes=${profile.writeCalls}",
            )
            metrics.error()
        }
        failure = error
        runCatching(closePhysical)
        synchronized(queueGate) {
            for (queue in listOf(controlWrites, videoWrites)) while (true) {
                val abandoned = queue.poll() ?: break
                abandoned.reservation?.close()
                abandoned.completion.completeExceptionally(error ?: EOFException())
            }
        }
        synchronized(channels) {
            if (limits.wifi) wifiStreams.toList().forEach { it.finish(error ?: EOFException("Session closed")) }
            else channels.values.forEach { it.finish(error) }
            channels.clear()
        }
        payloads.clear()
        writeReady.release()
        state = PlmxSessionState.DISCONNECTED
        // Last, and exactly once: the lifecycle above this decides what to do
        // about a dead session, and it must never be told twice.
        onClosed(error)
    }

    override fun close() = closeWith(null)

    private inner class MuxConnection(
        override val channel: LogicalChannel,
    ) : PanelByteConnection {
        private val closed = AtomicBoolean()
        private val packets: BlockingQueue<InboundPacket> = if (limits.wifi) {
            CarrierLimits.inboundQueue(channel)
        } else if (channel == LogicalChannel.VIDEO) {
            ArrayBlockingQueue(MAX_VIDEO_INBOUND_PACKETS)
        } else {
            LinkedBlockingQueue()
        }
        @Volatile private var timeoutMillis = 0
        private var releaseActive: (() -> Unit)? = null
        private var hasActive: () -> Boolean = { false }

        override val input: InputStream = object : InputStream() {
            private var current: PooledPayload? = null
            private var offset = 0
            init { releaseActive = { releaseCurrent() }; hasActive = { current != null } }

            override fun read(): Int {
                val one = ByteArray(1)
                return if (read(one) < 0) -1 else one[0].toInt() and 0xff
            }

            override fun read(buffer: ByteArray, targetOffset: Int, length: Int): Int {
                if (length == 0) return 0
                while (current == null || offset == current!!.length) {
                    releaseCurrent()
                    when (val packet = takePacket()) {
                        is InboundPacket.Data -> { current = packet.payload; offset = 0 }
                        is InboundPacket.End -> {
                            packet.error?.let {
                                throw if (it is IOException) it else TransportDisconnectedException("Logical channel closed", it)
                            }
                            return -1
                        }
                    }
                }
                val active = current!!
                val count = minOf(length, active.length - offset)
                active.bytes.copyInto(buffer, targetOffset, offset, offset + count)
                offset += count
                if (offset == active.length) releaseCurrent()
                return count
            }

            private fun releaseCurrent() {
                current?.release()
                current = null
                offset = 0
                forgetIfDrained()
            }

            private fun takePacket(): InboundPacket = if (timeoutMillis > 0) {
                packets.poll(timeoutMillis.toLong(), TimeUnit.MILLISECONDS)
                    ?: throw SocketTimeoutException("Logical channel read timed out")
            } else {
                packets.take()
            }
        }

        override val output: OutputStream = object : OutputStream() {
            override fun write(value: Int) = write(byteArrayOf(value.toByte()))
            override fun write(buffer: ByteArray, offset: Int, length: Int) {
                if (closed.get()) throw TransportDisconnectedException("Logical channel is closed")
                var chunkOffset = offset
                val end = offset + length
                val maximum = minOf(fragment, PlmxFrame.maximumPayload(channel))
                while (chunkOffset < end) {
                    val count = minOf(maximum, end - chunkOffset)
                    // Slice, do not copy: send() parks until the fragment is on
                    // the wire, so the caller's array stays valid throughout.
                    send(channel, PlmxFrameType.DATA, buffer, chunkOffset, count)
                    profile.fragmentSent()
                    chunkOffset += count
                }
            }
        }

        override val isClosed get() = closed.get() || !running.get()
        override fun setReadTimeout(timeoutMillis: Int) { this.timeoutMillis = timeoutMillis.coerceAtLeast(0) }

        fun receive(payload: PooledPayload): Boolean = if (limits.wifi) {
            if (!packets.offer(InboundPacket.Data(payload))) throw TransportProtocolException("WiFi input queue exceeded")
            true
        } else if (channel == LogicalChannel.VIDEO) {
            packets.offer(InboundPacket.Data(payload))
        } else {
            packets.put(InboundPacket.Data(payload))
            true
        }

        fun finish(error: Throwable?) {
            if (limits.wifi && error != null) { releaseActive?.invoke(); drain() }
            if (closed.compareAndSet(false, true)) {
                if (limits.wifi && error != null) releaseActive?.invoke()
                if (error != null) drain()
                if (!packets.offer(InboundPacket.End(error))) {
                    drain()
                    metrics.videoDrop()
                    packets.offer(
                        InboundPacket.End(
                            TransportDisconnectedException(
                                "Video channel closed while its bounded receive queue was full",
                            ),
                        ),
                    )
                }
            }
            forgetIfDrained()
        }

        private fun forgetIfDrained() {
            if (limits.wifi && closed.get() && !hasActive() && packets.none { it is InboundPacket.Data }) wifiStreams.remove(this)
        }

        private fun drain() {
            while (true) {
                val packet = packets.poll() ?: return
                if (packet is InboundPacket.Data) packet.payload.release()
            }
        }

        /**
         * Never waits for the FIN.
         *
         * A logical close runs on whatever thread tore the session down, and
         * after a cable pull the writer thread is inside a physical write that
         * does not return. Waiting here is what froze the panel on detach: the
         * FIN is a courtesy to a peer that, in the case that matters, is not
         * on the other end of the cable any more.
         */
        override fun close() {
            if (limits.wifi) { releaseActive?.invoke(); drain() }
            if (!closed.compareAndSet(false, true)) { forgetIfDrained(); return }
            channels.remove(channel, this)
            runCatching {
                enqueue(channel, PlmxFrameType.FIN, EMPTY, 0, 0, copyPayload = false)
            }
            packets.offer(InboundPacket.End(null))
            forgetIfDrained()
        }
    }

    private sealed interface InboundPacket {
        class Data(val payload: PooledPayload) : InboundPacket
        class End(val error: Throwable?) : InboundPacket
    }

    private class OutboundWrite(
        val channel: LogicalChannel,
        val type: PlmxFrameType,
        val payload: ByteArray,
        val offset: Int,
        val length: Int,
        val enqueuedAt: Long,
        val completion: CompletableFuture<Unit> = CompletableFuture(),
    ) { var reservation: AutoCloseable? = null }

    companion object {
        private val EMPTY = ByteArray(0)
        private const val CAPABILITY_MULTIPLEXING = 1 shl 0
        private const val CAPABILITY_CONTROL_PRIORITY = 1 shl 1
        private const val CAPABILITY_VIDEO_BACKPRESSURE = 1 shl 2
        private const val CAPABILITY_SESSION_IDENTITY = 1 shl 3
        private const val CAPABILITY_RESYNC = 1 shl 4
        private const val CAPABILITIES = CAPABILITY_MULTIPLEXING or
            CAPABILITY_CONTROL_PRIORITY or CAPABILITY_VIDEO_BACKPRESSURE or
            CAPABILITY_SESSION_IDENTITY or CAPABILITY_RESYNC
        private const val MAX_VIDEO_INBOUND_PACKETS = 8
        private const val MAX_POOLED_PAYLOADS = 16
        private const val MAX_CHANNEL_ID = 4

        /**
         * Matches BULK_BUFFER_SIZE in the kernel's f_accessory gadget. Every
         * refill posts exactly this many bytes, which keeps the queued USB
         * request a whole number of 512-byte max packets. A smaller request
         * would risk a babble error when the host coalesces a header and payload
         * into one transfer.
         */
        internal const val STAGING_BYTES = 16 * 1024

        /**
         * Bound on one queued frame reaching the wire. Long enough that a busy
         * host never trips it, short enough that a detached gadget cannot hold
         * a caller - including a teardown - for the life of the process.
         */
        internal const val WRITE_TIMEOUT_MILLIS = 5_000L

        private val counter = AtomicInteger()
    }
}
