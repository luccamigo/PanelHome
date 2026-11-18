package com.panelos.panelhome.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SparklineTest {
    @Test
    fun aGapSplitsTheLineInsteadOfBridgingIt() {
        // A failed sample is never a straight line across the hole, nor a zero.
        val runs = sparklineRuns(listOf(1f, 2f, null, null, 5f, 6f, 7f))
        assertEquals(listOf(0..1, 4..6), runs)
    }

    @Test
    fun leadingAndTrailingGapsProduceNoRunThere() {
        assertEquals(listOf(1..2), sparklineRuns(listOf(null, 3f, 4f, null)))
        assertTrue(sparklineRuns(listOf(null, null)).isEmpty())
        assertTrue(sparklineRuns(emptyList<Float?>()).isEmpty())
    }

    @Test
    fun aSeriesWithoutGapsIsOneRun() {
        assertEquals(listOf(0..3), sparklineRuns(listOf(1f, 2f, 3f, 4f)))
    }

    @Test
    fun downsamplingKeepsAtMostOneSamplePerPixelAndTheNewestSample() {
        val values = (0 until 1000).toList()
        val sampled = downsample(values, 300)
        assertTrue(sampled.size <= 301)
        assertEquals(0, sampled.first())
        assertEquals(999, sampled.last())
    }

    @Test
    fun aShortSeriesIsLeftAlone() {
        val values = listOf(1, 2, 3)
        assertEquals(values, downsample(values, 300))
        assertEquals(values, downsample(values, 0))
        assertFalse(downsample((0 until 10).toList(), 3).isEmpty())
    }
}
