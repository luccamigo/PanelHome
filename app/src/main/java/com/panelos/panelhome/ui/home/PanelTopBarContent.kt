package com.panelos.panelhome.ui.home

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.ui.components.PanelDateTime
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelAccentSoft
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType

/** The Painel's top-left: the clock and the date on two short lines. */
@Composable
internal fun ClockBlock(dateTime: PanelDateTime, modifier: Modifier = Modifier) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
        Text(dateTime.time, style = PanelType.clock, color = PanelText, maxLines = 1)
        if (dateTime.weekday.isNotEmpty()) {
            Column {
                Text(dateTime.weekday, style = PanelType.caption, color = PanelTextMuted, maxLines = 1)
                Text(dateTime.dayMonth, style = PanelType.caption, color = PanelTextMuted, maxLines = 1)
            }
        }
    }
}

/** A page title in the top bar, with optional [trailing] content (chips, a switch). */
@Composable
internal fun PageTitle(title: String, modifier: Modifier = Modifier, trailing: @Composable () -> Unit = {}) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
        Text(title, style = PanelType.title, color = PanelText, maxLines = 1, overflow = TextOverflow.Ellipsis)
        trailing()
    }
}

/** Page chips of Atalhos ("Trabalho · Mídia · Casa"): which shortcut page is showing. */
@Composable
internal fun PageChips(names: List<String>, selectedIndex: Int, onSelect: (Int) -> Unit, modifier: Modifier = Modifier) {
    Row(modifier, horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        names.forEachIndexed { index, name ->
            val selected = index == selectedIndex
            val reduced = LocalPanelReducedMotion.current
            val fill by animateColorAsState(
                if (selected) PanelAccentSoft else PanelSurface,
                tween(PanelMotion.duration(reduced, PanelMotion.NavColorMillis), easing = PanelMotion.EaseOut),
                label = "chipFill",
            )
            val shape = RoundedCornerShape(PanelRadius.pill)
            // 48 dp hit area around the 36 dp pill.
            Row(
                Modifier
                    .height(48.dp)
                    .defaultMinSize(minWidth = 48.dp)
                    .selectable(selected = selected, role = Role.RadioButton, onClick = { onSelect(index) }),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(
                    modifier = Modifier
                        .height(36.dp)
                        .clip(shape)
                        .background(fill, shape)
                        .border(1.dp, if (selected) PanelAccent else PanelOutline.copy(alpha = 0.5f), shape)
                        .padding(horizontal = 16.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        text = name,
                        style = PanelType.label.copy(letterSpacing = TextUnit.Unspecified),
                        color = if (selected) PanelAccent else PanelText,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
        }
    }
}
