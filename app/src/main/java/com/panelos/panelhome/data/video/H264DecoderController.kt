package com.panelos.panelhome.data.video

import android.media.MediaCodec
import android.media.MediaFormat
import android.os.Build
import android.view.Surface
import com.panelos.panelhome.domain.video.VideoSurfaceController
import com.panelos.panelhome.domain.video.VideoSurfaceDiagnostics
import com.panelos.panelhome.domain.video.VideoLogger
import java.io.Closeable
import java.util.concurrent.ArrayBlockingQueue
import java.util.concurrent.RejectedExecutionException
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

enum class H264DecoderState(val logName: String) {
    IDLE("idle"),
    WAITING_FOR_SURFACE("waiting_for_surface"),
    WAITING_FOR_CODEC_CONFIG("waiting_for_codec_config"),
    WAITING_FOR_KEYFRAME("waiting_for_keyframe"),
    DECODING("decoding"),
    RENDERING("rendering"),
    RECOVERING("recovering"),
    ERROR("error"),
    CLOSED("closed"),
}

internal enum class H264DecoderErrorDisposition {
    RECOVER,
    FATAL,
}

internal fun classifyH264DecoderError(
    isRecoverable: Boolean,
    isTransient: Boolean,
): H264DecoderErrorDisposition = if (isRecoverable || isTransient) {
    H264DecoderErrorDisposition.RECOVER
} else {
    H264DecoderErrorDisposition.FATAL
}

private class H264DecoderBackpressureException(message: String) : RuntimeException(message)

data class H264DecoderDiagnostics(
    val surfaceState: String = "detached",
    val decoderState: H264DecoderState = H264DecoderState.IDLE,
    val accessUnitsQueued: Long = 0,
    val framesRendered: Long = 0,
    val codecErrors: Long = 0,
    val queueDrops: Long = 0,
    val queuedAccessUnits: Int = 0,
    val queuedBytes: Long = 0,
    val queueHighWatermarkBytes: Long = 0,
    val lastQueuedAtNanos: Long = 0,
    val lastRenderedAtNanos: Long = 0,
    val codecName: String? = null,
) {
    fun toClientRuntimeState() = VideoClientRuntimeState(
        surfaceState = surfaceState,
        decoderState = decoderState.logName,
        renderedFrames = framesRendered,
        codecErrors = codecErrors,
        lastRenderedAtNanos = lastRenderedAtNanos,
    )
}

interface H264DecoderController : Closeable, VideoSurfaceController {
    fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    )

    fun queuePacket(generation: Long, packet: H264VideoPacket)

    fun diagnostics(): H264DecoderDiagnostics = H264DecoderDiagnostics()

    override fun surfaceDiagnostics() = VideoSurfaceDiagnostics(
        decoderStateLogName = diagnostics().decoderState.logName,
    )

    fun setDiagnosticsListener(listener: (H264DecoderDiagnostics) -> Unit) = Unit

    fun stopSession()
}

class MediaCodecH264DecoderController(
    private val capabilities: H264DecoderCapabilityProvider =
        AndroidH264DecoderCapabilityProvider,
    private val adapterFactory: H264CodecAdapterFactory = AndroidH264CodecAdapterFactory,
    private val logger: VideoLogger = AndroidVideoLogger,
) : H264DecoderController {
    private val lifecycleLock = Any()
    private val executor = ThreadPoolExecutor(
        1,
        1,
        0L,
        TimeUnit.MILLISECONDS,
        ArrayBlockingQueue(COMMAND_CAPACITY),
    ) { runnable ->
        Thread(runnable, "PanelH264Decoder").apply { isDaemon = true }
    }

    @Volatile
    private var closed = false
    private var sessionRequested = false
    private var activeGeneration: Long? = null
    private var activeSession: VideoSessionInfo? = null
    private var activeSurface: Surface? = null
    private var attachedSurfaceGeneration: Long? = null
    private var adapter: H264CodecAdapter? = null
    private val packetGate = H264DecoderPacketGate()
    private var renderedCallback: (Long, Long, Double) -> Unit = { _, _, _ -> }
    private var errorCallback: (Long, Long?, String, Throwable) -> Unit = { _, _, _, _ -> }
    @Volatile private var decoderState = H264DecoderState.IDLE
    @Volatile private var surfaceState = "detached"
    @Volatile private var codecName: String? = null
    private val accessUnitsQueued = AtomicLong()
    private val framesRendered = AtomicLong()
    private val codecErrors = AtomicLong()
    private val queueDrops = AtomicLong()
    private val dependencyLossPending = AtomicBoolean()
    private val retentionBudget = H264RetentionBudget(
        MAX_QUEUED_H264_ACCESS_UNITS,
        MAX_QUEUED_H264_BYTES,
    )
    private val queueBudgetLock = Any()
    private val lastQueueMetricsLogNanos = AtomicLong()
    private val lastQueuedAtNanos = AtomicLong()
    private val lastRenderedAtNanos = AtomicLong()
    @Volatile private var diagnosticsListener: (H264DecoderDiagnostics) -> Unit = {}

    override fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    ) {
        require(session.codec == VideoProtocol.CODEC_H264)
        synchronized(lifecycleLock) {
            if (closed) return
            sessionRequested = true
        }
        executeControl {
            releaseAdapter()
            activeGeneration = generation
            activeSession = session
            packetGate.reset()
            decoderState = if (
                attachedSurfaceGeneration == generation && activeSurface?.isValid == true
            ) {
                H264DecoderState.WAITING_FOR_CODEC_CONFIG
            } else {
                H264DecoderState.WAITING_FOR_SURFACE
            }
            publishDiagnostics()
            renderedCallback = onFrameRendered
            errorCallback = onError
            createAdapterIfReady()
        }
    }

    override fun attachSurface(generation: Long, surface: Surface) {
        executeControl {
            if (!surface.isValid) return@executeControl
            if (activeSurface !== surface) {
                releaseAdapter()
                activeSurface = surface
                attachedSurfaceGeneration = generation
                packetGate.onDecoderGone()
            }
            surfaceState = "attached:${System.identityHashCode(surface)}"
            publishDiagnostics()
            if (activeGeneration != generation) return@executeControl
            decoderState = H264DecoderState.WAITING_FOR_CODEC_CONFIG
            createAdapterIfReady()
        }
    }

    override fun detachSurface(generation: Long, surface: Surface) {
        executeControl {
            if (attachedSurfaceGeneration == generation && activeSurface === surface) {
                releaseAdapter()
                activeSurface = null
                attachedSurfaceGeneration = null
                packetGate.onDecoderGone()
                surfaceState = "destroyed:${System.identityHashCode(surface)}"
                decoderState = if (activeGeneration == generation) {
                    H264DecoderState.WAITING_FOR_SURFACE
                } else {
                    H264DecoderState.IDLE
                }
                logger.transition("H.264 decoder Surface detached")
                publishDiagnostics()
            }
        }
    }

    override fun queuePacket(generation: Long, packet: H264VideoPacket) {
        executePacket(generation, packet) {
            if (activeGeneration != generation) return@executePacket
            val accepted = packetGate.accept(packet)
            val currentAdapter = adapter ?: run {
                publishDiagnostics()
                return@executePacket
            }
            decoderState = when {
                packet.header.isConfig -> H264DecoderState.WAITING_FOR_KEYFRAME
                packet.header.isKeyFrame -> H264DecoderState.DECODING
                else -> decoderState
            }
            accepted.forEach { acceptedPacket ->
                currentAdapter.queue(acceptedPacket)
                if (!acceptedPacket.header.isConfig) {
                    accessUnitsQueued.incrementAndGet()
                    lastQueuedAtNanos.set(System.nanoTime())
                }
            }
            publishDiagnostics()
        }
    }

    override fun diagnostics(): H264DecoderDiagnostics = H264DecoderDiagnostics(
        surfaceState = surfaceState,
        decoderState = decoderState,
        accessUnitsQueued = accessUnitsQueued.get(),
        framesRendered = framesRendered.get(),
        codecErrors = codecErrors.get(),
        queueDrops = queueDrops.get(),
        queuedAccessUnits = retentionBudget.units(),
        queuedBytes = retentionBudget.bytes(),
        queueHighWatermarkBytes = retentionBudget.highWatermarkBytes(),
        lastQueuedAtNanos = lastQueuedAtNanos.get(),
        lastRenderedAtNanos = lastRenderedAtNanos.get(),
        codecName = codecName,
    )

    override fun setDiagnosticsListener(listener: (H264DecoderDiagnostics) -> Unit) {
        diagnosticsListener = listener
        publishDiagnostics()
    }

    override fun stopSession() {
        val shouldStop = synchronized(lifecycleLock) {
            if (closed || !sessionRequested) {
                false
            } else {
                sessionRequested = false
                true
            }
        }
        if (!shouldStop) return
        clearExecutorQueue()
        executeControl {
            releaseAdapter()
            activeGeneration = null
            activeSession = null
            packetGate.reset()
            decoderState = H264DecoderState.IDLE
            publishDiagnostics()
            renderedCallback = { _, _, _ -> }
            errorCallback = { _, _, _, _ -> }
            logger.transition("H.264 decoder session stopped")
        }
    }

    override fun close() {
        if (closed) return
        stopSession()
        synchronized(lifecycleLock) { closed = true }
        decoderState = H264DecoderState.CLOSED
        publishDiagnostics()
        executor.shutdown()
        if (!executor.awaitTermination(CLOSE_TIMEOUT_MILLIS, TimeUnit.MILLISECONDS)) {
            executor.shutdownNow()
        }
    }

    private fun createAdapterIfReady() {
        if (adapter != null) return
        val generation = activeGeneration ?: return
        val session = activeSession ?: return
        val surface = activeSurface
            ?.takeIf { attachedSurfaceGeneration == generation && it.isValid }
            ?: run {
                decoderState = H264DecoderState.WAITING_FOR_SURFACE
                return
            }
        try {
            val capability = capabilities.findDecoder()
                ?: throw VideoProtocolException("No Android H.264 decoder is available")
            adapter = adapterFactory.create(
                capability = capability,
                session = session,
                surface = surface,
                logger = logger,
                onFrameRendered = { sequence, latencyMillis ->
                    framesRendered.incrementAndGet()
                    lastRenderedAtNanos.set(System.nanoTime())
                    decoderState = H264DecoderState.RENDERING
                    publishDiagnostics()
                    renderedCallback(generation, sequence, latencyMillis)
                },
            )
            codecName = capability.codecName
            val replay = packetGate.onDecoderReady()
            replay.forEach(adapter!!::queue)
            decoderState = when {
                replay.any { it.header.isKeyFrame } -> H264DecoderState.DECODING
                packetGate.hasConfiguration() -> H264DecoderState.WAITING_FOR_KEYFRAME
                else -> H264DecoderState.WAITING_FOR_CODEC_CONFIG
            }
            publishDiagnostics()
            logger.transition(
                "H.264 decoder started: ${capability.codecName}, " +
                    "hardware=${capability.isHardwareAccelerated}, " +
                    "softwareOnly=${capability.isSoftwareOnly}, " +
                    "vendor=${capability.isVendor}, " +
                    "lowLatency=${capability.supportsLowLatency}, " +
                    "maxSupportedInstances=${capability.maxSupportedInstances ?: "unknown"}",
            )
        } catch (error: Throwable) {
            releaseAdapter(reportFailure = false)
            reportError(generation, null, "decoder_configure", error)
        }
    }

    private fun releaseAdapter(reportFailure: Boolean = true) {
        try {
            adapter?.close()
        } catch (error: Throwable) {
            if (reportFailure) {
                activeGeneration?.let { reportError(it, null, "decoder_release", error) }
            } else {
                logger.error("decoder_recovery_release", null, error)
            }
        } finally {
            adapter = null
        }
    }

    private fun executeControl(block: () -> Unit) {
        if (closed) return
        try {
            executor.execute {
                try {
                    block()
                } catch (error: Throwable) {
                    activeGeneration?.let { reportError(it, null, "decoder_control", error) }
                }
            }
        } catch (rejected: RejectedExecutionException) {
            activeGeneration?.let { reportError(it, null, "decoder_queue", rejected) }
        }
    }

    private fun executePacket(
        generation: Long,
        packet: H264VideoPacket,
        block: () -> Unit,
    ) {
        if (closed) throw VideoProtocolException("H.264 decoder is closed")
        val task = PacketTask(generation, packet) {
            try {
                if (dependencyLossPending.getAndSet(false)) {
                    packetGate.onAccessUnitDropped()
                    decoderState = H264DecoderState.WAITING_FOR_KEYFRAME
                }
                try {
                    block()
                } catch (backpressure: H264DecoderBackpressureException) {
                    packetGate.onAccessUnitDropped()
                    decoderState = H264DecoderState.WAITING_FOR_KEYFRAME
                    recordQueueDrop(packet.header.sequence, "input_buffer_unavailable")
                    publishDiagnostics()
                } catch (error: Throwable) {
                    activeGeneration?.let { generation ->
                        if (!tryRecoverDecoder(generation, packet.header.sequence, error)) {
                            reportError(generation, packet.header.sequence, "decoder_input", error)
                        }
                    }
                }
            } finally {
                retentionBudget.release(packet.payload.size)
                publishDiagnostics()
            }
        }
        synchronized(queueBudgetLock) {
            if (packet.payload.size > MAX_QUEUED_H264_BYTES) {
                markDropped(packet, "item_exceeds_byte_budget")
                return
            }
            while (!retentionBudget.canReserve(packet.payload.size)) {
                val oldest = executor.queue
                    .filterIsInstance<PacketTask>()
                    .firstOrNull { !it.packet.header.isConfig }
                if (oldest == null || !executor.queue.remove(oldest)) {
                    markDropped(packet, "budget_reject")
                    return
                }
                retentionBudget.release(oldest.packet.payload.size)
                markDropped(oldest.packet, "drop_oldest")
            }
            retentionBudget.reserve(packet.payload.size)
            try {
                executor.execute(task)
            } catch (_: RejectedExecutionException) {
                retentionBudget.release(packet.payload.size)
                if (!closed) markDropped(packet, "executor_rejected")
            }
        }
    }

    private fun clearExecutorQueue() = synchronized(queueBudgetLock) {
        while (true) {
            val queued = executor.queue.poll() ?: break
            if (queued is PacketTask) retentionBudget.release(queued.packet.payload.size)
        }
    }

    private fun markDropped(packet: H264VideoPacket, reason: String) {
        if (!packet.header.isConfig) dependencyLossPending.set(true)
        recordQueueDrop(packet.header.sequence, reason)
        publishDiagnostics()
    }

    private fun recordQueueDrop(sequence: Long, reason: String) {
        val dropped = queueDrops.incrementAndGet()
        val now = System.nanoTime()
        val previous = lastQueueMetricsLogNanos.get()
        if (now - previous >= QUEUE_METRICS_LOG_INTERVAL_NANOS &&
            lastQueueMetricsLogNanos.compareAndSet(previous, now)
        ) {
            logger.performance(
                "[VideoClientMetrics] decoderQueueDrop=$dropped reason=$reason " +
                    "lastSequence=$sequence queuedUnits=${retentionBudget.units()} " +
                    "queuedBytes=${retentionBudget.bytes()} " +
                    "queueHighWatermark=${retentionBudget.highWatermarkBytes()}",
            )
        }
    }

    private inner class PacketTask(
        val generation: Long,
        val packet: H264VideoPacket,
        private val action: () -> Unit,
    ) : Runnable {
        override fun run() = action()
    }

    private fun tryRecoverDecoder(
        generation: Long,
        sequence: Long?,
        error: Throwable,
    ): Boolean {
        val codecError = error as? MediaCodec.CodecException ?: return false
        logCodecException("decoder_recoverable", sequence, codecError)
        if (classifyH264DecoderError(
                isRecoverable = codecError.isRecoverable,
                isTransient = codecError.isTransient,
            ) == H264DecoderErrorDisposition.FATAL
        ) {
            return false
        }

        decoderState = H264DecoderState.RECOVERING
        publishDiagnostics()
        logger.transition(
            "H.264 decoder recovery started: generation=$generation " +
                "sequence=${sequence ?: "n/a"}",
        )
        releaseAdapter(reportFailure = false)
        packetGate.onDecoderGone()
        val errorsBeforeRecreate = codecErrors.get()
        createAdapterIfReady()
        val recovered = adapter != null
        logger.transition("H.264 decoder recovery success=$recovered")
        // createAdapterIfReady reports its own fatal configuration failure. Treat that path as
        // handled so the original exception is not reported a second time.
        return recovered || codecErrors.get() > errorsBeforeRecreate
    }

    private fun reportError(
        generation: Long,
        sequence: Long?,
        stage: String,
        error: Throwable,
    ) {
        codecErrors.incrementAndGet()
        decoderState = H264DecoderState.ERROR
        publishDiagnostics()
        if (error is MediaCodec.CodecException) {
            logCodecException(stage, sequence, error)
        } else {
            logger.transition(
                "[MediaCodecError] stage=$stage sequence=${sequence ?: "n/a"} " +
                    "exception=${error::class.java.name}:${error.message} " +
                    "codecName=${codecName ?: "unknown"}",
            )
        }
        logger.error(stage, sequence, error)
        try {
            errorCallback(generation, sequence, stage, error)
        } catch (callbackError: Throwable) {
            logger.error("decoder_error_callback", sequence, callbackError)
        }
    }

    private fun logCodecException(
        stage: String,
        sequence: Long?,
        error: MediaCodec.CodecException,
    ) {
        logger.transition(
            "[MediaCodecError] stage=$stage sequence=${sequence ?: "n/a"} " +
                "diagnosticInfo=${error.diagnosticInfo} errorCode=${error.errorCode} " +
                "recoverable=${error.isRecoverable} transient=${error.isTransient} " +
                "codecName=${codecName ?: "unknown"}",
        )
    }

    private fun publishDiagnostics() {
        try {
            diagnosticsListener(diagnostics())
        } catch (error: Throwable) {
            logger.error("decoder_diagnostics_callback", null, error)
        }
    }

    private companion object {
        // Once output starts, queue() drains non-blocking. This reserve absorbs short input
        // bursts without converting decoder pressure into a transport-level ClientFin.
        const val COMMAND_CAPACITY = MAX_QUEUED_H264_ACCESS_UNITS
        const val CLOSE_TIMEOUT_MILLIS = 1_000L
        const val QUEUE_METRICS_LOG_INTERVAL_NANOS = 5_000_000_000L
    }
}

internal const val MAX_QUEUED_H264_ACCESS_UNITS = 8
internal const val MAX_QUEUED_H264_BYTES = 2 * 1024 * 1024

/** Counts retained packet payloads, including the one currently being decoded. */
internal class H264RetentionBudget(
    private val maximumUnits: Int,
    private val maximumBytes: Int,
) {
    private val currentUnits = AtomicInteger()
    private val currentBytes = AtomicLong()
    private val highWatermarkBytes = AtomicLong()

    init {
        require(maximumUnits > 0)
        require(maximumBytes > 0)
    }

    fun canReserve(bytes: Int): Boolean = bytes in 1..maximumBytes &&
        currentUnits.get() < maximumUnits &&
        currentBytes.get() + bytes <= maximumBytes

    fun reserve(bytes: Int) {
        check(canReserve(bytes))
        currentUnits.incrementAndGet()
        val retained = currentBytes.addAndGet(bytes.toLong())
        highWatermarkBytes.accumulateAndGet(retained, ::maxOf)
    }

    fun release(bytes: Int) {
        currentUnits.decrementAndGet()
        currentBytes.addAndGet(-bytes.toLong())
    }

    fun units(): Int = currentUnits.get()

    fun bytes(): Long = currentBytes.get()

    fun highWatermarkBytes(): Long = highWatermarkBytes.get()
}

interface H264CodecAdapter : Closeable {
    fun queue(packet: H264VideoPacket)
}

fun interface H264CodecAdapterFactory {
    fun create(
        capability: H264DecoderCapability,
        session: VideoSessionInfo,
        surface: Surface,
        logger: VideoLogger,
        onFrameRendered: (Long, Double) -> Unit,
    ): H264CodecAdapter
}

object AndroidH264CodecAdapterFactory : H264CodecAdapterFactory {
    override fun create(
        capability: H264DecoderCapability,
        session: VideoSessionInfo,
        surface: Surface,
        logger: VideoLogger,
        onFrameRendered: (Long, Double) -> Unit,
    ): H264CodecAdapter = AndroidH264CodecAdapter(
        capability,
        session,
        surface,
        logger,
        onFrameRendered,
    )
}

private class AndroidH264CodecAdapter(
    capability: H264DecoderCapability,
    session: VideoSessionInfo,
    surface: Surface,
    private val logger: VideoLogger,
    private val onFrameRendered: (Long, Double) -> Unit,
) : H264CodecAdapter {
    private val codec = MediaCodec.createByCodecName(capability.codecName)
    private val presentation = HashMap<Long, PresentationTiming>()
    private val bufferInfo = MediaCodec.BufferInfo()
    private val performance = DecoderPerformanceMetrics(logger)
    private var closed = false
    private var hasProducedOutput = false

    init {
        val format = MediaFormat.createVideoFormat(
            AndroidH264DecoderCapabilityProvider.MIME_TYPE,
            session.width,
            session.height,
        )
        format.setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, VideoProtocol.MAX_H264_PAYLOAD_BYTES)
        if (VideoHelloNegotiator.shouldEnableLowLatency(Build.VERSION.SDK_INT, capability)) {
            format.setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
        }
        codec.setOnFrameRenderedListener(
            { _, presentationTimeUs, renderTimeNanos ->
                val timing = synchronized(presentation) {
                    presentation.remove(presentationTimeUs)
                } ?: return@setOnFrameRenderedListener
                val latencyMillis = (renderTimeNanos - timing.receivedAtNanos)
                    .coerceAtLeast(0L) / 1_000_000.0
                performance.frameRendered(timing, renderTimeNanos)
                onFrameRendered(presentationTimeUs, latencyMillis)
            },
            null,
        )
        codec.configure(format, surface, null, 0)
        codec.start()
    }

    override fun queue(packet: H264VideoPacket) {
        check(!closed) { "MediaCodec session is closed" }
        val inputIndex = codec.dequeueInputBuffer(INPUT_TIMEOUT_MICROS)
        if (inputIndex < 0) {
            throw H264DecoderBackpressureException("input_buffer_unavailable")
        }
        val input = codec.getInputBuffer(inputIndex)
            ?: throw VideoProtocolException("MediaCodec returned a null input buffer")
        if (input.capacity() < packet.payload.size) {
            throw VideoProtocolException(
                "H.264 access unit exceeds MediaCodec input capacity",
            )
        }
        input.clear()
        input.put(packet.payload)
        val flags = when {
            packet.header.isConfig -> MediaCodec.BUFFER_FLAG_CODEC_CONFIG
            packet.header.isKeyFrame -> MediaCodec.BUFFER_FLAG_KEY_FRAME
            else -> 0
        }
        val presentationTimeUs = packet.header.sequence
        val inputStartedNanos = System.nanoTime()
        if (!packet.header.isConfig) {
            synchronized(presentation) {
                presentation[presentationTimeUs] = PresentationTiming(
                    receivedAtNanos = packet.receivedAtNanos,
                    queuedToCodecNanos = inputStartedNanos,
                )
            }
        }
        try {
            codec.queueInputBuffer(
                inputIndex,
                0,
                packet.payload.size,
                presentationTimeUs,
                flags,
            )
        } catch (error: Throwable) {
            if (!packet.header.isConfig) {
                synchronized(presentation) { presentation.remove(presentationTimeUs) }
            }
            throw error
        }
        if (!packet.header.isConfig) {
            val queuedAtNanos = System.nanoTime()
            synchronized(presentation) {
                presentation[presentationTimeUs]?.let { timing ->
                    presentation[presentationTimeUs] = timing.copy(
                        queuedToCodecNanos = queuedAtNanos,
                    )
                }
            }
            performance.frameQueued(packet.receivedAtNanos, queuedAtNanos)
        }
        drainOutput(
            waitForFirstOutput = !packet.header.isConfig && !hasProducedOutput,
        )
    }

    override fun close() {
        if (closed) return
        closed = true
        try {
            codec.stop()
        } finally {
            codec.release()
            synchronized(presentation) { presentation.clear() }
        }
    }

    private fun drainOutput(waitForFirstOutput: Boolean) {
        var remainingWaitPolls = if (waitForFirstOutput) OUTPUT_WAIT_POLLS else 0
        while (true) {
            val timeoutMicros = if (remainingWaitPolls > 0) {
                OUTPUT_TIMEOUT_MICROS
            } else {
                0L
            }
            when (val outputIndex = codec.dequeueOutputBuffer(bufferInfo, timeoutMicros)) {
                MediaCodec.INFO_TRY_AGAIN_LATER -> {
                    if (remainingWaitPolls-- <= 1) return
                }
                MediaCodec.INFO_OUTPUT_FORMAT_CHANGED ->
                    logger.transition("H.264 decoder output: ${codec.outputFormat}")
                else -> if (outputIndex >= 0) {
                    hasProducedOutput = true
                    remainingWaitPolls = 0
                    val outputAvailableNanos = System.nanoTime()
                    synchronized(presentation) {
                        presentation[bufferInfo.presentationTimeUs]?.let { timing ->
                            presentation[bufferInfo.presentationTimeUs] = timing.copy(
                                outputAvailableNanos = outputAvailableNanos,
                            )
                            performance.outputAvailable(timing, outputAvailableNanos)
                        }
                    }
                    codec.releaseOutputBuffer(outputIndex, bufferInfo.size > 0)
                }
            }
        }
    }

    private companion object {
        const val INPUT_TIMEOUT_MICROS = 10_000L
        const val OUTPUT_TIMEOUT_MICROS = 10_000L
        const val OUTPUT_WAIT_POLLS = 5
    }
}

private data class PresentationTiming(
    val receivedAtNanos: Long,
    val queuedToCodecNanos: Long,
    val outputAvailableNanos: Long = 0L,
)

private class DecoderPerformanceMetrics(private val logger: VideoLogger) {
    private val lock = Any()
    private var startedNanos = System.nanoTime()
    private var received = 0L
    private var rendered = 0L
    private val queueCodec = ArrayList<Long>(256)
    private val decode = ArrayList<Long>(256)
    private val render = ArrayList<Long>(256)
    private val receiveToRender = ArrayList<Long>(256)

    fun frameQueued(receivedAtNanos: Long, queuedAtNanos: Long) = synchronized(lock) {
        received++
        queueCodec += (queuedAtNanos - receivedAtNanos).coerceAtLeast(0L)
    }

    fun outputAvailable(timing: PresentationTiming, outputAvailableNanos: Long) =
        synchronized(lock) {
            decode += (outputAvailableNanos - timing.queuedToCodecNanos).coerceAtLeast(0L)
        }

    fun frameRendered(timing: PresentationTiming, renderedAtNanos: Long) = synchronized(lock) {
        rendered++
        if (timing.outputAvailableNanos > 0L) {
            render += (renderedAtNanos - timing.outputAvailableNanos).coerceAtLeast(0L)
        }
        receiveToRender += (renderedAtNanos - timing.receivedAtNanos).coerceAtLeast(0L)
        maybeLog(renderedAtNanos)
    }

    private fun maybeLog(nowNanos: Long) {
        val elapsed = nowNanos - startedNanos
        if (elapsed < METRICS_INTERVAL_NANOS) return
        val seconds = elapsed / 1_000_000_000.0
        logger.performance(
            "receiveFps=${"%.1f".format(received / seconds)} " +
                "renderFps=${"%.1f".format(rendered / seconds)} " +
                "${format("queueCodec", queueCodec)} " +
                "${format("decode", decode)} " +
                "${format("render", render)} " +
                format("receiveToRender", receiveToRender),
        )
        received = 0
        rendered = 0
        queueCodec.clear()
        decode.clear()
        render.clear()
        receiveToRender.clear()
        startedNanos = nowNanos
    }

    private fun format(name: String, samples: MutableList<Long>): String {
        if (samples.isEmpty()) return "$name=n/a"
        samples.sort()
        val average = samples.sumOf(Long::toDouble) / samples.size / 1_000_000.0
        val p50 = samples[percentileIndex(samples.size, 0.50)] / 1_000_000.0
        val p95 = samples[percentileIndex(samples.size, 0.95)] / 1_000_000.0
        return "$name=${"%.2f".format(average)}/${"%.2f".format(p50)}/" +
            "${"%.2f".format(p95)}ms(avg/p50/p95)"
    }

    private fun percentileIndex(count: Int, percentile: Double): Int =
        (kotlin.math.ceil(count * percentile).toInt() - 1).coerceIn(0, count - 1)

    private companion object {
        const val METRICS_INTERVAL_NANOS = 5_000_000_000L
    }
}
