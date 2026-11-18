package com.panelos.panelhome.ui.wifi

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

// Drawn, not an icon font: the app has no icon library and colour is never the only signal (the text always says it too).

/** A check mark, for finished steps and good states. */
@Composable
internal fun CheckMark(color: Color, modifier: Modifier = Modifier, size: Dp = 24.dp) {
    Canvas(modifier.size(size)) {
        val w = this.size.width
        val path = Path().apply {
            moveTo(w * 0.18f, w * 0.54f); lineTo(w * 0.42f, w * 0.76f); lineTo(w * 0.84f, w * 0.26f)
        }
        drawPath(path, color, style = Stroke(width = w * 0.12f, cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}

/** A warning triangle with an exclamation mark, for failures. */
@Composable
internal fun WarningMark(color: Color, modifier: Modifier = Modifier, size: Dp = 24.dp) {
    Canvas(modifier.size(size)) {
        val w = this.size.width
        val triangle = Path().apply {
            moveTo(w * 0.5f, w * 0.08f); lineTo(w * 0.94f, w * 0.88f); lineTo(w * 0.06f, w * 0.88f); close()
        }
        drawPath(triangle, color, style = Stroke(width = w * 0.08f, join = StrokeJoin.Round))
        drawLine(color, Offset(w * 0.5f, w * 0.38f), Offset(w * 0.5f, w * 0.60f), strokeWidth = w * 0.09f, cap = StrokeCap.Round)
        drawCircle(color, radius = w * 0.05f, center = Offset(w * 0.5f, w * 0.74f))
    }
}