package com.panelos.panelhome.ui.monitoring

import org.junit.Assert.*
import org.junit.Test

class ChartDataTest {
    @Test fun absentSamplesAndDisconnectsSplitTheLine() {
        val points = listOf(point(0, 20.0), point(1_000, null), point(2_000, 50.0), point(10_000, 70.0))
        val paths = chartEnvelope(points, 100, 0, 10_000)
        assertEquals(3, paths.size)
        assertEquals(listOf(20.0, 50.0, 70.0), paths.flatten().map { it.value })
    }

    @Test fun pixelReductionRetainsBriefPeaksAndTroughs() {
        val points = (0..900).map { point(it.toLong(), if (it == 455) 99.0 else if (it == 456) 1.0 else 50.0) }
        val reduced = chartEnvelope(points, 10, 0, 901).flatten()
        assertTrue(reduced.size <= 20)
        assertEquals(99.0, reduced.maxOf { it.value!! }, 0.0)
        assertEquals(1.0, reduced.minOf { it.value!! }, 0.0)
    }

    @Test fun networkBothDirectionsUseTheSameNiceScale() {
        assertEquals(5_000.0, networkCeiling(listOf(ChartPoint(0, 0, 1_200.0, 4_000.0))), 0.0)
        assertEquals(1_000.0, networkCeiling(emptyList()), 0.0)
    }

    @Test fun summaryExcludesMissingValuesAndRetainsPeakTime() {
        val stats = summarize(listOf(point(0, 10.0), point(1, null), point(2, 30.0)))
        assertEquals(20.0, stats.average!!, 0.0)
        assertEquals(10.0, stats.minimum!!, 0.0)
        assertEquals(30.0, stats.maximum!!, 0.0)
        assertEquals(2L, stats.maximumAt)
        assertNull(summarize(emptyList()).average)
    }

    @Test fun inspectionInAnOfflineGapNeverRepeatsTheNearestOldValue() {
        val points = listOf(point(0, 20.0), point(10_000, 40.0))
        assertNull(selectedPoint(points, 5_000))
        assertEquals(40.0, selectedPoint(points, 10_010)?.value)
    }

    private fun point(at: Long, value: Double?) = ChartPoint(at, at, value)
}
