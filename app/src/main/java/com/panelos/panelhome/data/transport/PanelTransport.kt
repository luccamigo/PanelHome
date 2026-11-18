package com.panelos.panelhome.data.transport

import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

enum class LogicalChannel(val id: Int) {
    TRANSPORT(0),
    CONTROL(1),
    VIDEO(2),
    ASSETS(3),
    AUDIO(4);

    companion object {
        fun fromId(id: Int): LogicalChannel = entries.firstOrNull { it.id == id }
            ?: throw TransportProtocolException("Unknown logical channel $id")
    }
}

interface PanelByteConnection : Closeable {
    val channel: LogicalChannel
    val input: InputStream
    val output: OutputStream
    val isClosed: Boolean
    fun setReadTimeout(timeoutMillis: Int)
}

interface PanelTransport : Closeable {
    val name: String
    val metrics: TransportMetrics
    suspend fun connect(channel: LogicalChannel): PanelByteConnection
}

class TransportDisconnectedException(message: String, cause: Throwable? = null) :
    IOException(message, cause)

class TransportProtocolException(message: String) : IOException(message)

class TransportMetrics {
    private val controlTxValue = AtomicLong()
    private val controlRxValue = AtomicLong()
    private val videoTxValue = AtomicLong()
    private val videoRxValue = AtomicLong()
    private val controlLatencyNanosValue = AtomicLong()
    private val controlWritesValue = AtomicLong()
    private val videoDropsValue = AtomicLong()
    private val reconnectsValue = AtomicLong()
    private val payloadTxValue = AtomicLong()
    private val payloadRxValue = AtomicLong()
    private val wireTxValue = AtomicLong()
    private val wireRxValue = AtomicLong()
    private val errorsValue = AtomicLong()
    private val controlDepthValue = AtomicInteger()
    private val videoDepthValue = AtomicInteger()
    private val sessionsStartedValue = AtomicLong()
    private val resyncEventsValue = AtomicLong()
    private val resyncDiscardedBytesValue = AtomicLong()
    private val staleFramesDroppedValue = AtomicLong()
    private val handshakeFailuresValue = AtomicLong()
    private val sequenceGapsValue = AtomicLong()

    /** Physical-layer counters used to attribute transport CPU. */
    val io = TransportIoProfile()

    val controlBytesTx get() = controlTxValue.get()
    val controlBytesRx get() = controlRxValue.get()
    val videoBytesTx get() = videoTxValue.get()
    val videoBytesRx get() = videoRxValue.get()
    val videoDrops get() = videoDropsValue.get()
    val reconnects get() = reconnectsValue.get()
    val payloadBytesTx get() = payloadTxValue.get()
    val payloadBytesRx get() = payloadRxValue.get()
    val wireBytesTx get() = wireTxValue.get()
    val wireBytesRx get() = wireRxValue.get()
    val errors get() = errorsValue.get()
    val sessionsStarted get() = sessionsStartedValue.get()
    val resyncEvents get() = resyncEventsValue.get()
    val resyncDiscardedBytes get() = resyncDiscardedBytesValue.get()
    val staleFramesDropped get() = staleFramesDroppedValue.get()
    val handshakeFailures get() = handshakeFailuresValue.get()
    val sequenceGaps get() = sequenceGapsValue.get()
    val controlQueueDepth get() = controlDepthValue.get()
    val videoQueueDepth get() = videoDepthValue.get()
    val averageControlLatencyNanos: Long
        get() = controlWritesValue.get().takeIf { it > 0 }
            ?.let { controlLatencyNanosValue.get() / it } ?: 0

    internal fun sent(
        channel: LogicalChannel,
        bytes: Int,
        latencyNanos: Long,
        wireBytes: Int = bytes,
    ) {
        payloadTxValue.addAndGet(bytes.toLong())
        wireTxValue.addAndGet(wireBytes.toLong())
        when (channel) {
            LogicalChannel.CONTROL -> {
                controlTxValue.addAndGet(bytes.toLong())
                controlLatencyNanosValue.addAndGet(latencyNanos)
                controlWritesValue.incrementAndGet()
            }
            LogicalChannel.VIDEO -> videoTxValue.addAndGet(bytes.toLong())
            else -> Unit
        }
    }

    internal fun received(channel: LogicalChannel, bytes: Int, wireBytes: Int = bytes) {
        payloadRxValue.addAndGet(bytes.toLong())
        wireRxValue.addAndGet(wireBytes.toLong())
        when (channel) {
            LogicalChannel.CONTROL -> controlRxValue.addAndGet(bytes.toLong())
            LogicalChannel.VIDEO -> videoRxValue.addAndGet(bytes.toLong())
            else -> Unit
        }
    }

    internal fun setQueueDepth(channel: LogicalChannel, value: Int) {
        when (channel) {
            LogicalChannel.CONTROL -> controlDepthValue.set(value)
            LogicalChannel.VIDEO -> videoDepthValue.set(value)
            else -> Unit
        }
    }

    internal fun reconnect() = reconnectsValue.incrementAndGet()
    internal fun videoDrop() = videoDropsValue.incrementAndGet()
    internal fun error() = errorsValue.incrementAndGet()
    internal fun sessionStarted() = sessionsStartedValue.incrementAndGet()
    internal fun staleFrameDropped() = staleFramesDroppedValue.incrementAndGet()
    internal fun handshakeFailed() = handshakeFailuresValue.incrementAndGet()
    internal fun sequenceGap() = sequenceGapsValue.incrementAndGet()

    internal fun resynchronized(discardedBytes: Long) {
        resyncEventsValue.incrementAndGet()
        resyncDiscardedBytesValue.addAndGet(discardedBytes)
    }
}

class AdbTcpTransport(
    private val host: String = LOOPBACK_HOST,
    private val controlPort: Int = CONTROL_PORT,
    private val videoPort: Int = VIDEO_PORT,
    authenticationToken: ByteArray? = null,
) : PanelTransport {
    private val authenticationToken = authenticationToken?.copyOf()
    override val name = "adb-tcp"
    override val metrics = TransportMetrics()

    override suspend fun connect(channel: LogicalChannel): PanelByteConnection {
        val token = authenticationToken
            ?: throw TransportDisconnectedException(
                "ADB/TCP development session is missing authentication",
            )
        val port = when (channel) {
            LogicalChannel.CONTROL -> controlPort
            LogicalChannel.VIDEO -> videoPort
            LogicalChannel.ASSETS -> BENCHMARK_PORT
            else -> throw IllegalArgumentException("ADB/TCP exposes control, video, and benchmark assets")
        }
        val socket = Socket()
        try {
            socket.keepAlive = true
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MILLIS)
            AdbTcpAuthentication.authenticate(socket, channel, token)
            return SocketPanelByteConnection(channel, socket, metrics)
        } catch (error: Exception) {
            runCatching { socket.close() }
            throw TransportDisconnectedException("ADB/TCP connect failed for $channel", error)
        }
    }

    override fun close() {
        authenticationToken?.fill(0)
    }

    private class SocketPanelByteConnection(
        override val channel: LogicalChannel,
        private val socket: Socket,
        private val metrics: TransportMetrics,
    ) : PanelByteConnection {
        private val socketInput = socket.getInputStream()
        private val socketOutput = socket.getOutputStream()
        override val input: InputStream = object : InputStream() {
            override fun read(): Int {
                val one = ByteArray(1)
                return if (read(one) < 0) -1 else one[0].toInt() and 0xff
            }
            override fun read(buffer: ByteArray, offset: Int, length: Int): Int = try {
                socketInput.read(buffer, offset, length).also { if (it > 0) metrics.received(channel, it) }
            } catch (error: IOException) {
                if (error is SocketTimeoutException) throw error
                throw TransportDisconnectedException("ADB/TCP read failed", error)
            }
        }
        override val output: OutputStream = object : OutputStream() {
            override fun write(value: Int) = write(byteArrayOf(value.toByte()))
            override fun write(buffer: ByteArray, offset: Int, length: Int) {
                val started = System.nanoTime()
                try {
                    socketOutput.write(buffer, offset, length)
                    metrics.sent(channel, length, System.nanoTime() - started)
                } catch (error: IOException) {
                    throw TransportDisconnectedException("ADB/TCP write failed", error)
                }
            }
            override fun flush() = socketOutput.flush()
        }
        override val isClosed get() = socket.isClosed
        override fun setReadTimeout(timeoutMillis: Int) { socket.soTimeout = timeoutMillis }
        override fun close() = socket.close()
    }

    companion object {
        const val LOOPBACK_HOST = "127.0.0.1"
        const val CONTROL_PORT = 8765
        const val VIDEO_PORT = 8766
        const val BENCHMARK_PORT = 8767
        private const val CONNECT_TIMEOUT_MILLIS = 3_000
    }
}
