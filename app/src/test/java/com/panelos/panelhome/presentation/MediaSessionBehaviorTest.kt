package com.panelos.panelhome.presentation

import android.view.Surface
import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264PacketHeader
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.protocol.CpuTelemetry
import com.panelos.panelhome.domain.protocol.DesktopCommand
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import com.panelos.panelhome.domain.protocol.DesktopStateMessage
import com.panelos.panelhome.domain.protocol.GpuTelemetry
import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.MemoryTelemetry
import com.panelos.panelhome.domain.protocol.StorageTelemetry
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.ToggleVolumeMuteCommand
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

internal class MediaSessionBehaviorTest : PanelSessionTestFixture() {
    @Test
    fun mediaEventAppliesMetadataProgressAndCapabilities() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                connectedMedia(
                    mediaId = "media-1",
                    sourceApp = "Spotify",
                    album = "OutRun",
                    isPlaying = true,
                    positionMs = 125_000,
                    durationMs = 270_000,
                    canPrevious = false,
                ),
            ),
        )

        val media = coordinator.uiState.value.panelData.media
        val track = requireNotNull(media.track)
        assertTrue(media.available)
        assertEquals("media-1", track.mediaId)
        assertEquals("Spotify", track.sourceApp)
        assertEquals("Nightcall", track.title)
        assertEquals("Kavinsky", track.artist)
        assertEquals("OutRun", track.album)
        assertTrue(media.isPlaying)
        assertEquals(125_000, media.progress.positionMs)
        assertEquals(270_000, media.progress.durationMs)
        assertTrue(media.capabilities.canPlayPause)
        assertFalse(media.capabilities.canPrevious)
        assertTrue(media.capabilities.canNext)
    }

    @Test
    fun unavailableMediaEventClearsEveryMediaField() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "media-1")),
        )
        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-1",
                artwork = artwork("media-1"),
            ),
        )

        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                MediaStateMessage(
                    available = false,
                    mediaId = "stale-id",
                    title = "Stale title",
                    artist = "Stale artist",
                    isPlaying = true,
                    positionMs = 500,
                    durationMs = 1_000,
                    canPlayPause = true,
                    canPrevious = true,
                    canNext = true,
                ),
            ),
        )

        val media = coordinator.uiState.value.panelData.media
        assertFalse(media.available)
        assertNull(media.track)
        assertFalse(media.isPlaying)
        assertEquals(0, media.progress.positionMs)
        assertEquals(0, media.progress.durationMs)
        assertFalse(media.capabilities.canPlayPause)
        assertFalse(media.capabilities.canPrevious)
        assertFalse(media.capabilities.canNext)
        assertNull(media.artwork)
    }

    @Test
    fun mediaProgressIsNormalizedToValidRange() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                connectedMedia(positionMs = 2_000, durationMs = 1_000),
            ),
        )
        assertEquals(1_000, coordinator.uiState.value.panelData.media.progress.positionMs)

        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                connectedMedia(positionMs = -50, durationMs = -10),
            ),
        )
        assertEquals(0, coordinator.uiState.value.panelData.media.progress.positionMs)
        assertEquals(0, coordinator.uiState.value.panelData.media.progress.durationMs)
    }

    @Test
    fun matchingArtworkIsAppliedAndPreservedForSameMediaId() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "media-1")),
        )
        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-1",
                artwork = artwork("media-1"),
            ),
        )

        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                connectedMedia(mediaId = "media-1", positionMs = 1_000),
            ),
        )

        val storedArtwork = requireNotNull(coordinator.uiState.value.panelData.media.artwork)
        assertEquals("media-1", storedArtwork.mediaId)
        assertArrayEquals(byteArrayOf(1, 2, 3), storedArtwork.bytes)
    }

    @Test
    fun artworkForDifferentMediaIsIgnoredAndTrackChangeClearsOldArtwork() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "media-1")),
        )
        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-2",
                artwork = artwork("media-2"),
            ),
        )
        assertNull(coordinator.uiState.value.panelData.media.artwork)

        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-1",
                artwork = artwork("media-1"),
            ),
        )
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "media-2")),
        )

        assertNull(coordinator.uiState.value.panelData.media.artwork)
    }

    @Test
    fun invalidArtworkEventClearsArtworkWithoutChangingMedia() {
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "media-1")),
        )
        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-1",
                artwork = artwork("media-1"),
            ),
        )

        connection.emit(
            PanelConnectionEvent.MediaArtworkReceived(
                mediaId = "media-1",
                artwork = null,
            ),
        )

        val media = coordinator.uiState.value.panelData.media
        assertTrue(media.available)
        assertEquals("media-1", media.track?.mediaId)
        assertNull(media.artwork)
    }

    @Test
    fun disconnectionClearsRemoteMediaAndTelemetry() {
        connection.emit(
            PanelConnectionEvent.Connected(connectionId = 1L, hostName = "DEV-PC"),
        )
        connection.emit(
            PanelConnectionEvent.TelemetryReceived(
                TelemetryMessage(
                    cpu = CpuTelemetry(
                        name = "CPU",
                        temperatureC = 55.0,
                        usagePercent = 20.0,
                        clockGhz = 4.0,
                        powerW = 50.0,
                    ),
                    gpu = GpuTelemetry(
                        name = "GPU",
                        temperatureC = 48.0,
                        memoryUsedGb = 4.0,
                    ),
                    memory = MemoryTelemetry(usedGb = 12.0, totalGb = 32.0),
                    storage = listOf(StorageTelemetry(name = "SSD")),
                ),
            ),
        )
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia(mediaId = "remote")),
        )

        connection.emit(PanelConnectionEvent.Disconnected)

        val state = coordinator.uiState.value
        assertEquals(ConnectionStatus.DISCONNECTED, state.connectionStatus)
        assertNull(state.hostName)
        assertEquals(MockPanelData.initial, state.panelData)
        assertNull(state.panelData.cpu.name)
        assertNull(state.panelData.cpu.temperatureCelsius)
        assertNull(state.panelData.cpu.usagePercent)
        assertNull(state.panelData.cpu.frequencyGhz)
        assertNull(state.panelData.cpu.powerWatts)
        assertNull(state.panelData.gpu.name)
        assertNull(state.panelData.gpu.memoryUsedGb)
        assertNull(state.panelData.ram.usedGb)
        assertTrue(state.panelData.storage.isEmpty())
        assertFalse(state.panelData.media.available)
        assertNull(state.panelData.media.track)
        assertNull(state.panelData.media.artwork)
    }

    @Test
    fun connectedSupportedMediaControlsSendProtocolCommands() {
        connection.emit(
            PanelConnectionEvent.Connected(connectionId = 1L, hostName = "DEV-PC"),
        )
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(connectedMedia()),
        )

        coordinator.previousTrack()
        coordinator.togglePlayback()
        coordinator.nextTrack()

        assertEquals(
            listOf(MediaCommand.PREVIOUS, MediaCommand.PLAY_PAUSE, MediaCommand.NEXT),
            connection.sentCommands,
        )
    }

    @Test
    fun unavailableMediaDoesNotSendCommands() {
        connection.emit(
            PanelConnectionEvent.Connected(connectionId = 1L, hostName = "DEV-PC"),
        )

        coordinator.previousTrack()
        coordinator.togglePlayback()
        coordinator.nextTrack()

        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun mediaCapabilitiesGuardIndividualCommands() {
        connection.emit(
            PanelConnectionEvent.Connected(connectionId = 1L, hostName = "DEV-PC"),
        )
        connection.emit(
            PanelConnectionEvent.MediaStateReceived(
                connectedMedia(
                    canPlayPause = false,
                    canPrevious = false,
                    canNext = true,
                ),
            ),
        )

        coordinator.previousTrack()
        coordinator.togglePlayback()
        coordinator.nextTrack()

        assertEquals(listOf(MediaCommand.NEXT), connection.sentCommands)
    }

    @Test
    fun negotiatedVolumeStateUpdatesUiAndClampsPercent() {
        connectWithVolume()

        connection.emit(
            PanelConnectionEvent.VolumeStateReceived(
                VolumeStateMessage(
                    available = true,
                    volumePercent = 172,
                    isMuted = true,
                ),
            ),
        )

        val volume = coordinator.uiState.value.panelData.volume
        assertTrue(volume.available)
        assertEquals(100, volume.volumePercent)
        assertTrue(volume.isMuted)
        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun volumeStateWithoutNegotiatedFeatureRemainsUnavailable() {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = 1L,
                hostName = "LEGACY-PC",
            ),
        )

        connection.emit(
            PanelConnectionEvent.VolumeStateReceived(
                VolumeStateMessage(
                    available = true,
                    volumePercent = 72,
                ),
            ),
        )

        assertFalse(coordinator.uiState.value.panelData.volume.available)
    }

    @Test
    fun volumeCommandsClampEndpointsAndToggleMute() {
        connectWithVolume()
        emitAvailableVolume()

        coordinator.setVolume(-10)
        coordinator.finishVolumeChange(130)
        coordinator.toggleVolumeMute()

        assertEquals(
            listOf(
                SetVolumeCommand(0),
                SetVolumeCommand(100),
                ToggleVolumeMuteCommand,
            ),
            connection.sentCommands,
        )
    }

    @Test
    fun unavailableOrDisconnectedVolumeDoesNotSendCommands() {
        connectWithVolume()
        connection.emit(
            PanelConnectionEvent.VolumeStateReceived(
                VolumeStateMessage(
                    available = false,
                    volumePercent = 72,
                    isMuted = true,
                ),
            ),
        )

        coordinator.setVolume(40)
        coordinator.finishVolumeChange(40)
        coordinator.toggleVolumeMute()
        assertTrue(connection.sentCommands.isEmpty())

        emitAvailableVolume()
        connection.emit(PanelConnectionEvent.Disconnected)
        coordinator.setVolume(80)
        coordinator.finishVolumeChange(80)
        coordinator.toggleVolumeMute()

        assertTrue(connection.sentCommands.isEmpty())
        assertFalse(coordinator.uiState.value.panelData.volume.available)
    }
}
