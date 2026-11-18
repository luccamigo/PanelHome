package com.panelos.panelhome.ui.components

import androidx.annotation.DrawableRes
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelAccentSoft
import com.panelos.panelhome.ui.theme.PanelError
import com.panelos.panelhome.ui.theme.PanelOnAccent
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSize
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelType

internal enum class PanelButtonStyle { Primary, Secondary, Destructive }

/**
 * The app's button: 56 dp (48 in compact banners), 18 dp radius, 16 sp / 600.
 * Primary is cyan, secondary a neutral fill with an outline, destructive red at
 * 14% with red text. It shrinks to 0.96 while pressed, like every touch target.
 */
@Composable
internal fun PanelButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    style: PanelButtonStyle = PanelButtonStyle.Primary,
    @DrawableRes iconRes: Int? = null,
    enabled: Boolean = true,
    height: Dp = PanelSize.button,
) {
    val interaction = remember { MutableInteractionSource() }
    val shape = RoundedCornerShape(PanelRadius.button)
    val (container, content, border) = when (style) {
        PanelButtonStyle.Primary -> Triple(PanelAccent, PanelOnAccent, null)
        PanelButtonStyle.Secondary -> Triple(PanelSurfaceElevated, PanelText, BorderStroke(1.dp, PanelOutline))
        PanelButtonStyle.Destructive -> Triple(PanelError.copy(alpha = 0.14f), PanelError, null)
    }
    Row(
        modifier = modifier
            .pressScale(interaction)
            .alpha(if (enabled) 1f else 0.4f)
            .height(height)
            .defaultMinSize(minWidth = PanelSize.minTouch)
            .clip(shape)
            .background(container)
            .then(if (border != null) Modifier.border(border, shape) else Modifier)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .padding(horizontal = 20.dp),
        horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (iconRes != null) PanelIcon(iconRes, contentDescription = null, tint = content)
        Text(text, style = PanelType.label.copy(letterSpacing = androidx.compose.ui.unit.TextUnit.Unspecified), color = content, maxLines = 1, softWrap = false, overflow = TextOverflow.Ellipsis)
    }
}

/** Soft cyan fill used for a selected or pressed neutral surface. */
internal val SelectedFill = PanelAccentSoft
