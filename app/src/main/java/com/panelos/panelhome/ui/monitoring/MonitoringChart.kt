package com.panelos.panelhome.ui.monitoring

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.ui.components.formatUsagePercent
import com.panelos.panelhome.presentation.HistoryGap
import kotlin.math.roundToInt

@Composable
internal fun MonitoringChart(
    points: List<ChartPoint>,
    now: Long,
    windowMinutes: Int,
    network: Boolean,
    modifier: Modifier = Modifier,
    interactive: Boolean = false,
    gaps: List<HistoryGap> = emptyList(),
) {
    val primary = MaterialTheme.colorScheme.primary
    val secondary = MaterialTheme.colorScheme.secondary
    val grid = MaterialTheme.colorScheme.outline.copy(alpha = 0.5f)
    val ceiling = if (network) networkCeiling(points) else 100.0
    val start = now - windowMinutes * 60_000L
    var selectedX by remember { mutableStateOf<Float?>(null) }
    var chartWidth by remember { mutableIntStateOf(1) }
    val latestPoints by rememberUpdatedState(points)
    val latestNow by rememberUpdatedState(now)
    val latestStart by rememberUpdatedState(start)
    val density = LocalDensity.current
    Box(modifier) {
        Canvas(Modifier.fillMaxSize().onSizeChanged { chartWidth = it.width.coerceAtLeast(1) }.clearAndSetSemantics {}.then(
            if (!interactive) Modifier else Modifier.pointerInput(Unit) {
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    selectedX = down.position.x.coerceIn(0f, size.width.toFloat())
                    down.consume()
                    do {
                        val event = awaitPointerEvent()
                        val pointer = event.changes.find { it.id == down.id }
                        pointer?.consume()
                        selectedX = pointer?.position?.x?.coerceIn(0f, size.width.toFloat())
                    } while (event.changes.any { it.id == down.id && it.pressed })
                    selectedX = null
                }
            },
        )) {
            val baseline = if (network) size.height / 2 else size.height
            val amplitude = if (network) size.height / 2 else size.height
            fun x(at: Long) = ((at - start).toDouble() / (now - start) * size.width).toFloat().coerceIn(0f, size.width)
            if (network) drawLine(grid, Offset(0f, baseline), Offset(size.width, baseline), 1.dp.toPx())
            else listOf(0.25f, 0.5f, 0.75f).forEach { fraction ->
                drawLine(grid, Offset(0f, size.height * fraction), Offset(size.width, size.height * fraction), 1.dp.toPx())
            }
            // Empty intervals are hatched; there is no interpolation across a disconnect.
            gaps.filter { it.end != null && it.end >= start && it.start <= now }.forEach { gap ->
                val left = x(gap.start)
                val right = x(gap.end!!)
                var stripe = left
                while (stripe < right) {
                    drawLine(grid.copy(alpha = 0.4f), Offset(stripe, size.height),
                        Offset((stripe + 12.dp.toPx()).coerceAtMost(right), 0f), 1.dp.toPx())
                    stripe += 10.dp.toPx()
                }
            }
            val series = if (network) listOf(false, true) else listOf(false)
            series.forEach { upload ->
                val source = if (upload) points.map { it.copy(value = it.secondary) } else points
                val color = if (upload) secondary else primary
                chartEnvelope(source, chartWidth, start, now).forEach { segment ->
                    val line = Path()
                    segment.forEachIndexed { index, point ->
                        val y = baseline + (if (upload) 1 else -1) * (point.value!! / ceiling).coerceIn(0.0, 1.0).toFloat() * amplitude
                        if (index == 0) line.moveTo(x(point.at), y) else line.lineTo(x(point.at), y)
                    }
                    drawPath(line, color, style = Stroke(2.dp.toPx()))
                    val fill = Path().apply {
                        addPath(line)
                        lineTo(x(segment.last().at), baseline)
                        lineTo(x(segment.first().at), baseline)
                        close()
                    }
                    drawPath(fill, color.copy(alpha = 0.14f))
                    if (segment.size == 1) {
                        val point = segment.first()
                        val y = baseline + (if (upload) 1 else -1) * (point.value!! / ceiling).toFloat() * amplitude
                        drawLine(color, Offset(x(point.at), y), Offset(x(point.at) + 1.dp.toPx(), y), 2.dp.toPx())
                    }
                }
            }
            selectedX?.let { drawLine(primary, Offset(it, 0f), Offset(it, size.height), 1.dp.toPx()) }
        }
        if (network) Text(rateText(ceiling), color = MaterialTheme.colorScheme.onSurfaceVariant,
            style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(4.dp))
        selectedX?.let { x ->
            val at = latestStart + ((latestNow - latestStart) * (x / chartWidth)).toLong()
            val point = selectedPoint(latestPoints, at)
            val value = if (point == null || point.value == null && (!network || point.secondary == null)) "sem dados" else if (network)
                "↓ ${rateText(point.value)} · ↑ ${rateText(point.secondary)}" else formatUsagePercent(point.value)
            val tooltipWidth = with(density) { 300.dp.roundToPx() }
            Text("${point?.let { clockText(it.unixMs, true) } ?: "—"} · $value",
                modifier = Modifier.offset { IntOffset((x.roundToInt() - tooltipWidth / 2).coerceIn(0, (chartWidth - tooltipWidth).coerceAtLeast(0)), 24) }
                    .widthIn(max = 300.dp).background(MaterialTheme.colorScheme.surface).padding(8.dp),
                style = MaterialTheme.typography.labelLarge)
        }
    }
}
