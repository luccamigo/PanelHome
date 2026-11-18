package com.panelos.panelhome.domain.video

import android.view.Surface

/** UI-facing surface lifecycle contract, independent of the decoder implementation. */
interface VideoSurfaceController {
    fun attachSurface(generation: Long, surface: Surface)

    fun detachSurface(generation: Long, surface: Surface)

    fun surfaceDiagnostics(): VideoSurfaceDiagnostics = VideoSurfaceDiagnostics()
}

data class VideoSurfaceDiagnostics(
    val decoderStateLogName: String = "unknown",
)

/** Read-only view consumed by the desktop renderer. */
interface LatestVideoFrameSource {
    fun <T> withLatest(
        expectedSequence: Long? = null,
        block: (LatestVideoFrame) -> T,
    ): T?
}

/** Pixel storage is owned by the source and must not escape [LatestVideoFrameSource.withLatest]. */
data class LatestVideoFrame(
    val sequence: Long,
    val captureTimestampMicros: Long,
    val width: Int,
    val height: Int,
    val pixels: ByteArray,
)

interface VideoLogger {
    fun transition(message: String)

    fun performance(message: String) = transition("[VideoPerf] $message")

    fun debug(message: String)

    fun error(stage: String, sequence: Long?, error: Throwable)
}

enum class VideoSurfaceTransportAction {
    KEEP_SESSION,
}

/** Recreating the render target must not tear down the transport session. */
object VideoSurfaceLifecyclePolicy {
    fun onSurfaceDestroyed(desktopSessionActive: Boolean): VideoSurfaceTransportAction =
        VideoSurfaceTransportAction.KEEP_SESSION
}
