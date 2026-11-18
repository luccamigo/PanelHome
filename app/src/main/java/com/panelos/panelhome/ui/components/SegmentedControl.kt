package com.panelos.panelhome.ui.components

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelAccentSoft
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSize
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType

/**
 * Segmented control: a 40 dp track on `surface`; the active segment is cyan
 * text on a soft cyan fill with a 1 dp cyan border. Each segment keeps a 48 dp
 * touch height: the track is 40 dp tall, the hit area is not.
 */
@Composable
internal fun SegmentedControl(
    options: List<String>,
    selectedIndex: Int,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
) {
    val reduced = LocalPanelReducedMotion.current
    val shape = RoundedCornerShape(PanelRadius.control)
    Row(
        modifier = modifier
            .height(PanelSize.minTouch)
            .drawBehind {
                val track = 40.dp.toPx()
                drawRoundRect(
                    PanelSurface,
                    topLeft = Offset(0f, (size.height - track) / 2),
                    size = Size(size.width, track),
                    cornerRadius = CornerRadius(PanelRadius.control.toPx()),
                )
            },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        options.forEachIndexed { index, label ->
            val selected = index == selectedIndex
            val fill by animateColorAsState(
                if (selected) PanelAccentSoft else Color.Transparent,
                tween(PanelMotion.duration(reduced, PanelMotion.NavColorMillis), easing = PanelMotion.EaseOut),
                label = "segmentFill",
            )
            // The hit area is 48 dp tall; the visible segment inside it is 40 dp.
            Box(
                modifier = Modifier
                    .defaultMinSize(minWidth = 72.dp)
                    .height(PanelSize.minTouch)
                    .selectable(selected = selected, role = Role.RadioButton, onClick = { onSelect(index) }),
                contentAlignment = Alignment.Center,
            ) {
                Box(
                    modifier = Modifier
                        .height(40.dp)
                        .background(fill, shape)
                        .then(if (selected) Modifier.border(1.dp, PanelAccent, shape) else Modifier)
                        .padding(horizontal = 16.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = label,
                        style = PanelType.label.copy(letterSpacing = TextUnit.Unspecified),
                        color = if (selected) PanelAccent else PanelTextMuted,
                        maxLines = 1,
                    )
                }
            }
        }
    }
}
