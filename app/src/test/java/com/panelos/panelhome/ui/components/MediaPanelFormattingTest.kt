package com.panelos.panelhome.ui.components

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class MediaPanelFormattingTest {
    @Test
    fun formatsDurationsForMediaControls() {
        assertEquals("0:00", formatMediaDuration(-1))
        assertEquals("1:05", formatMediaDuration(65_000))
        assertEquals("1:01:01", formatMediaDuration(3_661_000))
    }

    @Test
    fun muteStateUsesADistinctVolumeIcon() {
        assertNotEquals(volumeIcon(isMuted = false), volumeIcon(isMuted = true))
    }
}
