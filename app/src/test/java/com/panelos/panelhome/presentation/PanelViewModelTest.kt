package com.panelos.panelhome.presentation

import android.view.Surface
import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.protocol.DesktopCommand
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PanelViewModelTest {
    private val connection = ProjectionConnection()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val coordinator = PanelSessionCoordinator(
        connection = connection,
        videoConnection = ProjectionVideoConnection(),
        h264Decoder = ProjectionDecoder(),
        videoLogger = SilentVideoLogger,
        communicationScope = scope,
    )
    private val viewModel = PanelViewModel(projectionScope = scope).also {
        it.attachSession(coordinator)
    }

    @After
    fun tearDown() {
        coordinator.close()
        scope.cancel()
    }

    @Test
    fun presentationModesRemainViewModelIntents() {
        viewModel.openSettings()
        assertEquals(PanelMode.SETTINGS, viewModel.uiState.value.mode)

        viewModel.enterAmbient()
        assertEquals(PanelMode.AMBIENT, viewModel.uiState.value.mode)

        viewModel.returnToDashboard()
        assertEquals(PanelMode.DASHBOARD, viewModel.uiState.value.mode)
    }

    @Test
    fun deviceProfileIsPresentationStateAndSendsNoCommand() {
        val profile = DeviceProfile(2560, 1600, 120f)

        viewModel.updateDeviceProfile(profile)

        assertEquals(profile, viewModel.uiState.value.deviceProfile)
        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun desktopIntentDelegatesToTheSessionCoordinator() {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = 7,
                hostName = "DEV-PC",
                features = setOf(PanelProtocol.FEATURE_DESKTOP),
            ),
        )

        viewModel.openDesktop()

        assertEquals(PanelMode.DESKTOP, viewModel.uiState.value.mode)
        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)
        assertEquals(listOf(7L), connection.sentConnectionIds)
    }
}

private class ProjectionConnection : PanelConnection {
    private val mutableEvents = MutableSharedFlow<PanelConnectionEvent>(extraBufferCapacity = 8)
    override val events: Flow<PanelConnectionEvent> = mutableEvents
    val sentCommands = mutableListOf<PanelCommand>()
    val sentConnectionIds = mutableListOf<Long>()

    override suspend fun maintainConnection(): Nothing = awaitCancellation()

    override suspend fun send(command: PanelCommand, expectedConnectionId: Long) {
        sentCommands += command
        sentConnectionIds += expectedConnectionId
    }

    override fun close() = Unit

    fun emit(event: PanelConnectionEvent) {
        check(mutableEvents.tryEmit(event))
    }
}

private class ProjectionVideoConnection : VideoConnection {
    override suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    ): Nothing = awaitCancellation()

    override fun disconnect() = Unit
    override fun close() = Unit
}

private class ProjectionDecoder : H264DecoderController {
    override fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    ) = Unit

    override fun attachSurface(generation: Long, surface: Surface) = Unit
    override fun detachSurface(generation: Long, surface: Surface) = Unit
    override fun queuePacket(generation: Long, packet: H264VideoPacket) = Unit
    override fun stopSession() = Unit
    override fun close() = Unit
}
