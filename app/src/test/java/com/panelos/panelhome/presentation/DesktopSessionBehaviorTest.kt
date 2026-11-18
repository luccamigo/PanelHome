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

internal class DesktopSessionBehaviorTest : PanelSessionTestFixture() {
    @Test
    fun failedBrokerStartReturnsToPanelWhenHostPublishesStopped() {
        connectWithDesktop()
        for (error in listOf(DesktopErrorCode.DRIVER_IO_ERROR, "broker_disconnected")) {
            coordinator.openDesktop()
            connection.emit(desktopState(DesktopStatus.ERROR, error))

            coordinator.returnToDashboard()

            assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
            assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
            assertEquals(listOf(DesktopCommand.START, DesktopCommand.STOP), connection.sentCommands.takeLast(2))
            connection.emit(desktopState(DesktopStatus.STOPPED))
            assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
            assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
            assertNull(coordinator.uiState.value.desktopErrorCode)
        }
    }

    @Test
    fun connectedDesktopFeatureStartsDesktopAndChangesMode() {
        connectWithDesktop()

        coordinator.openDesktop()

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STARTING, coordinator.uiState.value.desktopStatus)
        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)
    }

    @Test
    fun negotiatedTouchForRunningGenerationForwardsSingleTouchDown() {
        connectWithTouch()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 3))

        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 3))

        assertTrue(coordinator.uiState.value.desktopTouchAvailable)
        assertEquals(3L, coordinator.uiState.value.desktopTouchGeneration)
        assertEquals(
            listOf(
                DesktopCommand.START,
                TouchCommand(
                    action = TouchCommandAction.DOWN,
                    pointerId = 0,
                    x = 0.5,
                    y = 0.5,
                    generation = 3,
                ),
            ),
            connection.sentCommands,
        )
    }

    @Test
    fun legacyHostAndStaleTouchGenerationNeverForwardTouch() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 3))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 3))

        assertFalse(coordinator.uiState.value.desktopTouchAvailable)
        assertNull(coordinator.uiState.value.desktopTouchGeneration)
        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)

        connection.emit(PanelConnectionEvent.Disconnected)
        connectWithTouch(connectionId = 2)
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 7))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 6))

        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.START),
            connection.sentCommands,
        )
    }

    @Test
    fun desktopExitCancelsActiveTouchBeforeStopAndDisablesForwarding() {
        connectWithTouch()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 3))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 3))

        coordinator.returnToDashboard()

        assertNull(coordinator.uiState.value.desktopTouchGeneration)
        assertEquals(
            listOf(
                DesktopCommand.START,
                TouchCommand(
                    TouchCommandAction.DOWN,
                    pointerId = 0,
                    x = 0.5,
                    y = 0.5,
                    generation = 3,
                ),
                TouchCommand(
                    TouchCommandAction.CANCEL,
                    pointerId = 0,
                    generation = 3,
                ),
                DesktopCommand.STOP,
            ),
            connection.sentCommands,
        )
    }

    @Test
    fun newDesktopGenerationCancelsOldContactAndRequiresNewDown() {
        connectWithTouch()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 3))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 3))

        connection.emit(desktopState(DesktopStatus.RUNNING, generation = 4))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.MOVE, generation = 3))
        coordinator.onDesktopTouch(touchEvent(TouchCommandAction.DOWN, generation = 4))

        val touchCommands = connection.sentCommands.filterIsInstance<TouchCommand>()
        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.CANCEL, TouchCommandAction.DOWN),
            touchCommands.map(TouchCommand::action),
        )
        assertEquals(listOf(3L, 3L, 4L), touchCommands.map(TouchCommand::generation))
    }

    @Test
    fun desktopExitWaitsForStoppedBeforeReturningToDashboard() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))

        coordinator.returnToDashboard()

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.STOP),
            connection.sentCommands,
        )

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun disconnectedDesktopOpenDoesNothing() {
        coordinator.openDesktop()

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun unsupportedDesktopEntersErrorWithoutSendingCommand() {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = 1L,
                hostName = "LEGACY-PC",
            ),
        )

        coordinator.openDesktop()

        val state = coordinator.uiState.value
        assertEquals(PanelMode.DESKTOP, state.mode)
        assertEquals(DesktopStatus.ERROR, state.desktopStatus)
        assertEquals(DesktopErrorCode.UNSUPPORTED, state.desktopErrorCode)
        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun obsoleteRunningAndStartingStatesAreIgnoredWhileExitIsPending() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        coordinator.returnToDashboard()

        connection.emit(desktopState(DesktopStatus.STARTING))
        connection.emit(desktopState(DesktopStatus.RUNNING))

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun startErrorCanBeRetried() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.ERROR, "start_failed"))

        coordinator.retryDesktop()

        assertEquals(DesktopStatus.STARTING, coordinator.uiState.value.desktopStatus)
        assertNull(coordinator.uiState.value.desktopErrorCode)
        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.START),
            connection.sentCommands,
        )
    }

    @Test
    fun stopErrorRetriesStopAndStillWaitsForStopped() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        coordinator.returnToDashboard()
        connection.emit(desktopState(DesktopStatus.ERROR, "stop_failed"))

        coordinator.retryDesktop()

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.STOP, DesktopCommand.STOP),
            connection.sentCommands,
        )
    }

    @Test
    fun backAfterStopErrorRetriesStopAndStillWaitsForStopped() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        coordinator.returnToDashboard()
        connection.emit(desktopState(DesktopStatus.ERROR, DesktopErrorCode.STOP_FAILED))

        coordinator.returnToDashboard()

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.STOP, DesktopCommand.STOP),
            connection.sentCommands,
        )
    }

    @Test
    fun disconnectDuringDesktopReturnsDashboardWithConnectionErrorWithoutStop() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))

        connection.emit(PanelConnectionEvent.Disconnected)

        val state = coordinator.uiState.value
        assertEquals(PanelMode.DASHBOARD, state.mode)
        assertEquals(DesktopStatus.ERROR, state.desktopStatus)
        assertEquals(DesktopErrorCode.CONNECTION_LOST, state.desktopErrorCode)
        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)
    }

    @Test
    fun reconnectDoesNotAutoStartAndAcceptsStoppedSnapshot() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(PanelConnectionEvent.Disconnected)
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = 2L,
                hostName = "DEV-PC",
                features = setOf(PanelProtocol.FEATURE_DESKTOP),
            ),
        )

        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)
        assertEquals(DesktopStatus.ERROR, coordinator.uiState.value.desktopStatus)

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
        assertEquals(listOf(DesktopCommand.START), connection.sentCommands)
    }

    @Test
    fun desktopCommandsPreserveUiOrderDuringRapidExit() {
        connectWithDesktop()

        coordinator.openDesktop()
        coordinator.returnToDashboard()

        assertEquals(
            listOf(DesktopCommand.START, DesktopCommand.STOP),
            connection.sentCommands,
        )
        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun initialStoppedSnapshotDoesNotOverridePendingDesktopStart() {
        connectWithDesktop()
        coordinator.openDesktop()

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STARTING, coordinator.uiState.value.desktopStatus)

        connection.emit(desktopState(DesktopStatus.RUNNING))

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.RUNNING, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun initialStoppedSnapshotDoesNotCompleteRapidDesktopExit() {
        connectWithDesktop()
        coordinator.openDesktop()
        coordinator.returnToDashboard()

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DESKTOP, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun unexpectedStoppedStateReturnsRunningDesktopToDashboard() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))

        connection.emit(desktopState(DesktopStatus.STOPPED))

        assertEquals(PanelMode.DASHBOARD, coordinator.uiState.value.mode)
        assertEquals(DesktopStatus.STOPPED, coordinator.uiState.value.desktopStatus)
    }

    @Test
    fun hostDesktopStatesAndErrorsUpdateSeparateUiState() {
        connectWithDesktop()
        coordinator.openDesktop()

        connection.emit(desktopState(DesktopStatus.STARTING))
        assertEquals(DesktopStatus.STARTING, coordinator.uiState.value.desktopStatus)
        connection.emit(desktopState(DesktopStatus.RUNNING))
        assertEquals(DesktopStatus.RUNNING, coordinator.uiState.value.desktopStatus)
        connection.emit(desktopState(DesktopStatus.ERROR, "internal_error"))
        assertEquals(DesktopStatus.ERROR, coordinator.uiState.value.desktopStatus)
        assertEquals("internal_error", coordinator.uiState.value.desktopErrorCode)
    }

    @Test
    fun runningDesktopConnectsVideoThenDisplaysTheLatestFrame() {
        connectWithDesktop()
        coordinator.openDesktop()

        connection.emit(desktopState(DesktopStatus.RUNNING))
        assertEquals(1, videoConnection.sessionsStarted)
        assertEquals(DesktopVideoStatus.CONNECTING, coordinator.uiState.value.desktopVideo.status)

        videoConnection.signalConnected()
        assertEquals(
            DesktopVideoStatus.WAITING_FOR_FIRST_FRAME,
            coordinator.uiState.value.desktopVideo.status,
        )

        videoConnection.emitFrame(sequence = 12, value = 0x12)
        val video = coordinator.uiState.value.desktopVideo
        assertEquals(DesktopVideoStatus.FRAME_RECEIVED, video.status)
        assertEquals(12L, video.sequence)
        assertNull(video.presentedSequence)
        assertEquals(1, video.width)
        assertEquals(1, video.height)
        assertEquals(12L, videoFrameStore.withLatest { it.sequence })

        val generation = requireNotNull(video.presentationGeneration)
        coordinator.onVideoFrameDrawn(generation, 12)

        assertEquals(DesktopVideoStatus.DISPLAYING, coordinator.uiState.value.desktopVideo.status)
        assertEquals(12L, coordinator.uiState.value.desktopVideo.presentedSequence)
    }

    @Test
    fun h264SessionQueuesPacketsRendersAndStopsWithDesktopLifecycle() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))

        videoConnection.signalH264Connected()
        val generation = requireNotNull(
            coordinator.uiState.value.desktopVideo.presentationGeneration,
        )
        assertEquals(1, h264Decoder.sessionsStarted)
        assertEquals(DesktopVideoCodec.H264, coordinator.uiState.value.desktopVideo.codec)
        assertEquals(
            DesktopVideoStatus.WAITING_FOR_CODEC_CONFIG,
            coordinator.uiState.value.desktopVideo.status,
        )

        videoConnection.emitH264Packet(sequence = 31, isConfig = true)
        assertEquals(
            DesktopVideoStatus.WAITING_FOR_KEYFRAME,
            coordinator.uiState.value.desktopVideo.status,
        )
        videoConnection.emitH264Packet(sequence = 31, isKeyFrame = true)
        assertEquals(2, h264Decoder.packets.size)
        assertEquals(DesktopVideoStatus.FRAME_RECEIVED, coordinator.uiState.value.desktopVideo.status)

        h264Decoder.signalRendered(generation, 31)
        assertEquals(DesktopVideoStatus.DISPLAYING, coordinator.uiState.value.desktopVideo.status)
        assertEquals(31L, coordinator.uiState.value.desktopVideo.presentedSequence)

        coordinator.returnToDashboard()
        assertEquals(1, h264Decoder.sessionsStopped)
        assertEquals(DesktopVideoStatus.IDLE, coordinator.uiState.value.desktopVideo.status)
        assertEquals(1, videoConnection.sessionsDisconnected)
        assertEquals(VideoClientCloseReason.DESKTOP_EXIT, videoConnection.disconnectReasons.last())
    }

    @Test
    fun shortH264ReconnectPreservesLastPresentedFrameInRecoveringState() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalH264Connected()
        val generation = requireNotNull(
            coordinator.uiState.value.desktopVideo.presentationGeneration,
        )
        videoConnection.emitH264Packet(sequence = 41, isConfig = true)
        videoConnection.emitH264Packet(sequence = 41, isKeyFrame = true)
        h264Decoder.signalRendered(generation, 41)

        videoConnection.signalSessionEnded()

        val recovering = coordinator.uiState.value.desktopVideo
        assertEquals(DesktopVideoStatus.RECOVERING, recovering.status)
        assertEquals(41L, recovering.presentedSequence)
        assertEquals(generation, recovering.presentationGeneration)
    }

    @Test
    fun fatalH264DecoderErrorClosesWithDecoderReasonAndShowsError() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalH264Connected()

        h264Decoder.signalError(IllegalStateException("decoder failed"))

        assertEquals(
            DesktopVideoStatus.PRESENTATION_ERROR,
            coordinator.uiState.value.desktopVideo.status,
        )
        assertEquals(VideoClientCloseReason.DECODER_ERROR, videoConnection.disconnectReasons.last())
    }

    @Test
    fun presentationErrorIsVisibleAndKeepsLatestFrameForRetry() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalConnected()
        videoConnection.emitFrame(sequence = 21, value = 21)

        val generation = requireNotNull(
            coordinator.uiState.value.desktopVideo.presentationGeneration,
        )
        coordinator.onVideoRenderError(
            generation,
            21,
            "surface_lock",
            IllegalStateException("unavailable"),
        )

        assertEquals(
            DesktopVideoStatus.PRESENTATION_ERROR,
            coordinator.uiState.value.desktopVideo.status,
        )
        assertEquals(21L, videoFrameStore.withLatest { it.sequence })

        coordinator.onVideoFrameDrawn(generation + 1, 21)
        assertEquals(
            DesktopVideoStatus.PRESENTATION_ERROR,
            coordinator.uiState.value.desktopVideo.status,
        )

        coordinator.onVideoFrameDrawn(generation, 21)

        assertEquals(DesktopVideoStatus.DISPLAYING, coordinator.uiState.value.desktopVideo.status)
        assertEquals(21L, coordinator.uiState.value.desktopVideo.presentedSequence)
    }

    @Test
    fun staleVideoFrameCannotReplaceTheCurrentRendererState() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalConnected()
        videoConnection.emitFrame(sequence = 20, value = 20)

        videoConnection.emitFrame(sequence = 19, value = 19)

        assertEquals(20L, coordinator.uiState.value.desktopVideo.sequence)
        assertEquals(20L, videoFrameStore.withLatest { it.sequence })
    }

    @Test
    fun stoppingDesktopClosesVideoImmediatelyAndClearsRenderer() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalConnected()
        videoConnection.emitFrame(sequence = 5, value = 5)

        coordinator.returnToDashboard()

        assertEquals(DesktopStatus.STOPPING, coordinator.uiState.value.desktopStatus)
        assertEquals(DesktopVideoStatus.IDLE, coordinator.uiState.value.desktopVideo.status)
        assertNull(coordinator.uiState.value.desktopVideo.sequence)
        assertNull(videoFrameStore.withLatest { it.sequence })
        assertEquals(1, videoConnection.sessionsDisconnected)
    }

    @Test
    fun usbDisconnectStopsVideoClearsSurfaceAndReconnectDoesNotStartIt() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalConnected()
        videoConnection.emitFrame(sequence = 7, value = 7)

        connection.emit(PanelConnectionEvent.Disconnected)

        assertEquals(DesktopVideoStatus.IDLE, coordinator.uiState.value.desktopVideo.status)
        assertNull(videoFrameStore.withLatest { it.sequence })
        assertEquals(1, videoConnection.sessionsDisconnected)
        assertEquals(VideoClientCloseReason.RECONNECT, videoConnection.disconnectReasons.last())

        connectWithDesktop(connectionId = 2)
        connection.emit(desktopState(DesktopStatus.STOPPED))
        assertEquals(1, videoConnection.sessionsStarted)
    }

    @Test
    fun callbackFromOldVideoGenerationCannotOverwriteANewSession() {
        connectWithDesktop()
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        val obsoleteCallback = videoConnection.captureFrameCallback()

        connection.emit(PanelConnectionEvent.Disconnected)
        connectWithDesktop(connectionId = 2)
        connection.emit(desktopState(DesktopStatus.STOPPED))
        coordinator.openDesktop()
        connection.emit(desktopState(DesktopStatus.RUNNING))
        videoConnection.signalConnected()
        videoConnection.emitFrame(sequence = 1, value = 1)

        obsoleteCallback(videoConnection.frame(sequence = 999, value = 99))

        assertEquals(1L, coordinator.uiState.value.desktopVideo.sequence)
        assertEquals(1L, videoFrameStore.withLatest { it.sequence })
    }
}
