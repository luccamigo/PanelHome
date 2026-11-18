package com.panelos.panelhome.ui.components

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.interaction.InteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelMotion

/**
 * Touch response: the element shrinks to 0.96 while pressed and comes back on
 * release (120 ms, ease-out). Under reduced motion there is no scale; the
 * pressed colour change carried by the caller is the only feedback.
 */
@Composable
internal fun Modifier.pressScale(
    interactionSource: InteractionSource,
    pressedScale: Float = PanelMotion.PressedScale,
): Modifier {
    val reduced = LocalPanelReducedMotion.current
    val pressed by interactionSource.collectIsPressedAsState()
    val scale by animateFloatAsState(
        targetValue = if (pressed && !reduced) pressedScale else 1f,
        animationSpec = tween(PanelMotion.PressMillis, easing = PanelMotion.EaseOut),
        label = "pressScale",
    )
    return graphicsLayer { scaleX = scale; scaleY = scale }
}
