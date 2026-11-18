package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class MediaControllerTest {
    private val sent = mutableListOf<PanelCommand>()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val controller = MediaController(scope, { sent += it; true })

    @After
    fun tearDown() {
        controller.close()
        scope.cancel()
    }

    @Test
    fun mediaCommandsRespectAvailabilityAndCapabilities() {
        controller.onConnected(emptySet())
        controller.applyMediaState(
            MediaStateMessage(
                available = true,
                mediaId = "track",
                canPrevious = false,
                canPlayPause = true,
                canNext = true,
            ),
        )

        controller.previousTrack()
        controller.togglePlayback()
        controller.nextTrack()

        assertEquals(listOf(MediaCommand.PLAY_PAUSE, MediaCommand.NEXT), sent)
    }

    @Test
    fun volumeIsNegotiatedClampedAndLatestFinishWins() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_VOLUME))
        controller.applyVolume(VolumeStateMessage(available = true, volumePercent = 172))

        assertEquals(100, controller.state.value.volume.volumePercent)
        controller.setVolume(-10)
        controller.finishVolumeChange(130)

        assertEquals(listOf(SetVolumeCommand(0), SetVolumeCommand(100)), sent)
    }

    @Test
    fun disconnectClearsRemoteMediaAndVolume() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_VOLUME))
        controller.applyMediaState(MediaStateMessage(available = true, mediaId = "track"))
        controller.applyVolume(VolumeStateMessage(available = true, volumePercent = 40))

        controller.onDisconnected()

        assertFalse(controller.state.value.media.available)
        assertFalse(controller.state.value.volume.available)
    }
}
