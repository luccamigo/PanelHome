package com.panelos.panelhome.ui.desktop

import android.view.MotionEvent
import android.view.View
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import java.util.Locale

internal data class DesktopContentRect(
    val left: Float,
    val top: Float,
    val right: Float,
    val bottom: Float,
) {
    val isEmpty: Boolean
        get() = right < left || bottom < top

    fun contains(x: Float, y: Float): Boolean =
        !isEmpty && x in left..right && y in top..bottom

    fun normalize(x: Float, y: Float): Pair<Double, Double> {
        require(!isEmpty)
        val normalizedX = if (right == left) {
            0.0
        } else {
            ((x.coerceIn(left, right) - left) / (right - left)).toDouble()
        }
        val normalizedY = if (bottom == top) {
            0.0
        } else {
            ((y.coerceIn(top, bottom) - top) / (bottom - top)).toDouble()
        }
        return normalizedX.coerceIn(0.0, 1.0) to normalizedY.coerceIn(0.0, 1.0)
    }
}

internal fun desktopContentRect(
    sourceWidth: Int,
    sourceHeight: Int,
    viewWidth: Int,
    viewHeight: Int,
): DesktopContentRect {
    val fitted = aspectFitRect(sourceWidth, sourceHeight, viewWidth, viewHeight)
    if (fitted.right <= fitted.left || fitted.bottom <= fitted.top) {
        return DesktopContentRect(0f, 0f, -1f, -1f)
    }

    // RectF drawing and MediaCodec scale-to-fit use exclusive right/bottom
    // edges. Motion coordinates address pixels from 0 through size - 1, so
    // expose inclusive endpoints to map the four visible border pixels to 0/1.
    return DesktopContentRect(
        fitted.left,
        fitted.top,
        (fitted.right - 1f).coerceAtLeast(fitted.left),
        (fitted.bottom - 1f).coerceAtLeast(fitted.top),
    )
}

internal enum class DesktopMotionAction {
    DOWN,
    MOVE,
    UP,
    CANCEL,
    OTHER,
}

internal class DesktopTouchGestureMapper(
    private val onTouch: (DesktopTouchEvent) -> Unit,
    private val onDebug: (String) -> Unit = {},
) {
    private var generation: Long? = null
    private var sourceWidth = 0
    private var sourceHeight = 0
    private var featureNegotiated = false
    private var desktopRunning = false
    private var active = false
    private var debugEvents = 0

    fun bind(
        generation: Long?,
        sourceWidth: Int,
        sourceHeight: Int,
        featureNegotiated: Boolean = generation != null,
        desktopRunning: Boolean = true,
    ) {
        if (this.generation != generation && active) {
            emitCancel()
        }
        this.generation = generation?.takeIf { it > 0 }
        this.sourceWidth = sourceWidth
        this.sourceHeight = sourceHeight
        this.featureNegotiated = featureNegotiated
        this.desktopRunning = desktopRunning
    }

    fun handle(
        action: DesktopMotionAction,
        pointerCount: Int,
        x: Float,
        y: Float,
        viewWidth: Int,
        viewHeight: Int,
    ): Boolean {
        val surfaceAvailable = sourceWidth > 0 && sourceHeight > 0 &&
            viewWidth > 0 && viewHeight > 0
        val content = if (surfaceAvailable) {
            desktopContentRect(sourceWidth, sourceHeight, viewWidth, viewHeight)
        } else {
            null
        }
        val insideContent = content?.contains(x, y) == true
        if (action == DesktopMotionAction.DOWN) {
            debug(
                "DOWN feature=$featureNegotiated desktopRunning=$desktopRunning " +
                    "generation=${generation ?: "missing"} surfaceAvailable=$surfaceAvailable " +
                    "contentRect=${content?.debugValue() ?: "unavailable"} " +
                    "insideContentRect=$insideContent pointerCount=$pointerCount",
            )
        }
        if (!featureNegotiated) {
            if (action == DesktopMotionAction.DOWN) debug("REJECT reason=feature_missing")
            return false
        }
        if (!desktopRunning) {
            if (action == DesktopMotionAction.DOWN) debug("REJECT reason=desktop_not_running")
            return false
        }
        val currentGeneration = generation
        if (currentGeneration == null) {
            if (action == DesktopMotionAction.DOWN) debug("REJECT reason=generation_missing")
            return false
        }
        if (!surfaceAvailable || content == null) {
            if (action == DesktopMotionAction.DOWN) debug("REJECT reason=surface_unavailable")
            return false
        }
        if (pointerCount > 1) {
            val wasActive = active
            if (wasActive) emitCancel()
            debug("REJECT reason=secondary_pointer pointerCount=$pointerCount")
            return wasActive
        }
        return when (action) {
            DesktopMotionAction.DOWN -> {
                if (active) {
                    debug("REJECT reason=duplicate_down")
                    false
                } else if (!insideContent) {
                    debug("REJECT reason=outside_content")
                    false
                } else {
                    active = true
                    val normalized = content.normalize(x, y)
                    onTouch(
                        DesktopTouchEvent(
                            TouchCommandAction.DOWN,
                            x = normalized.first,
                            y = normalized.second,
                            generation = currentGeneration,
                        ),
                    )
                    true
                }
            }

            DesktopMotionAction.MOVE -> {
                if (!active) {
                    false
                } else {
                    val normalized = content.normalize(x, y)
                    onTouch(
                        DesktopTouchEvent(
                            TouchCommandAction.MOVE,
                            x = normalized.first,
                            y = normalized.second,
                            generation = currentGeneration,
                        ),
                    )
                    true
                }
            }

            DesktopMotionAction.UP -> {
                if (!active) {
                    false
                } else {
                    val normalized = content.normalize(x, y)
                    active = false
                    onTouch(
                        DesktopTouchEvent(
                            TouchCommandAction.UP,
                            x = normalized.first,
                            y = normalized.second,
                            generation = currentGeneration,
                        ),
                    )
                    true
                }
            }

            DesktopMotionAction.CANCEL -> {
                if (active) {
                    emitCancel()
                    true
                } else {
                    false
                }
            }

            DesktopMotionAction.OTHER -> false
        }
    }

    fun cancelActive() {
        if (active) emitCancel()
    }

    private fun emitCancel() {
        val currentGeneration = generation
        active = false
        if (currentGeneration != null) {
            onTouch(
                DesktopTouchEvent(
                    TouchCommandAction.CANCEL,
                    generation = currentGeneration,
                ),
            )
        }
    }

    private fun debug(message: String) {
        if (debugEvents++ < DEBUG_EVENT_LIMIT) {
            onDebug("[TouchDebug] $message")
        }
    }

    private fun DesktopContentRect.debugValue(): String =
        "(%.1f,%.1f..%.1f,%.1f)".format(Locale.US, left, top, right, bottom)

    private companion object {
        const val DEBUG_EVENT_LIMIT = 20
    }
}

internal class DesktopTouchSurfaceHandler {
    private var onTouch: (DesktopTouchEvent) -> Unit = {}
    private val mapper = DesktopTouchGestureMapper(
        onTouch = { event -> onTouch(event) },
        onDebug = DesktopVideoLogger::debug,
    )
    private var rawDebugEvents = 0
    private var rawMoveEvents = 0

    fun bind(
        generation: Long?,
        sourceWidth: Int,
        sourceHeight: Int,
        onTouch: (DesktopTouchEvent) -> Unit,
        featureNegotiated: Boolean,
        desktopRunning: Boolean,
    ) {
        this.onTouch = onTouch
        mapper.bind(
            generation,
            sourceWidth,
            sourceHeight,
            featureNegotiated,
            desktopRunning,
        )
    }

    fun onTouch(view: View, event: MotionEvent): Boolean {
        val action = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> DesktopMotionAction.DOWN
            MotionEvent.ACTION_MOVE -> DesktopMotionAction.MOVE
            MotionEvent.ACTION_UP -> DesktopMotionAction.UP
            MotionEvent.ACTION_CANCEL -> DesktopMotionAction.CANCEL
            else -> DesktopMotionAction.OTHER
        }
        val shouldLogMove = action != DesktopMotionAction.MOVE || rawMoveEvents++ < 3
        if (shouldLogMove && rawDebugEvents++ < RAW_DEBUG_EVENT_LIMIT) {
            DesktopVideoLogger.debug(
                "[TouchDebug] MotionEvent $action x=${"%.1f".format(Locale.US, event.x)} " +
                    "y=${"%.1f".format(Locale.US, event.y)} pointers=${event.pointerCount} " +
                    "view=${view.width}x${view.height}",
            )
        }
        return mapper.handle(
            action = action,
            pointerCount = event.pointerCount,
            x = event.x,
            y = event.y,
            viewWidth = view.width,
            viewHeight = view.height,
        )
    }

    fun cancelActive() = mapper.cancelActive()

    private companion object {
        const val RAW_DEBUG_EVENT_LIMIT = 12
    }
}
