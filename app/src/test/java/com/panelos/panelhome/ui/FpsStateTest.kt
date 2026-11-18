package com.panelos.panelhome.ui

import com.panelos.panelhome.domain.model.*
import com.panelos.panelhome.presentation.PanelUiState
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.ui.dashboard.joinReadings
import org.junit.Assert.*
import org.junit.Test

class FpsStateTest {
    @Test fun startsUnavailableWithoutInventingAReading() {
        assertEquals(FpsState.Unavailable, PanelUiState(PanelMode.DASHBOARD, MockPanelData.initial).fps)
    }
    @Test fun summariesIgnoreGapsAndInvalidValuesButKeepRealZero() {
        assertEquals(FpsSummary(0, 60, 120), fpsSummary(listOf(null, Float.NaN, -1f, 0f, 60f, 120f)))
        assertNull(fpsSummary(listOf(null, Float.POSITIVE_INFINITY, -1f)))
    }
    @Test fun chartCeilingIsStrictlyAboveTheLargestSample() {
        assertEquals(60f, fpsChartCeiling(emptyList()), 0f)
        assertEquals(180f, fpsChartCeiling(listOf(150f)), 0f)
        assertEquals(150f, fpsChartCeiling(listOf(null, 144f)), 0f)
    }
    @Test fun optionalTemperatureLeavesNoPlaceholderOrSeparator() {
        assertNull(joinReadings(null, null))
        assertEquals("4,11 GHz", joinReadings(null, "4,11 GHz"))
        assertEquals("62 °C · 4,11 GHz", joinReadings("62 °C", "4,11 GHz"))
    }
}
