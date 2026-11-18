package com.panelos.panelhome.ui.dashboard

import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.VolumeState
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DashboardVolumeTest {
    @Test
    fun sliderRequiresAConnectedHostAndAvailableVolume() {
        val availableVolume = VolumeState(available = true, volumePercent = 72)

        assertTrue(
            isVolumeControlEnabled(ConnectionStatus.CONNECTED, availableVolume),
        )
        assertFalse(
            isVolumeControlEnabled(ConnectionStatus.DISCONNECTED, availableVolume),
        )
        assertFalse(
            isVolumeControlEnabled(
                ConnectionStatus.CONNECTED,
                VolumeState(available = false, volumePercent = 72),
            ),
        )
    }
}
