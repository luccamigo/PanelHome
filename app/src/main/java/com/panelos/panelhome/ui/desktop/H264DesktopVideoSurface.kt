package com.panelos.panelhome.ui.desktop

import android.content.Context
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.video.VideoSurfaceController
import com.panelos.panelhome.domain.video.VideoSurfaceLifecyclePolicy

@Composable
fun H264DesktopVideoSurface(
    decoder: VideoSurfaceController,
    generation: Long,
    sourceWidth: Int,
    sourceHeight: Int,
    touchFeatureNegotiated: Boolean,
    touchGeneration: Long?,
    onTouch: (DesktopTouchEvent) -> Unit,
    modifier: Modifier = Modifier,
) {
    AndroidView(
        factory = { context ->
            H264SurfaceView(context).also {
                it.bind(
                    decoder,
                    generation,
                    touchFeatureNegotiated,
                    touchGeneration,
                    sourceWidth,
                    sourceHeight,
                    onTouch,
                )
            }
        },
        update = { view ->
            view.bind(
                decoder,
                generation,
                touchFeatureNegotiated,
                touchGeneration,
                sourceWidth,
                sourceHeight,
                onTouch,
            )
        },
        modifier = modifier,
    )
}

internal class H264SurfaceView(context: Context) :
    SurfaceView(context),
    SurfaceHolder.Callback {
    private var decoder: VideoSurfaceController? = null
    private var generation = 0L
    private val touchHandler = DesktopTouchSurfaceHandler()

    init {
        holder.addCallback(this)
        // Keep the First Pixels black-screen fix: the Surface itself is the
        // video layer, and an opaque View background would cover MediaCodec.
        isClickable = true
        isFocusable = false
        setOnTouchListener(touchHandler::onTouch)
    }

    fun bind(
        decoder: VideoSurfaceController,
        generation: Long,
        touchFeatureNegotiated: Boolean,
        touchGeneration: Long?,
        sourceWidth: Int,
        sourceHeight: Int,
        onTouch: (DesktopTouchEvent) -> Unit,
    ) {
        touchHandler.bind(
            touchGeneration,
            sourceWidth,
            sourceHeight,
            onTouch,
            touchFeatureNegotiated,
            desktopRunning = true,
        )
        if (this.decoder === decoder && this.generation == generation) return
        if (holder.surface.isValid && this.generation > 0L) {
            this.decoder?.detachSurface(this.generation, holder.surface)
        }
        this.decoder = decoder
        this.generation = generation
        if (holder.surface.isValid) {
            decoder.attachSurface(generation, holder.surface)
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        DesktopVideoLogger.transition(
            "[VideoSurface] event=created desktopGeneration=$generation " +
                "videoSessionGeneration=$generation " +
                "surface=${System.identityHashCode(holder.surface)} " +
                "valid=${holder.surface.isValid} " +
                "size=${holder.surfaceFrame.width()}x${holder.surfaceFrame.height()}",
        )
        decoder?.attachSurface(generation, holder.surface)
    }

    override fun surfaceChanged(
        holder: SurfaceHolder,
        format: Int,
        width: Int,
        height: Int,
    ) {
        DesktopVideoLogger.transition(
            "[VideoSurface] event=changed desktopGeneration=$generation " +
                "videoSessionGeneration=$generation " +
                "surface=${System.identityHashCode(holder.surface)} " +
                "valid=${holder.surface.isValid} size=${width}x$height format=$format",
        )
        decoder?.attachSurface(generation, holder.surface)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        touchHandler.cancelActive()
        val transportAction = VideoSurfaceLifecyclePolicy.onSurfaceDestroyed(
            desktopSessionActive = generation > 0L,
        )
        DesktopVideoLogger.transition(
            "[VideoSurface] event=destroyed desktopGeneration=$generation " +
                "videoSessionGeneration=$generation " +
                "surface=${System.identityHashCode(holder.surface)} " +
                "valid=${holder.surface.isValid} " +
                "decoderState=${decoder?.surfaceDiagnostics()?.decoderStateLogName ?: "unknown"} " +
                "transportAction=${transportAction.name.lowercase()}",
        )
        decoder?.detachSurface(generation, holder.surface)
    }
}
