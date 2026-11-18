package com.panelos.panelhome.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.unit.TextUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Design tokens: the contrasts and sizes the DESIGN.md promises. */
class PanelTokensTest {
    private fun contrast(a: Color, b: Color): Double {
        val l1 = a.luminance().toDouble()
        val l2 = b.luminance().toDouble()
        return (maxOf(l1, l2) + 0.05) / (minOf(l1, l2) + 0.05)
    }

    @Test
    fun theColoursOfTheDesignKeepTheirValues() {
        assertEquals(Color(0xFF75D6FF), PanelAccent)
        assertEquals(Color(0xFFFF7A85), PanelError)
        assertEquals(Color(0xFF070B11), PanelBackground)
    }

    @Test
    fun textAndStateColoursReachAA_onSurface() {
        for (color in listOf(PanelText, PanelTextMuted, PanelAccent, PanelGood, PanelWarning, PanelError)) {
            assertTrue("$color on surface", contrast(color, PanelSurface) >= 4.5)
        }
        assertTrue(contrast(PanelOnAccent, PanelAccent) >= 4.5)
    }

    @Test
    fun noFunctionalTextIsBelow14sp() {
        val functional = listOf(
            PanelType.numeralL, PanelType.numeralM, PanelType.numeralS, PanelType.clock, PanelType.title,
            PanelType.cardTitle, PanelType.label, PanelType.body, PanelType.value, PanelType.caption,
        )
        for (style in functional) assertTrue(style.fontSize.value >= PanelType.MinFunctionalSp)
        for (style in listOf(PanelTypography.labelMedium, PanelTypography.labelSmall, PanelTypography.bodySmall)) {
            assertTrue(style.fontSize.value >= PanelType.MinFunctionalSp)
        }
    }

    @Test
    fun everyNumericStyleUsesTabularFigures() {
        val numeric = listOf(
            PanelType.numeralXL, PanelType.numeralL, PanelType.numeralM, PanelType.numeralS, PanelType.clock, PanelType.caption,
        )
        for (style in numeric) assertEquals("tnum", style.fontFeatureSettings)
    }

    @Test
    fun theUnitIsFortyPercentOfTheNumber() {
        val unit: TextUnit = PanelType.unitSize(PanelType.numeralL.fontSize)
        assertEquals(22.4f, unit.value, 0.01f)
    }

    @Test
    fun reducedMotionZeroesDurationsButKeepsTheShortFade() {
        assertEquals(0, PanelMotion.duration(reduced = true, normal = 200))
        assertEquals(200, PanelMotion.duration(reduced = false, normal = 200))
        assertEquals(PanelMotion.ReducedFadeMillis, PanelMotion.fade(reduced = true, normal = 200))
        assertEquals(200, PanelMotion.fade(reduced = false, normal = 200))
    }

    @Test
    fun nothingAnimatesLongerThan300ms() {
        for (ms in listOf(
            PanelMotion.PressMillis, PanelMotion.NavColorMillis, PanelMotion.BannerEnterMillis,
            PanelMotion.BannerExitMillis, PanelMotion.MenuMillis, PanelMotion.ChromeFadeMillis,
        )) assertTrue(ms <= 300)
    }

    @Test
    fun spacingFollowsTheFourDpScale() {
        for (dp in listOf(PanelSpacing.xs, PanelSpacing.sm, PanelSpacing.md, PanelSpacing.lg, PanelSpacing.xl, PanelSpacing.xxl)) {
            assertEquals(0f, dp.value % 4f, 0f)
        }
        assertTrue(PanelSize.minTouch.value >= 48f)
    }
}
