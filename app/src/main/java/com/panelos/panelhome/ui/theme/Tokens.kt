package com.panelos.panelhome.ui.theme

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** Spacing scale, base 4 dp. */
internal object PanelSpacing {
    val xs: Dp = 4.dp
    val sm: Dp = 8.dp
    val md: Dp = 12.dp
    val lg: Dp = 16.dp
    val xl: Dp = 24.dp
    val xxl: Dp = 32.dp

    /** Page margin. */
    val page: Dp = xl

    /** Gap between metric cards (12 dp in the shortcut grid). */
    val cardGap: Dp = lg
    val gridGap: Dp = md
}

/** Corner radii. */
internal object PanelRadius {
    val chip: Dp = 12.dp
    val control: Dp = 14.dp
    val button: Dp = 18.dp
    val card: Dp = 20.dp
    val banner: Dp = 22.dp
    val pill: Dp = 999.dp
}

/** Fixed chrome sizes. */
internal object PanelSize {
    val topBar: Dp = 56.dp
    val navBar: Dp = 64.dp
    val minTouch: Dp = 48.dp
    val button: Dp = 56.dp
    val navIcon: Dp = 26.dp
    val navIndicatorWidth: Dp = 36.dp
    val navIndicatorHeight: Dp = 3.dp
    val bannerHeight: Dp = 104.dp
    val statusChip: Dp = 36.dp
}

/** Motion. Quanto mais vezes por dia, menos animação. */
internal object PanelMotion {
    const val PressMillis = 120
    const val NavColorMillis = 150
    const val BannerEnterMillis = 200
    const val BannerExitMillis = 150
    const val MenuMillis = 160
    const val ChromeFadeMillis = 200

    /** Fade that survives reduced motion: it informs, it does not decorate. */
    const val ReducedFadeMillis = 100

    const val PressedScale = 0.96f
    const val BannerRiseDp = 12

    /** How long the media page keeps its controls after a touch. */
    const val MediaChromeHideMillis = 4_000L

    val EaseOut = CubicBezierEasing(0.23f, 1f, 0.32f, 1f)

    /** Duration to use for [normal], honouring reduced motion. */
    fun duration(reduced: Boolean, normal: Int): Int = if (reduced) 0 else normal

    /** Duration of an appearing element: shortened to a plain fade when motion is reduced. */
    fun fade(reduced: Boolean, normal: Int): Int = if (reduced) ReducedFadeMillis else normal
}
