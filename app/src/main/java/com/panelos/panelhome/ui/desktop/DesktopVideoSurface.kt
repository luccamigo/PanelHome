package com.panelos.panelhome.ui.desktop

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.graphics.createBitmap
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.video.LatestVideoFrameSource
import com.panelos.panelhome.domain.video.VideoLogger

@Composable
fun DesktopVideoSurface(
    frameStore: LatestVideoFrameSource,
    frameSequence: Long,
    frameGeneration: Long,
    sourceWidth: Int,
    sourceHeight: Int,
    touchFeatureNegotiated: Boolean,
    touchGeneration: Long?,
    onTouch: (DesktopTouchEvent) -> Unit,
    onFrameDrawn: (Long, Long) -> Unit,
    onRenderError: (Long, Long, String, Throwable) -> Unit,
    modifier: Modifier = Modifier,
) {
    AndroidView(
        factory = { context ->
            DesktopSurfaceView(context).also {
                it.bind(
                    frameStore,
                    { sequence -> onFrameDrawn(frameGeneration, sequence) },
                    { sequence, stage, error ->
                        onRenderError(frameGeneration, sequence, stage, error)
                    },
                    touchGeneration,
                    sourceWidth,
                    sourceHeight,
                    onTouch,
                    touchFeatureNegotiated,
                )
            }
        },
        modifier = modifier,
        update = { surface ->
            surface.bind(
                frameStore,
                { sequence -> onFrameDrawn(frameGeneration, sequence) },
                { sequence, stage, error ->
                    onRenderError(frameGeneration, sequence, stage, error)
                },
                touchGeneration,
                sourceWidth,
                sourceHeight,
                onTouch,
                touchFeatureNegotiated,
            )
            surface.showFrame(frameSequence)
        },
    )
}

internal class DesktopSurfaceView(context: Context) : SurfaceView(context), SurfaceHolder.Callback {
    private val logger: VideoLogger = DesktopVideoLogger
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG or Paint.FILTER_BITMAP_FLAG)
    private val converter = BgraToArgbConverter()
    private val presentation = DesktopFramePresentationLifecycle()
    private var bitmap: Bitmap? = null
    private var frameWidth = 0
    private var frameHeight = 0
    private var frameStore: LatestVideoFrameSource? = null
    private var onFrameDrawn: (Long) -> Unit = {}
    private var onRenderError: (Long, String, Throwable) -> Unit = { _, _, _ -> }
    private var bitmapDebugCount = 0
    private var drawnDebugCount = 0
    private var renderErrorLogCount = 0
    private val touchHandler = DesktopTouchSurfaceHandler()

    init {
        holder.addCallback(this)
        // SurfaceView owns a separate composited Surface. A View background can cover its pixels;
        // letterboxing is cleared explicitly on the Surface Canvas instead.
        isClickable = true
        isFocusable = false
        setOnTouchListener(touchHandler::onTouch)
    }

    fun bind(
        store: LatestVideoFrameSource,
        onFrameDrawn: (Long) -> Unit,
        onRenderError: (Long, String, Throwable) -> Unit,
        touchGeneration: Long?,
        sourceWidth: Int,
        sourceHeight: Int,
        onTouch: (DesktopTouchEvent) -> Unit,
        touchFeatureNegotiated: Boolean,
    ) {
        frameStore = store
        this.onFrameDrawn = onFrameDrawn
        this.onRenderError = onRenderError
        touchHandler.bind(
            touchGeneration,
            sourceWidth,
            sourceHeight,
            onTouch,
            touchFeatureNegotiated,
            desktopRunning = true,
        )
    }

    fun showFrame(sequence: Long) {
        presentation.onFrameAvailable(sequence)?.let(::renderLatest)
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
        logger.transition("Video Surface available")
        presentation.onSurfaceAvailable()?.let(::renderLatest)
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        logger.transition("Video Surface changed: ${width}x$height format=$format")
        presentation.onSurfaceChanged()?.let(::renderLatest)
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        touchHandler.cancelActive()
        presentation.onSurfaceDestroyed()
        logger.transition("Video Surface destroyed")
    }

    private fun renderLatest(sequence: Long) {
        if (!holder.surface.isValid || sequence <= 0L) return

        val prepared = try {
            frameStore?.withLatest(sequence) { frame ->
                ensureBitmap(frame.width, frame.height)
                val argb = converter.convert(frame.pixels, frame.width, frame.height)
                bitmap?.setPixels(argb, 0, frame.width, 0, 0, frame.width, frame.height)
                frameWidth = frame.width
                frameHeight = frame.height
                if (bitmapDebugCount++ < DEBUG_FRAME_LIMIT) {
                    logger.debug(describeBitmap(sequence, argb, frame.width, frame.height))
                }
                true
            } ?: false
        } catch (error: Exception) {
            reportRenderError(sequence, "bitmap_update", error)
            return
        }
        if (!prepared) return

        val currentBitmap = bitmap ?: return
        val canvas = try {
            holder.lockCanvas()
        } catch (error: Exception) {
            reportRenderError(sequence, "surface_lock", error)
            return
        }
        if (canvas == null) {
            reportRenderError(
                sequence,
                "surface_lock",
                IllegalStateException("SurfaceHolder.lockCanvas returned null"),
            )
            return
        }
        val canvasWidth = canvas.width
        val canvasHeight = canvas.height

        var drawSucceeded = false
        var postSucceeded = false
        try {
            drawLetterboxed(canvas, currentBitmap, frameWidth, frameHeight)
            drawSucceeded = true
        } catch (error: Exception) {
            reportRenderError(sequence, "canvas_draw", error)
        } finally {
            try {
                holder.unlockCanvasAndPost(canvas)
                postSucceeded = true
            } catch (error: Exception) {
                reportRenderError(sequence, "surface_post", error)
            }
        }

        if (drawSucceeded && postSucceeded) {
            presentation.onDrawSucceeded(sequence)
            if (drawnDebugCount++ < DEBUG_FRAME_LIMIT) {
                logger.debug("Frame drawn: sequence=$sequence canvas=${canvasWidth}x$canvasHeight")
            }
            try {
                onFrameDrawn(sequence)
            } catch (error: Exception) {
                logger.error("frame_drawn_callback", sequence, error)
            }
        }
    }

    private fun reportRenderError(sequence: Long, stage: String, error: Throwable) {
        presentation.onDrawFailed(sequence)
        if (renderErrorLogCount < DEBUG_ERROR_LIMIT) {
            logger.error(stage, sequence, error)
        } else if (renderErrorLogCount == DEBUG_ERROR_LIMIT) {
            logger.transition("Further video presentation errors are suppressed for this Surface")
        }
        renderErrorLogCount++
        try {
            onRenderError(sequence, stage, error)
        } catch (callbackError: Exception) {
            logger.error("render_error_callback", sequence, callbackError)
        }
    }

    private fun ensureBitmap(width: Int, height: Int) {
        val current = bitmap
        if (current?.width == width && current.height == height) return
        current?.recycle()
        bitmap = createBitmap(width, height, Bitmap.Config.ARGB_8888)
    }

    private fun drawLetterboxed(
        canvas: Canvas,
        source: Bitmap,
        sourceWidth: Int,
        sourceHeight: Int,
    ) {
        canvas.drawColor(Color.BLACK)
        val target = aspectFitRect(
            sourceWidth = sourceWidth,
            sourceHeight = sourceHeight,
            targetWidth = canvas.width,
            targetHeight = canvas.height,
        )
        canvas.drawBitmap(
            source,
            null,
            RectF(target.left, target.top, target.right, target.bottom),
            paint,
        )
    }

    private fun describeBitmap(
        sequence: Long,
        argb: IntArray,
        width: Int,
        height: Int,
    ): String {
        val center = ((height / 2) * width) + (width / 2)
        return buildString {
            append("Android bitmap:")
            append("\nsequence=").append(sequence)
            append("\nsize=").append(width).append('x').append(height)
            append("\npixel(0,0)=0x").append(argb.first().toUInt().toString(16).padStart(8, '0'))
            append("\npixel(center)=0x")
                .append(argb[center].toUInt().toString(16).padStart(8, '0'))
            append("\npixel(last)=0x")
                .append(argb.last().toUInt().toString(16).padStart(8, '0'))
        }
    }

    private companion object {
        const val DEBUG_FRAME_LIMIT = 3
        const val DEBUG_ERROR_LIMIT = 3
    }
}

internal class BgraToArgbConverter {
    private var argb = IntArray(0)

    fun convert(bgra: ByteArray, width: Int, height: Int): IntArray {
        val pixelCount = Math.multiplyExact(width, height)
        val byteCount = Math.multiplyExact(pixelCount, 4)
        require(bgra.size >= byteCount) { "BGRA payload is truncated" }
        if (argb.size != pixelCount) argb = IntArray(pixelCount)

        var source = 0
        for (pixel in 0 until pixelCount) {
            val blue = bgra[source].toInt() and 0xff
            val green = bgra[source + 1].toInt() and 0xff
            val red = bgra[source + 2].toInt() and 0xff
            // The desktop preview is always opaque. Source swapchains may leave alpha undefined or
            // zero; preserving it would make otherwise valid RGB pixels transparent on Android.
            argb[pixel] = OPAQUE_ALPHA or (red shl 16) or (green shl 8) or blue
            source += 4
        }
        return argb
    }

    private companion object {
        const val OPAQUE_ALPHA = -0x1000000
    }
}

/** Pure state machine used to make Surface/frame ordering and recreation independently testable. */
internal class DesktopFramePresentationLifecycle {
    private var surfaceAvailable = false
    private var latestSequence = 0L
    private var drawnSequence = 0L
    private var retryPending = false

    fun onFrameAvailable(sequence: Long): Long? {
        if (sequence <= 0L || sequence < latestSequence) return null
        if (sequence > latestSequence) {
            latestSequence = sequence
            retryPending = true
        }
        return sequenceToRender()
    }

    fun onSurfaceAvailable(): Long? {
        surfaceAvailable = true
        drawnSequence = 0L
        retryPending = latestSequence > 0L
        return sequenceToRender()
    }

    fun onSurfaceChanged(): Long? {
        if (!surfaceAvailable) return null
        retryPending = latestSequence > 0L
        return sequenceToRender()
    }

    fun onSurfaceDestroyed() {
        surfaceAvailable = false
        drawnSequence = 0L
        retryPending = latestSequence > 0L
    }

    fun onDrawSucceeded(sequence: Long) {
        if (sequence == latestSequence) {
            drawnSequence = sequence
            retryPending = false
        }
    }

    fun onDrawFailed(sequence: Long) {
        if (sequence == latestSequence) retryPending = true
    }

    private fun sequenceToRender(): Long? =
        latestSequence.takeIf {
            surfaceAvailable && it > 0L && (retryPending || drawnSequence != it)
        }
}

internal data class AspectFitRect(
    val left: Float,
    val top: Float,
    val right: Float,
    val bottom: Float,
)

internal fun aspectFitRect(
    sourceWidth: Int,
    sourceHeight: Int,
    targetWidth: Int,
    targetHeight: Int,
): AspectFitRect {
    require(sourceWidth > 0 && sourceHeight > 0)
    require(targetWidth >= 0 && targetHeight >= 0)
    if (targetWidth == 0 || targetHeight == 0) return AspectFitRect(0f, 0f, 0f, 0f)
    val scale = minOf(
        targetWidth.toFloat() / sourceWidth,
        targetHeight.toFloat() / sourceHeight,
    )
    val width = sourceWidth * scale
    val height = sourceHeight * scale
    val left = (targetWidth - width) / 2f
    val top = (targetHeight - height) / 2f
    return AspectFitRect(left, top, left + width, top + height)
}
