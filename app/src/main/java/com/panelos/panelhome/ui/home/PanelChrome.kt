package com.panelos.panelhome.ui.home

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.ui.components.PanelIcon
import com.panelos.panelhome.ui.components.pressScale
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelHairline
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelNavSurface
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSize
import com.panelos.panelhome.ui.theme.PanelSpacing
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import com.panelos.panelhome.ui.theme.PanelWarning

private data class NavSpec(val iconRes: Int, val labelRes: Int)

private fun NavDestination.spec() = when (this) {
    NavDestination.PANEL -> NavSpec(R.drawable.ic_panel, R.string.home_page_dashboard)
    NavDestination.SHORTCUTS -> NavSpec(R.drawable.ic_shortcuts, R.string.streamdeck_title)
    NavDestination.COMPUTER -> NavSpec(R.drawable.ic_computer, R.string.nav_computer)
    NavDestination.ALERTS -> NavSpec(R.drawable.ic_alerts, R.string.nav_alerts)
    NavDestination.MEDIA -> NavSpec(R.drawable.ic_media, R.string.loop_media_title)
}

/** Over the media the chrome floats on the page background at 90% / 70%, without blur. */
private val NavOverMedia = Color(0xE6070B11)
private val TopBarOverMedia = Brush.verticalGradient(listOf(Color(0xBF070B11), Color.Transparent))

/**
 * The bottom bar: one item per destination, equal width, icon plus a name of
 * at least 14 sp. The active item is cyan with a 3 dp bar on top. It is
 * chrome of the carousel, outside the pager, so it never joins a swipe.
 */
@Composable
internal fun PanelNavBar(
    destinations: List<NavDestination>,
    selected: NavDestination,
    onSelect: (NavDestination) -> Unit,
    modifier: Modifier = Modifier,
    alertsUnseen: Boolean = false,
    overMedia: Boolean = false,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .height(PanelSize.navBar)
            .background(if (overMedia) NavOverMedia else PanelNavSurface)
            .drawBehind { drawRect(PanelHairline, Offset.Zero, Size(size.width, 1.dp.toPx())) },
    ) {
        destinations.forEachIndexed { index, destination ->
            NavItem(
                destination = destination,
                selected = destination == selected,
                position = index + 1,
                count = destinations.size,
                badge = destination == NavDestination.ALERTS && alertsUnseen,
                onClick = { onSelect(destination) },
                modifier = Modifier.weight(1f),
            )
        }
    }
}

@Composable
private fun NavItem(
    destination: NavDestination,
    selected: Boolean,
    position: Int,
    count: Int,
    badge: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val spec = destination.spec()
    val reduced = LocalPanelReducedMotion.current
    val tint by animateColorAsState(
        targetValue = if (selected) PanelAccent else PanelTextMuted,
        animationSpec = tween(PanelMotion.duration(reduced, PanelMotion.NavColorMillis), easing = PanelMotion.EaseOut),
        label = "navTint",
    )
    val positionText = stringResource(R.string.nav_position, position, count)
    val selectedText = stringResource(R.string.nav_selected)
    Box(
        modifier = modifier
            .fillMaxSize()
            .selectable(selected = selected, role = Role.Tab, onClick = onClick)
            .semantics { stateDescription = if (selected) "$selectedText, $positionText" else positionText },
        contentAlignment = Alignment.Center,
    ) {
        if (selected) {
            Box(
                Modifier
                    .align(Alignment.TopCenter)
                    .width(PanelSize.navIndicatorWidth)
                    .height(PanelSize.navIndicatorHeight)
                    .clip(RoundedCornerShape(bottomStart = 3.dp, bottomEnd = 3.dp))
                    .background(PanelAccent),
            )
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Box {
                PanelIcon(spec.iconRes, contentDescription = null, size = PanelSize.navIcon, tint = tint)
                if (badge) {
                    Box(
                        Modifier
                            .align(Alignment.TopEnd)
                            .size(10.dp)
                            .clip(CircleShape)
                            .background(PanelWarning),
                    )
                }
            }
            Text(
                text = stringResource(spec.labelRes),
                style = PanelType.caption,
                color = tint,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
    }
}

/**
 * Connection chip: a dot plus a word, so the state is never colour alone.
 * Connected is cyan, connecting amber, disconnected an empty ring in muted text.
 */
@Composable
internal fun ConnectionChip(
    status: ConnectionStatus,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val label = stringResource(
        when (status) {
            ConnectionStatus.CONNECTED -> R.string.connection_pc_connected
            ConnectionStatus.CONNECTING -> R.string.connection_chip_connecting
            ConnectionStatus.DISCONNECTED -> R.string.connection_pc_disconnected
        },
    )
    val interaction = remember { MutableInteractionSource() }
    Row(
        modifier = modifier
            .pressScale(interaction)
            .height(PanelSize.statusChip)
            .clip(RoundedCornerShape(PanelRadius.pill))
            .background(PanelSurfaceElevated)
            .clickable(interactionSource = interaction, indication = null, role = Role.Button, onClick = onClick)
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        val dot = Modifier.size(8.dp)
        when (status) {
            ConnectionStatus.CONNECTED -> Box(dot.clip(CircleShape).background(PanelAccent))
            ConnectionStatus.CONNECTING -> Box(dot.clip(CircleShape).background(PanelWarning))
            ConnectionStatus.DISCONNECTED -> Box(dot.border(1.5.dp, PanelTextMuted, CircleShape))
        }
        Text(
            text = label,
            style = PanelType.caption,
            color = if (status == ConnectionStatus.DISCONNECTED) PanelTextMuted else MaterialTheme.colorScheme.onSurface,
            maxLines = 1,
        )
    }
}

/** The ⋮ in the corner: Modo ambiente and Configurações, the rarely used actions. */
@Composable
internal fun OverflowMenu(
    expanded: Boolean,
    onExpandedChange: (Boolean) -> Unit,
    onEnterAmbient: () -> Unit,
    onOpenSettings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val description = stringResource(R.string.menu_more)
    Box(modifier) {
        IconButton(
            onClick = { onExpandedChange(true) },
            modifier = Modifier
                .size(PanelSize.minTouch)
                .semantics { contentDescription = description },
        ) {
            PanelIcon(R.drawable.ic_more, contentDescription = null, tint = MaterialTheme.colorScheme.onSurface)
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { onExpandedChange(false) },
            shape = RoundedCornerShape(PanelRadius.control),
            containerColor = PanelSurfaceElevated,
            border = BorderStroke(1.dp, PanelOutline),
            shadowElevation = 0.dp,
            tonalElevation = 0.dp,
        ) {
            MenuItem(R.drawable.ic_ambient, R.string.dashboard_open_ambient) { onExpandedChange(false); onEnterAmbient() }
            MenuItem(R.drawable.ic_settings, R.string.dashboard_open_settings) { onExpandedChange(false); onOpenSettings() }
        }
    }
}

@Composable
private fun MenuItem(iconRes: Int, labelRes: Int, onClick: () -> Unit) {
    DropdownMenuItem(
        text = { Text(stringResource(labelRes), style = PanelType.body) },
        leadingIcon = { PanelIcon(iconRes, contentDescription = null) },
        onClick = onClick,
        modifier = Modifier.heightIn(min = 56.dp),
    )
}

/** Top bar: connection chip and the ⋮ on the right; the left is left to the page's own title. */
@Composable
internal fun PanelTopBar(
    status: ConnectionStatus,
    onChipClick: () -> Unit,
    menuExpanded: Boolean,
    onMenuExpandedChange: (Boolean) -> Unit,
    onEnterAmbient: () -> Unit,
    onOpenSettings: () -> Unit,
    modifier: Modifier = Modifier,
    overMedia: Boolean = false,
    start: @Composable () -> Unit = {},
    end: @Composable () -> Unit = {},
) {
    // Over the media the bar sits on a 72 dp fade (75% to 0), so the chip and the menu stay legible.
    Box(
        modifier = modifier
            .fillMaxWidth()
            .then(
                if (overMedia) Modifier.height(72.dp).background(TopBarOverMedia)
                else Modifier.height(PanelSize.topBar),
            ),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .height(PanelSize.topBar)
                .padding(horizontal = PanelSpacing.page),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) { start() }
            end()
            Box(Modifier.width(PanelSpacing.md))
            ConnectionChip(status, onChipClick)
            Box(Modifier.width(PanelSpacing.sm))
            OverflowMenu(menuExpanded, onMenuExpandedChange, onEnterAmbient, onOpenSettings)
        }
    }
}
