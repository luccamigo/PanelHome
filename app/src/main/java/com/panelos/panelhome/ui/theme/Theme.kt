package com.panelos.panelhome.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider

private val PanelColors = darkColorScheme(
    primary = PanelAccent,
    onPrimary = PanelOnAccent,
    primaryContainer = PanelAccentSoft,
    onPrimaryContainer = PanelAccent,
    // One accent only: the old lilac secondary is retired. Anything that still
    // asks for "secondary" (second chart series, alternating blocks) gets muted grey.
    secondary = PanelTextMuted,
    onSecondary = PanelOnAccent,
    background = PanelBackground,
    onBackground = PanelText,
    surface = PanelSurface,
    onSurface = PanelText,
    surfaceContainer = PanelSurface,
    surfaceContainerHigh = PanelSurfaceElevated,
    onSurfaceVariant = PanelTextMuted,
    outline = PanelOutline,
    outlineVariant = PanelHairline,
    error = PanelError,
    onError = PanelOnAccent,
)

@Composable
fun PanelHomeTheme(content: @Composable () -> Unit) {
    val reducedMotion = rememberPanelReducedMotion()
    CompositionLocalProvider(LocalPanelReducedMotion provides reducedMotion) {
        MaterialTheme(
            colorScheme = PanelColors,
            typography = PanelTypography,
            content = content,
        )
    }
}
