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

internal class PanelSessionCoordinatorTest : PanelSessionTestFixture() {
    @Test
    fun deviceProfileIsStoredWithoutBeingSent() {
        val profile = DeviceProfile(
            displayWidth = 2560,
            displayHeight = 1600,
            refreshRate = 120f,
        )

        coordinator.updateDeviceProfile(profile)

        assertEquals(profile, coordinator.uiState.value.deviceProfile)
        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun dashboardTransitionsToSettings() {
        coordinator.openSettings()

        assertEquals(PanelMode.SETTINGS, coordinator.uiState.value.mode)
    }

    @Test
    fun settingsTransitionsToDashboard() {
        coordinator.openSettings()
        coordinator.returnToDashboard()

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
    }

    @Test
    fun dashboardTransitionsToAmbient() {
        coordinator.enterAmbient()

        assertEquals(PanelMode.AMBIENT, coordinator.uiState.value.mode)
    }

    @Test
    fun ambientTransitionsToDashboard() {
        coordinator.enterAmbient()
        coordinator.returnToDashboard()

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
    }

    @Test
    fun connectionEventsUpdateStatusAndHostName() {
        connection.emit(PanelConnectionEvent.Connecting)
        assertEquals(ConnectionStatus.CONNECTING, coordinator.uiState.value.connectionStatus)

        connection.emit(
            PanelConnectionEvent.Connected(connectionId = 1L, hostName = "DEV-PC"),
        )

        assertEquals(ConnectionStatus.CONNECTED, coordinator.uiState.value.connectionStatus)
        assertEquals("DEV-PC", coordinator.uiState.value.hostName)
    }


    @Test
    fun commandsCarryTheConnectionIdThatProducedTheConnectedState() {
        connectWithDesktop(connectionId = 73L)

        coordinator.openDesktop()

        assertEquals(listOf(73L), connection.sentConnectionIds)
    }
}
