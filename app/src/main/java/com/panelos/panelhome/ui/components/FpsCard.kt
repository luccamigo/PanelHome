package com.panelos.panelhome.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.FpsState
import com.panelos.panelhome.domain.model.fpsChartCeiling
import com.panelos.panelhome.domain.model.fpsSummary
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType

/** How the line under the FPS number reads. */
internal enum class FpsSummaryStyle { None, MinMaxWindow, MinAverageMax }

/**
 * FPS card, three states. [FpsState.Unavailable] draws nothing at all (the
 * caller reflows the page around the missing card). [FpsState.NoApp] keeps the
 * card so the layout does not jump when a game opens, with no number, no "—"
 * and no chart. [FpsState.Measuring] is the full metric card; the summary is
 * computed here from the series.
 */
@Composable
internal fun FpsCard(
    state: FpsState,
    windowMinutes: Int,
    modifier: Modifier = Modifier,
    numeral: TextStyle = PanelType.numeralL,
    summaryStyle: FpsSummaryStyle = FpsSummaryStyle.MinMaxWindow,
    chartHeight: Dp = 40.dp,
    stale: Boolean = false,
    staleLabel: String? = null,
) {
    when (state) {
        FpsState.Unavailable -> Unit
        FpsState.NoApp -> {
            val title = stringResource(R.string.fps_none_title)
            val hint = stringResource(R.string.fps_none_hint)
            BoxWithConstraints(modifier) {
            val compact = maxHeight < 180.dp
            MetricCardFrame(Modifier.matchParentSize(), description = "FPS. $title. $hint", stale = stale) {
                Text("FPS", style = PanelType.label, color = PanelAccent, maxLines = 1)
                Row(
                    Modifier.weight(1f),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(
                        Modifier.size(if (compact) 32.dp else 56.dp).clip(RoundedCornerShape(PanelRadius.control)).background(PanelSurfaceElevated),
                        contentAlignment = Alignment.Center,
                    ) { PanelIcon(R.drawable.ic_gamepad, contentDescription = null, size = 32.dp, tint = PanelTextMuted) }
                    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(title, style = if (compact) PanelType.caption else PanelType.value, color = PanelText, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        Text(hint, style = PanelType.caption, color = PanelTextMuted, maxLines = if (compact) 2 else 4, overflow = TextOverflow.Ellipsis)
                    }
                }
            }
            }
        }
        is FpsState.Measuring -> {
            val summary = remember(state.series) { fpsSummary(state.series) }
            val caption = when {
                summary == null -> null
                summaryStyle == FpsSummaryStyle.MinMaxWindow ->
                    stringResource(R.string.fps_summary_window, summary.minimum, summary.maximum, windowMinutes)
                summaryStyle == FpsSummaryStyle.MinAverageMax ->
                    stringResource(R.string.fps_summary_full, summary.minimum, summary.average, summary.maximum)
                else -> null
            }
            val description = state.appName?.let { stringResource(R.string.fps_description_app, state.current, it) }
                ?: stringResource(R.string.fps_description, state.current)
            MetricCard(
                MetricCardContent(
                    label = "FPS",
                    value = state.current.toString(),
                    unit = "apres./s",
                    secondary = state.appName,
                    caption = caption,
                    series = state.series,
                    seriesMax = fpsChartCeiling(state.series),
                    description = description,
                ),
                modifier = modifier,
                numeral = numeral,
                chartHeight = chartHeight,
                stale = stale,
                staleLabel = staleLabel,
            )
        }
    }
}
