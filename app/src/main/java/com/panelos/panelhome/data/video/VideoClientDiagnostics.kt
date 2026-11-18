package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import java.util.concurrent.atomic.AtomicBoolean

enum class VideoClientCloseReason(val logName: String) {
    DESKTOP_EXIT("desktop_exit"),
    SURFACE_DESTROYED("surface_destroyed"),
    SURFACE_RECREATED("surface_recreated"),
    DECODER_ERROR("decoder_error"),
    PROTOCOL_ERROR("protocol_error"),
    READ_EOF("read_eof"),
    READ_EXCEPTION("read_exception"),
    WRITE_EXCEPTION("write_exception"),
    CANCELLED("cancelled"),
    RECONNECT("reconnect"),
    FIRST_FRAME_TIMEOUT("first_frame_timeout"),
    STALL_TIMEOUT("stall_timeout"),
    APP_STOP("app_stop"),
}

data class VideoClientRuntimeState(
    val surfaceState: String = "unknown",
    val decoderState: String = "unknown",
    val renderedFrames: Long = 0,
    val codecErrors: Long = 0,
    val lastRenderedAtNanos: Long = 0,
)

internal data class VideoClientMetricsSnapshot(
    val packetsReceived: Long,
    val bytesReceived: Long,
    val accessUnitsQueued: Long,
    val renderedFrames: Long,
    val codecErrors: Long,
    val lastNetworkPacketAgeMs: Long?,
    val lastQueuedAccessUnitAgeMs: Long?,
    val lastRenderedFrameAgeMs: Long?,
)

internal class VideoClientSessionMetrics(
    private val clockNanos: () -> Long = System::nanoTime,
) {
    private val closeLogged = AtomicBoolean(false)
    private var startedAtNanos = clockNanos()
    private var lastPeriodicLogNanos = startedAtNanos
    private var packetsReceived = 0L
    private var bytesReceived = 0L
    private var accessUnitsQueued = 0L
    private var lastNetworkPacketAtNanos = 0L
    private var lastQueuedAccessUnitAtNanos = 0L

    @Synchronized
    fun packetReceived(bytes: Int) {
        packetsReceived++
        bytesReceived += bytes.coerceAtLeast(0)
        lastNetworkPacketAtNanos = clockNanos()
    }

    @Synchronized
    fun accessUnitQueued() {
        accessUnitsQueued++
        lastQueuedAccessUnitAtNanos = clockNanos()
    }

    @Synchronized
    fun snapshot(runtime: VideoClientRuntimeState): VideoClientMetricsSnapshot {
        val now = clockNanos()
        return VideoClientMetricsSnapshot(
            packetsReceived = packetsReceived,
            bytesReceived = bytesReceived,
            accessUnitsQueued = accessUnitsQueued,
            renderedFrames = runtime.renderedFrames,
            codecErrors = runtime.codecErrors,
            lastNetworkPacketAgeMs = ageMillis(now, lastNetworkPacketAtNanos),
            lastQueuedAccessUnitAgeMs = ageMillis(now, lastQueuedAccessUnitAtNanos),
            lastRenderedFrameAgeMs = ageMillis(now, runtime.lastRenderedAtNanos),
        )
    }

    fun maybeLog(logger: VideoLogger, runtime: VideoClientRuntimeState) {
        val now = clockNanos()
        val shouldLog = synchronized(this) {
            if (now - lastPeriodicLogNanos < METRICS_INTERVAL_NANOS) {
                false
            } else {
                lastPeriodicLogNanos = now
                true
            }
        }
        if (!shouldLog) return
        val snapshot = snapshot(runtime)
        val elapsedSeconds = ((now - startedAtNanos).coerceAtLeast(1L)) /
            1_000_000_000.0
        logger.performance(
            "[VideoClientMetrics] packetsReceived=${snapshot.packetsReceived} " +
                "bytesReceived=${snapshot.bytesReceived} " +
                "accessUnitsQueued=${snapshot.accessUnitsQueued} " +
                "framesRendered=${snapshot.renderedFrames} " +
                "codecErrors=${snapshot.codecErrors} " +
                "networkFps=${"%.1f".format(snapshot.packetsReceived / elapsedSeconds)} " +
                snapshot.ages(),
        )
    }

    fun logClose(
        logger: VideoLogger,
        reason: VideoClientCloseReason,
        exception: Throwable?,
        runtime: VideoClientRuntimeState,
    ) {
        if (!closeLogged.compareAndSet(false, true)) return
        val snapshot = snapshot(runtime)
        logger.transition(
            "[VideoClientClose] reason=${reason.logName} " +
                "exception=${exception?.let { it::class.java.simpleName + ":" + it.message } ?: "none"} " +
                "surfaceState=${runtime.surfaceState} " +
                "decoderState=${runtime.decoderState} " +
                "receivedFrames=${snapshot.packetsReceived} " +
                "renderedFrames=${snapshot.renderedFrames} " +
                "lastFrameAgeMs=${snapshot.lastRenderedFrameAgeMs ?: "n/a"} " +
                "bytesReceived=${snapshot.bytesReceived} " +
                "accessUnitsQueued=${snapshot.accessUnitsQueued} " +
                "codecErrors=${snapshot.codecErrors} " +
                snapshot.ages(),
        )
    }

    private fun VideoClientMetricsSnapshot.ages(): String =
        "lastNetworkPacketAgeMs=${lastNetworkPacketAgeMs ?: "n/a"} " +
            "lastQueuedAccessUnitAgeMs=${lastQueuedAccessUnitAgeMs ?: "n/a"} " +
            "lastRenderedFrameAgeMs=${lastRenderedFrameAgeMs ?: "n/a"}"

    private fun ageMillis(now: Long, timestamp: Long): Long? =
        timestamp.takeIf { it > 0L }
            ?.let { ((now - it).coerceAtLeast(0L)) / 1_000_000L }

    private companion object {
        const val METRICS_INTERVAL_NANOS = 5_000_000_000L
    }
}
