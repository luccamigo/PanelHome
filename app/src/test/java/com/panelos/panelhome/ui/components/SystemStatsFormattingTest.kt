package com.panelos.panelhome.ui.components

import java.util.Locale
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SystemStatsFormattingTest {
    @Test
    fun temperatureUsesUnitAndPreservesRealZero() {
        assertEquals("52.0 °C", formatTemperatureCelsius(52.0, Locale.US))
        assertEquals("0.0 °C", formatTemperatureCelsius(0.0, Locale.US))
    }

    @Test
    fun frequencyUsesGhzWithTwoDecimalPlaces() {
        assertEquals("4.25 GHz", formatFrequencyGhz(4.25, Locale.US))
    }

    @Test
    fun unavailableOrNonFiniteMetricsUseEmDash() {
        assertEquals(METRIC_UNAVAILABLE, formatTemperatureCelsius(null, Locale.US))
        assertEquals(METRIC_UNAVAILABLE, formatFrequencyGhz(null, Locale.US))
        assertEquals(METRIC_UNAVAILABLE, formatUsagePercent(Double.NaN, Locale.US))
        assertEquals(METRIC_UNAVAILABLE, formatFrequencyGhz(Double.POSITIVE_INFINITY, Locale.US))
    }

    @Test
    fun memoryKeepsUnavailableCapacityDistinctFromZero() {
        assertEquals("11.4 / — GB", formatMemoryUsage(11.4, null, Locale.US))
        assertEquals("0.0 / 32.0 GB", formatMemoryUsage(0.0, 32.0, Locale.US))
        assertEquals(METRIC_UNAVAILABLE, formatMemoryUsage(null, null, Locale.US))
    }

    @Test
    fun presentVramFormatsCapacityAndProgress() {
        assertTrue(hasVramCapacity(3.2, 8.0))
        assertEquals("3.2 / 8.0 GB", formatVramUsage(3.2, 8.0, Locale.US))
        assertEquals(0.4f, requireNotNull(normalizedUsageProgress(40.0)), 0.0f)
    }

    @Test
    fun absentOrInvalidVramUsesDashAndHasNoProgress() {
        assertFalse(hasVramCapacity(null, null))
        assertFalse(hasVramCapacity(0.0, 0.0))
        assertFalse(hasVramCapacity(null, 8.0))
        assertEquals(METRIC_UNAVAILABLE, formatVramUsage(null, null, Locale.US))
        assertEquals(METRIC_UNAVAILABLE, formatVramUsage(0.0, 0.0, Locale.US))
        assertNull(normalizedUsageProgress(null))
    }

    @Test
    fun vramPercentIsDerivedFromCapacityNotFromAReportedField() {
        // 1228 MiB / 8151 MiB, the values nvidia-smi reports for this GPU.
        val used = 1228.0 / 1024.0
        val total = 8151.0 / 1024.0
        assertEquals(15.07, requireNotNull(vramUsagePercent(used, total)), 0.01)
        assertEquals(25.0, requireNotNull(vramUsagePercent(2.0, 8.0)), 0.0)
        assertEquals(0.0, requireNotNull(vramUsagePercent(0.0, 8.0)), 0.0)
    }

    @Test
    fun vramPercentIsAbsentWhenCapacityIsUnknown() {
        assertNull(vramUsagePercent(null, null))
        assertNull(vramUsagePercent(2.0, null))
        assertNull(vramUsagePercent(null, 8.0))
        assertNull(vramUsagePercent(2.0, 0.0))
        assertNull(vramUsagePercent(Double.NaN, 8.0))
    }

    @Test
    fun vramPercentNeverExceedsFullEvenIfUsedOvershoots() {
        assertEquals(100.0, requireNotNull(vramUsagePercent(8.1, 8.0)), 0.0)
    }

    @Test
    fun progressIsClampedToTheIndicatorRange() {
        assertEquals(0.0f, requireNotNull(normalizedUsageProgress(-10.0)), 0.0f)
        assertEquals(1.0f, requireNotNull(normalizedUsageProgress(150.0)), 0.0f)
        assertNull(normalizedUsageProgress(Double.NaN))
    }
}
