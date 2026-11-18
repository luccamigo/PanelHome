package com.panelos.panelhome.ui.ambient

import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.GpuMetrics
import com.panelos.panelhome.domain.model.MediaProgress
import com.panelos.panelhome.domain.model.MediaTrack
import com.panelos.panelhome.domain.model.RamMetrics
import kotlin.math.abs
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AmbientPresentationTest {
    @Test
    fun availableMediaIncludesTrackAndNormalizedProgress() {
        val media = MockPanelData.initial.media.copy(
            available = true,
            track = MediaTrack(
                mediaId = "track-1",
                sourceApp = "Player",
                title = " Midnight City ",
                artist = " M83 ",
                album = null,
            ),
            progress = MediaProgress(positionMs = 72_000L, durationMs = 100_000L),
        )

        val result = ambientPresentation(
            MockPanelData.initial.copy(media = media),
        ).media

        requireNotNull(result)
        assertEquals("Midnight City", result.title)
        assertEquals("M83", result.artist)
        assertEquals(0.72f, requireNotNull(result.progress), 0.0001f)
    }

    @Test
    fun unavailableOrTracklessMediaIsOmitted() {
        val staleTrack = MediaTrack(
            mediaId = "stale",
            sourceApp = null,
            title = "Stale title",
            artist = "Stale artist",
            album = null,
        )
        val unavailable = MockPanelData.initial.media.copy(
            available = false,
            track = staleTrack,
        )
        val trackless = MockPanelData.initial.media.copy(
            available = true,
            track = null,
        )

        assertNull(ambientMediaPresentation(unavailable))
        assertNull(ambientMediaPresentation(trackless))
    }

    @Test
    fun missingOrNonFiniteMetricsRemainUnavailable() {
        val result = ambientPresentation(
            MockPanelData.initial.copy(
                cpu = CpuMetrics(temperatureCelsius = Double.NaN),
                gpu = GpuMetrics(temperatureCelsius = Double.POSITIVE_INFINITY),
                ram = RamMetrics(usagePercent = null),
            ),
        ).metrics

        assertNull(result.cpuTemperatureCelsius)
        assertNull(result.gpuTemperatureCelsius)
        assertNull(result.ramUsagePercent)
    }

    @Test
    fun availableMetricsArePreserved() {
        val result = ambientPresentation(
            MockPanelData.initial.copy(
                cpu = CpuMetrics(temperatureCelsius = 48.5),
                gpu = GpuMetrics(temperatureCelsius = 45.25),
                ram = RamMetrics(usagePercent = 36.0),
            ),
        ).metrics

        assertEquals(48.5, requireNotNull(result.cpuTemperatureCelsius), 0.0)
        assertEquals(45.25, requireNotNull(result.gpuTemperatureCelsius), 0.0)
        assertEquals(36.0, requireNotNull(result.ramUsagePercent), 0.0)
    }

    @Test
    fun progressIsClampedAndRequiresPositiveDuration() {
        assertEquals(
            0f,
            requireNotNull(
                normalizedAmbientProgress(
                    MediaProgress(positionMs = -1L, durationMs = 1_000L),
                ),
            ),
            0f,
        )
        assertEquals(
            1f,
            requireNotNull(
                normalizedAmbientProgress(
                    MediaProgress(positionMs = 2_000L, durationMs = 1_000L),
                ),
            ),
            0f,
        )
        assertNull(
            normalizedAmbientProgress(
                MediaProgress(positionMs = 0L, durationMs = 0L),
            ),
        )
    }

    @Test
    fun burnInOffsetsAreSmallAndRepeatDeterministically() {
        val firstCycle = AMBIENT_BURN_IN_OFFSETS.indices.map(::ambientBurnInOffsetForStep)
        val secondCycle = AMBIENT_BURN_IN_OFFSETS.indices.map { step ->
            ambientBurnInOffsetForStep(step + AMBIENT_BURN_IN_OFFSETS.size)
        }

        assertEquals(firstCycle, secondCycle)
        assertTrue(firstCycle.distinct().size > 1)
        assertTrue(firstCycle.all { abs(it.xDp) <= 5 && abs(it.yDp) <= 5 })
        assertEquals(firstCycle.last(), ambientBurnInOffsetForStep(-1))
    }
}
