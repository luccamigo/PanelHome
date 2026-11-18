package com.panelos.panelhome.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
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
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelHairline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType

/** "Abrir Desktop": an icon, the name and a hint, as a card. [horizontal] lays them in a row for the wide slot. */
@Composable
internal fun DesktopCard(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    horizontal: Boolean = false,
    stale: Boolean = false,
) {
    val interaction = remember { MutableInteractionSource() }
    val shape = RoundedCornerShape(PanelRadius.card)
    val title = stringResource(R.string.dashboard_open_desktop)
    val hint = stringResource(R.string.dashboard_open_desktop_hint)
    val icon: @Composable () -> Unit = {
        Box(
            Modifier.size(56.dp).clip(RoundedCornerShape(PanelRadius.control)).background(PanelSurfaceElevated),
            contentAlignment = Alignment.Center,
        ) { PanelIcon(R.drawable.ic_desktop, contentDescription = null, size = 32.dp, tint = PanelAccent) }
    }
    val text: @Composable () -> Unit = {
        Column {
            Text(title, style = PanelType.value.copy(fontSize = PanelType.cardTitle.fontSize), color = PanelText, maxLines = 1)
            Text(hint, style = PanelType.body, color = PanelTextMuted, maxLines = 1)
        }
    }
    Box(
        modifier = modifier
            .pressScale(interaction)
            .alpha(if (stale) 0.55f else 1f)
            .clip(shape)
            .background(PanelSurface, shape)
            .border(1.dp, PanelHairline, shape)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(horizontal = 20.dp, vertical = 14.dp),
        contentAlignment = Alignment.CenterStart,
    ) {
        if (horizontal) {
            Row(horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) { icon(); text() }
        } else {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) { icon(); text() }
        }
    }
}
