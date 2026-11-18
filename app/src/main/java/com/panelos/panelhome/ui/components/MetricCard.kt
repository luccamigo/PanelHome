package com.panelos.panelhome.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.panelos.panelhome.R
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelHairline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import com.panelos.panelhome.ui.theme.PanelWarning

/** Above this load, held for [HIGH_LOAD_HOLD_MILLIS], a metric's number turns amber (never red). */
internal const val HIGH_LOAD_PERCENT = 90f
internal const val HIGH_LOAD_HOLD_MILLIS = 10_000L

/**
 * What a metric card shows. The card never draws a placeholder for something
 * that does not exist: a missing [secondary] or [caption] simply is not shown,
 * and a missing [value] turns the whole number into "Sem leitura" with no chart.
 */
internal data class MetricCardContent(
    val label: String,
    /** Main number already formatted ("38,8"); null when the sensor has no reading. */
    val value: String?,
    val unit: String? = null,
    /** Right of the label: the model, the app name. */
    val secondary: String? = null,
    /** Line under the number: "4,11 GHz", "mín 98 · máx 165". */
    val caption: String? = null,
    /** Trend, oldest first; a null sample is a gap. */
    val series: List<Float?> = emptyList(),
    val seriesMax: Float = 100f,
    /** Spoken text for the whole card (TalkBack reads one element). */
    val description: String,
)

/**
 * Metric card: label, number, legend and a trend line on `surface` with a 1 dp
 * hairline, 20 dp radius, no shadow. When [onClick] is set the whole card is a
 * button that shrinks to 0.96 while pressed.
 *
 * [highLoad] makes the number amber. [staleLabel] (for example "há 2 min")
 * means the data is old: the card drops to 55% and shows that label.
 */
@Composable
internal fun MetricCard(
    content: MetricCardContent,
    modifier: Modifier = Modifier,
    numeral: TextStyle = PanelType.numeralL,
    chartHeight: Dp = 40.dp,
    highLoad: Boolean = false,
    staleLabel: String? = null,
    stale: Boolean = staleLabel != null,
    onClick: (() -> Unit)? = null,
) {
    MetricCardFrame(modifier, content.description, stale, onClick) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(content.label, style = PanelType.label, color = PanelAccent, maxLines = 1)
            Box(Modifier.weight(1f).padding(start = 8.dp), contentAlignment = Alignment.CenterEnd) {
                val note = staleLabel ?: content.secondary
                if (note != null) {
                    Text(
                        note,
                        style = PanelType.caption,
                        color = if (staleLabel != null) PanelWarning else PanelTextMuted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        }
        if (content.value == null) {
            Text(stringResource(R.string.metric_no_reading), style = PanelType.body, color = PanelTextMuted)
            Box(Modifier.weight(1f))
        } else {
            Row(verticalAlignment = Alignment.Bottom) {
                Text(
                    content.value,
                    style = numeral,
                    color = if (highLoad) PanelWarning else PanelText,
                    maxLines = 1,
                    softWrap = false,
                )
                if (content.unit != null) {
                    Text(
                        content.unit,
                        style = PanelType.caption.copy(
                            fontSize = maxOf(PanelType.unitSize(numeral.fontSize).value, PanelType.MinFunctionalSp).sp,
                            fontWeight = FontWeight.Medium,
                        ),
                        color = PanelTextMuted,
                        maxLines = 1,
                        modifier = Modifier.padding(start = 4.dp, bottom = 6.dp),
                    )
                }
            }
            if (content.caption != null) {
                Text(content.caption, style = PanelType.caption, color = PanelTextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            if (content.series.isNotEmpty()) {
                Box(Modifier.weight(1f, fill = true).fillMaxWidth().heightIn(min = chartHeight)) {
                    Sparkline(content.series, Modifier.fillMaxSize(), maxValue = content.seriesMax)
                }
            }
        }
    }
}

/**
 * The shared shell of every card: `surface` fill, 1 dp hairline, 20 dp radius,
 * no shadow, one TalkBack element described by [description]. With [onClick]
 * the whole card is a button that shrinks while pressed.
 */
@Composable
internal fun MetricCardFrame(
    modifier: Modifier,
    description: String,
    stale: Boolean = false,
    onClick: (() -> Unit)? = null,
    content: @Composable ColumnScope.() -> Unit,
) {
    val interaction = remember { MutableInteractionSource() }
    val shape = RoundedCornerShape(PanelRadius.card)
    val clickable = if (onClick != null) {
        Modifier
            .pressScale(interaction)
            .clip(shape)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
    } else {
        Modifier.clip(shape)
    }
    Column(
        modifier = modifier
            .then(clickable)
            .alpha(if (stale) 0.55f else 1f)
            .background(PanelSurface, shape)
            .border(1.dp, PanelHairline, shape)
            .padding(horizontal = 18.dp, vertical = 14.dp)
            .clearAndSetSemantics {
                contentDescription = description
                if (onClick != null) role = Role.Button
            },
        verticalArrangement = Arrangement.spacedBy(2.dp),
        content = content,
    )
}

/** True when [percent] has stayed at or above [HIGH_LOAD_PERCENT] for the whole hold time. */
internal fun isHighLoad(percentSinceMillis: Long?, nowMillis: Long): Boolean =
    percentSinceMillis != null && nowMillis - percentSinceMillis >= HIGH_LOAD_HOLD_MILLIS
