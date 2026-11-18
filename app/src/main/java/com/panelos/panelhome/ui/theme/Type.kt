package com.panelos.panelhome.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.sp

/** Tabular figures: digits keep their width, so a changing number never dances. */
private const val TABULAR = "tnum"

/**
 * Instrument type scale. Roboto from the system; nothing functional is below
 * 14 sp (it is read at arm's length). Numeric styles are tabular.
 */
internal object PanelType {
    val numeralXL = TextStyle(fontSize = 118.sp, lineHeight = 118.sp, fontWeight = FontWeight.ExtraLight, fontFeatureSettings = TABULAR, letterSpacing = (-1).sp)
    val numeralL = TextStyle(fontSize = 56.sp, lineHeight = 56.sp, fontWeight = FontWeight.Light, fontFeatureSettings = TABULAR, letterSpacing = (-1).sp)
    val numeralM = TextStyle(fontSize = 44.sp, lineHeight = 48.sp, fontWeight = FontWeight.Light, fontFeatureSettings = TABULAR, letterSpacing = (-1).sp)
    val numeralS = TextStyle(fontSize = 28.sp, lineHeight = 32.sp, fontWeight = FontWeight.Light, fontFeatureSettings = TABULAR)
    val clock = TextStyle(fontSize = 32.sp, lineHeight = 32.sp, fontWeight = FontWeight.Light, fontFeatureSettings = TABULAR)
    val title = TextStyle(fontSize = 26.sp, lineHeight = 32.sp, fontWeight = FontWeight.SemiBold)
    val cardTitle = TextStyle(fontSize = 22.sp, lineHeight = 28.sp, fontWeight = FontWeight.SemiBold)
    val label = TextStyle(fontSize = 16.sp, lineHeight = 20.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.8.sp)
    val body = TextStyle(fontSize = 16.sp, lineHeight = 22.sp, fontWeight = FontWeight.Normal)
    val value = TextStyle(fontSize = 18.sp, lineHeight = 24.sp, fontWeight = FontWeight.Medium)
    val caption = TextStyle(fontSize = 14.sp, lineHeight = 18.sp, fontWeight = FontWeight.Normal, fontFeatureSettings = TABULAR)

    /** Chart axis marks only: decorative, so the one place below 14 sp. */
    val axis = TextStyle(fontSize = 12.sp, lineHeight = 16.sp, fontWeight = FontWeight.Normal, fontFeatureSettings = TABULAR)

    /** The unit next to a number is 40% of it, medium weight. */
    fun unitSize(numeral: TextUnit): TextUnit = (numeral.value * 0.4f).sp

    /** Smallest functional text size. */
    const val MinFunctionalSp = 14f
}

internal val PanelTypography = Typography(
    displayLarge = TextStyle(fontSize = 64.sp, lineHeight = 68.sp, fontWeight = FontWeight.Bold, fontFeatureSettings = TABULAR),
    displayMedium = TextStyle(fontSize = 48.sp, lineHeight = 52.sp, fontWeight = FontWeight.Bold, fontFeatureSettings = TABULAR),
    headlineLarge = PanelType.title.copy(fontSize = 32.sp, lineHeight = 38.sp),
    headlineMedium = PanelType.title,
    titleLarge = PanelType.cardTitle,
    titleMedium = PanelType.value,
    bodyLarge = TextStyle(fontSize = 18.sp, lineHeight = 26.sp, fontWeight = FontWeight.Normal),
    bodyMedium = PanelType.body,
    bodySmall = PanelType.caption,
    labelLarge = TextStyle(fontSize = 16.sp, lineHeight = 20.sp, fontWeight = FontWeight.SemiBold),
    // Material's defaults are 12 sp; nothing functional may be that small.
    labelMedium = PanelType.caption,
    labelSmall = PanelType.caption,
)
