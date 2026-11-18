package com.panelos.panelhome.ui.components

import androidx.compose.foundation.Canvas
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelOutline

/** A stretch of samples with a value, between two gaps. Indices are inclusive. */
internal fun sparklineRuns(values: List<Float?>): List<IntRange> {
    val runs = ArrayList<IntRange>()
    var start = -1
    for (i in values.indices) {
        if (values[i] != null) {
            if (start < 0) start = i
        } else if (start >= 0) {
            runs += start..(i - 1)
            start = -1
        }
    }
    if (start >= 0) runs += start..values.lastIndex
    return runs
}

/** At most one sample per pixel: when there are more, keep every n-th and the last. */
internal fun <T> downsample(values: List<T>, maxSamples: Int): List<T> {
    if (maxSamples <= 0 || values.size <= maxSamples) return values
    val step = (values.size + maxSamples - 1) / maxSamples
    val result = ArrayList<T>(maxSamples + 1)
    var i = 0
    while (i < values.size) { result += values[i]; i += step }
    if ((values.size - 1) % step != 0) result += values.last()
    return result
}

/**
 * Trend line for a metric card. A single [Path] per run of samples, no
 * animation, no shadow. A missing sample ([null]) is a gap, drawn as a
 * diagonal hatch: the chart never invents a value, and never draws a straight
 * line across a failure.
 *
 * Values are drawn on a fixed scale of 0..[maxValue], so the shape does not
 * rescale itself from one sample to the next.
 */
@Composable
internal fun Sparkline(
    values: List<Float?>,
    modifier: Modifier = Modifier,
    maxValue: Float = 100f,
    color: Color = PanelAccent,
    showGrid: Boolean = true,
) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        if (w <= 0f || h <= 0f) return@Canvas
        val stroke = 2.dp.toPx()
        val pad = stroke / 2
        if (showGrid) {
            val dash = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 4.dp.toPx()))
            for (fraction in floatArrayOf(0.5f)) {
                val y = pad + (h - 2 * pad) * (1 - fraction)
                drawLine(PanelOutline.copy(alpha = 0.5f), Offset(0f, y), Offset(w, y), 1.dp.toPx(), pathEffect = dash)
            }
        }
        val samples = downsample(values, w.toInt().coerceAtLeast(1))
        if (samples.size < 2) return@Canvas
        val top = maxValue.coerceAtLeast(0.0001f)
        fun x(i: Int) = w * i / (samples.size - 1)
        fun y(v: Float) = pad + (h - 2 * pad) * (1f - (v / top).coerceIn(0f, 1f))

        // Gaps: faint diagonal hatch over the missing stretch.
        var gapStart = -1
        fun hatch(from: Int, to: Int) {
            val left = x(from)
            val right = x(to).coerceAtLeast(left + 1f)
            clipRect(left, 0f, right, h) {
                val step = 6.dp.toPx()
                var o = -h
                while (o < right - left + h) {
                    drawLine(PanelOutline, Offset(left + o, h), Offset(left + o + h, 0f), 1.5.dp.toPx())
                    o += step
                }
            }
        }
        for (i in samples.indices) {
            if (samples[i] == null) { if (gapStart < 0) gapStart = i } else if (gapStart >= 0) { hatch(gapStart, i); gapStart = -1 }
        }
        if (gapStart >= 0) hatch(gapStart, samples.lastIndex)

        for (run in sparklineRuns(samples)) {
            if (run.first == run.last) {
                drawCircle(color, stroke, Offset(x(run.first), y(samples[run.first]!!)))
                continue
            }
            val line = Path()
            for (i in run) {
                val px = x(i); val py = y(samples[i]!!)
                if (i == run.first) line.moveTo(px, py) else line.lineTo(px, py)
            }
            val area = Path().apply {
                addPath(line)
                lineTo(x(run.last), h)
                lineTo(x(run.first), h)
                close()
            }
            drawPath(area, color.copy(alpha = 0.14f))
            drawPath(line, color, style = Stroke(stroke, cap = StrokeCap.Round, join = StrokeJoin.Round))
        }
    }
}
