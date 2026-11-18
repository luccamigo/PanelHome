package com.panelos.panelhome.ui.components

import androidx.annotation.DrawableRes
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelError
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSize
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import com.panelos.panelhome.ui.theme.PanelWarning

internal enum class BannerTone(val border: Color, @DrawableRes val iconRes: Int, val urgent: Boolean) {
    Connecting(PanelWarning, R.drawable.ic_refresh, urgent = false),
    Disconnected(PanelOutline, R.drawable.ic_computer_off, urgent = false),
    Error(PanelError, R.drawable.ic_warning, urgent = true),
}

internal data class BannerAction(
    val label: String,
    val onClick: () -> Unit,
    @DrawableRes val iconRes: Int? = null,
)

/**
 * State strip: what happened, in plain words, and a way out. Every failure
 * keeps its exit button visible (Tentar de novo, Voltar ao painel...): a state
 * that offers none is a dead end. The text is read aloud as soon as it appears
 * (assertively for errors, politely while connecting).
 */
@Composable
internal fun StatusBanner(
    title: String,
    message: String,
    tone: BannerTone,
    modifier: Modifier = Modifier,
    primary: BannerAction? = null,
    secondary: BannerAction? = null,
) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .heightIn(min = PanelSize.bannerHeight)
            .clip(RoundedCornerShape(PanelRadius.banner))
            .background(PanelSurfaceElevated)
            .border(1.dp, tone.border, RoundedCornerShape(PanelRadius.banner))
            .padding(horizontal = 20.dp, vertical = 16.dp)
            .semantics { liveRegion = if (tone.urgent) LiveRegionMode.Assertive else LiveRegionMode.Polite },
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Box(
            Modifier
                .size(56.dp)
                .clip(RoundedCornerShape(PanelRadius.control))
                .background(PanelSurface),
            contentAlignment = Alignment.Center,
        ) {
            PanelIcon(
                tone.iconRes,
                contentDescription = null,
                size = 32.dp,
                tint = if (tone == BannerTone.Connecting) PanelWarning else if (tone == BannerTone.Error) PanelError else PanelTextMuted,
            )
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(title, style = PanelType.cardTitle, color = PanelText, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(message, style = PanelType.body, color = PanelTextMuted, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
        if (primary != null) {
            PanelButton(primary.label, primary.onClick, style = PanelButtonStyle.Primary, iconRes = primary.iconRes)
        }
        if (secondary != null) {
            PanelButton(secondary.label, secondary.onClick, style = PanelButtonStyle.Secondary, iconRes = secondary.iconRes)
        }
    }
}

/**
 * Shows [StatusBanner] when [visible]: fade in 200 ms with a 12 dp rise, out in
 * 150 ms. With reduced motion only a 100 ms fade is left.
 */
@Composable
internal fun AnimatedStatusBanner(
    visible: Boolean,
    title: String,
    message: String,
    tone: BannerTone,
    modifier: Modifier = Modifier,
    primary: BannerAction? = null,
    secondary: BannerAction? = null,
) {
    val reduced = LocalPanelReducedMotion.current
    val risePx = with(LocalDensity.current) { (if (reduced) 0 else PanelMotion.BannerRiseDp).dp.roundToPx() }
    AnimatedVisibility(
        visible = visible,
        modifier = modifier,
        enter = fadeIn(tween(PanelMotion.fade(reduced, PanelMotion.BannerEnterMillis), easing = PanelMotion.EaseOut)) +
            slideInVertically(tween(PanelMotion.duration(reduced, PanelMotion.BannerEnterMillis), easing = PanelMotion.EaseOut)) { -risePx },
        exit = fadeOut(tween(PanelMotion.fade(reduced, PanelMotion.BannerExitMillis), easing = PanelMotion.EaseOut)) +
            slideOutVertically(tween(PanelMotion.duration(reduced, PanelMotion.BannerExitMillis), easing = PanelMotion.EaseOut)) { -risePx },
    ) {
        StatusBanner(title, message, tone, primary = primary, secondary = secondary)
    }
}
