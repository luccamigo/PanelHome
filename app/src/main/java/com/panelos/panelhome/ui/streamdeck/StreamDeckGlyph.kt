package com.panelos.panelhome.ui.streamdeck

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke

/**
 * Glyph names PanelHost derives from the action kind. They are visual metadata
 * only: knowing a button draws a keyboard says nothing about which keys it
 * presses.
 */
internal object StreamDeckGlyphs {
    const val APP = "app"
    const val LINK = "link"
    const val KEYBOARD = "keyboard"
    const val MEDIA_PLAY_PAUSE = "media-play-pause"
    const val MEDIA_PREVIOUS = "media-previous"
    const val MEDIA_NEXT = "media-next"
    const val VOLUME_UP = "volume-up"
    const val VOLUME_DOWN = "volume-down"
    const val VOLUME_MUTE = "volume-mute"
    const val PANEL_MODE = "panel-mode"
}

/**
 * Draws a button's fallback icon.
 *
 * The shapes are drawn rather than shipped as a font or an icon pack: the set
 * is small and fixed, the result scales to any button size, and it keeps the
 * panel looking like PanelOS instead of like a stock icon sheet.
 */
@Composable
internal fun StreamDeckGlyph(
    glyph: String?,
    color: Color,
    modifier: Modifier = Modifier,
) {
    Canvas(modifier = modifier) {
        val stroke = Stroke(width = size.minDimension * 0.075f)
        when (glyph) {
            StreamDeckGlyphs.MEDIA_PLAY_PAUSE -> drawPlay(color)
            StreamDeckGlyphs.MEDIA_PREVIOUS -> drawSkip(color, forward = false)
            StreamDeckGlyphs.MEDIA_NEXT -> drawSkip(color, forward = true)
            StreamDeckGlyphs.VOLUME_UP -> drawSpeaker(color, stroke, Badge.PLUS)
            StreamDeckGlyphs.VOLUME_DOWN -> drawSpeaker(color, stroke, Badge.MINUS)
            StreamDeckGlyphs.VOLUME_MUTE -> drawSpeaker(color, stroke, Badge.CROSS)
            StreamDeckGlyphs.KEYBOARD -> drawKeyboard(color, stroke)
            StreamDeckGlyphs.LINK -> drawLink(color, stroke)
            StreamDeckGlyphs.PANEL_MODE -> drawPanel(color, stroke)
            StreamDeckGlyphs.APP -> drawApp(color, stroke)
            else -> drawDot(color, stroke)
        }
    }
}

private enum class Badge { PLUS, MINUS, CROSS }

private fun DrawScope.drawPlay(color: Color) {
    val inset = size.minDimension * 0.24f
    val path = Path().apply {
        moveTo(inset * 1.2f, inset)
        lineTo(size.width - inset, size.height / 2f)
        lineTo(inset * 1.2f, size.height - inset)
        close()
    }
    drawPath(path, color)
}

private fun DrawScope.drawSkip(color: Color, forward: Boolean) {
    val inset = size.minDimension * 0.24f
    val barWidth = size.minDimension * 0.11f
    val path = Path().apply {
        if (forward) {
            moveTo(inset, inset)
            lineTo(size.width - inset - barWidth * 1.6f, size.height / 2f)
            lineTo(inset, size.height - inset)
        } else {
            moveTo(size.width - inset, inset)
            lineTo(inset + barWidth * 1.6f, size.height / 2f)
            lineTo(size.width - inset, size.height - inset)
        }
        close()
    }
    drawPath(path, color)
    drawRect(
        color = color,
        topLeft = Offset(
            x = if (forward) size.width - inset - barWidth else inset,
            y = inset,
        ),
        size = Size(barWidth, size.height - inset * 2f),
    )
}

private fun DrawScope.drawSpeaker(color: Color, stroke: Stroke, badge: Badge) {
    val unit = size.minDimension
    val left = unit * 0.16f
    val coneRight = unit * 0.46f
    val path = Path().apply {
        moveTo(left, size.height * 0.38f)
        lineTo(left + unit * 0.13f, size.height * 0.38f)
        lineTo(coneRight, size.height * 0.2f)
        lineTo(coneRight, size.height * 0.8f)
        lineTo(left + unit * 0.13f, size.height * 0.62f)
        lineTo(left, size.height * 0.62f)
        close()
    }
    drawPath(path, color)

    val center = Offset(unit * 0.72f, size.height / 2f)
    val arm = unit * 0.13f
    when (badge) {
        Badge.PLUS -> {
            drawLine(color, center.copy(x = center.x - arm), center.copy(x = center.x + arm), stroke.width)
            drawLine(color, center.copy(y = center.y - arm), center.copy(y = center.y + arm), stroke.width)
        }

        Badge.MINUS -> drawLine(
            color,
            center.copy(x = center.x - arm),
            center.copy(x = center.x + arm),
            stroke.width,
        )

        Badge.CROSS -> {
            drawLine(
                color,
                Offset(center.x - arm, center.y - arm),
                Offset(center.x + arm, center.y + arm),
                stroke.width,
            )
            drawLine(
                color,
                Offset(center.x + arm, center.y - arm),
                Offset(center.x - arm, center.y + arm),
                stroke.width,
            )
        }
    }
}

private fun DrawScope.drawKeyboard(color: Color, stroke: Stroke) {
    val inset = size.minDimension * 0.16f
    val body = Rect(
        left = inset,
        top = size.height * 0.3f,
        right = size.width - inset,
        bottom = size.height * 0.7f,
    )
    drawRoundRectOutline(body, color, stroke, size.minDimension * 0.08f)

    val keyRadius = size.minDimension * 0.032f
    val keyY = body.top + body.height * 0.36f
    repeat(3) { index ->
        drawCircle(
            color = color,
            radius = keyRadius,
            center = Offset(body.left + body.width * (0.26f + index * 0.24f), keyY),
        )
    }
    drawLine(
        color,
        Offset(body.left + body.width * 0.28f, body.bottom - body.height * 0.26f),
        Offset(body.right - body.width * 0.28f, body.bottom - body.height * 0.26f),
        stroke.width,
    )
}

private fun DrawScope.drawLink(color: Color, stroke: Stroke) {
    val unit = size.minDimension
    val height = unit * 0.22f
    val width = unit * 0.4f
    drawRoundRectOutline(
        Rect(
            left = unit * 0.12f,
            top = size.height / 2f - height / 2f - unit * 0.1f,
            right = unit * 0.12f + width,
            bottom = size.height / 2f + height / 2f - unit * 0.1f,
        ),
        color,
        stroke,
        height / 2f,
    )
    drawRoundRectOutline(
        Rect(
            left = size.width - unit * 0.12f - width,
            top = size.height / 2f - height / 2f + unit * 0.1f,
            right = size.width - unit * 0.12f,
            bottom = size.height / 2f + height / 2f + unit * 0.1f,
        ),
        color,
        stroke,
        height / 2f,
    )
    drawLine(
        color,
        Offset(size.width * 0.38f, size.height * 0.56f),
        Offset(size.width * 0.62f, size.height * 0.44f),
        stroke.width,
    )
}

private fun DrawScope.drawPanel(color: Color, stroke: Stroke) {
    val inset = size.minDimension * 0.16f
    val outer = Rect(inset, inset, size.width - inset, size.height - inset)
    drawRoundRectOutline(outer, color, stroke, size.minDimension * 0.1f)
    drawRect(
        color = color,
        topLeft = Offset(outer.left + outer.width * 0.2f, outer.top + outer.height * 0.24f),
        size = Size(outer.width * 0.6f, outer.height * 0.22f),
    )
}

private fun DrawScope.drawApp(color: Color, stroke: Stroke) {
    val inset = size.minDimension * 0.18f
    val cell = (size.minDimension - inset * 2f - inset * 0.4f) / 2f
    repeat(2) { row ->
        repeat(2) { column ->
            val topLeft = Offset(
                x = inset + column * (cell + inset * 0.4f),
                y = inset + row * (cell + inset * 0.4f),
            )
            drawRoundRectOutline(
                Rect(topLeft, Size(cell, cell)),
                color,
                stroke,
                cell * 0.22f,
            )
        }
    }
}

private fun DrawScope.drawDot(color: Color, stroke: Stroke) {
    drawCircle(
        color = color,
        radius = size.minDimension * 0.26f,
        style = stroke,
    )
}

private fun DrawScope.drawRoundRectOutline(
    rect: Rect,
    color: Color,
    stroke: Stroke,
    cornerRadius: Float,
) {
    drawRoundRect(
        color = color,
        topLeft = rect.topLeft,
        size = rect.size,
        cornerRadius = androidx.compose.ui.geometry.CornerRadius(cornerRadius, cornerRadius),
        style = stroke,
    )
}
