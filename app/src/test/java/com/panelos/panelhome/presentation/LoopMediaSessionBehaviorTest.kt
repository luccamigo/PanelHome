package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.domain.protocol.LoopMediaChunkMessage
import com.panelos.panelhome.domain.protocol.LoopMediaKind
import com.panelos.panelhome.domain.protocol.LoopMediaRequestCommand
import com.panelos.panelhome.domain.protocol.LoopMediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.ProcessesOptionsCommand
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.io.encoding.Base64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Exercises the phase1 coordinator and its ordered queue, not just the media controller. */
class LoopMediaSessionBehaviorTest {
    private val directory = Files.createTempDirectory("panelos-loop-session-").toFile()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val connection = FakePanelConnection()
    private val session = PanelSessionCoordinator(
        connection = connection,
        videoConnection = FakeVideoConnection(),
        h264Decoder = FakeH264DecoderController(),
        videoLogger = SilentVideoLogger,
        loopMediaDirectory = directory,
        communicationScope = scope,
    )
    private val bytes = "GIF89a0123456789".toByteArray()
    private val id = MessageDigest.getInstance("SHA-256").digest(bytes)
        .joinToString("") { "%02x".format(it) }
    private val state = LoopMediaStateMessage(true, id, LoopMediaKind.GIF, bytes.size)

    @After
    fun cleanup() {
        scope.cancel()
        directory.deleteRecursively()
    }

    @Test
    fun negotiatedMediaUsesThePhase1QueueAndKeepsMonitoringAndNoticesAvailable() {
        connect(PanelProtocol.CLIENT_FEATURES.toSet(), 41L)
        assertTrue(session.uiState.value.monitoring.computerAvailable)
        assertTrue(session.uiState.value.monitoring.processesAvailable)
        assertTrue(session.uiState.value.integrations.noticesAvailable)
        assertTrue(connection.sentCommands.any { it is ProcessesOptionsCommand })

        connection.emit(PanelConnectionEvent.LoopMediaStateReceived(state))
        assertEquals(LoopMediaPhase.DOWNLOADING, session.uiState.value.loopMedia.phase)
        assertEquals(listOf(LoopMediaRequestCommand(id, 0)),
            connection.sentCommands.filterIsInstance<LoopMediaRequestCommand>())
        assertTrue(connection.sentConnectionIds.all { it == 41L })

        connection.emit(PanelConnectionEvent.LoopMediaChunkReceived(
            LoopMediaChunkMessage(id, 0, bytes.size, Base64.Default.encode(bytes), true),
        ))
        assertEquals(LoopMediaPhase.READY, session.uiState.value.loopMedia.phase)
        assertArrayEquals(bytes, session.uiState.value.loopMedia.file!!.readBytes())
        assertTrue(session.uiState.value.monitoring.computerAvailable)
        assertTrue(session.uiState.value.integrations.noticesAvailable)
    }

    @Test
    fun aPhase1HostWithoutMediaNeverReceivesARequest() {
        connect(PanelProtocol.CLIENT_FEATURES.toSet() - PanelProtocol.FEATURE_LOOP_MEDIA, 1L)
        connection.emit(PanelConnectionEvent.LoopMediaStateReceived(state))
        assertFalse(session.uiState.value.loopMedia.hasPage)
        assertTrue(connection.sentCommands.none { it is LoopMediaRequestCommand })
        assertTrue(session.uiState.value.monitoring.computerAvailable)
    }

    @Test
    fun disconnectCancelsThePartialDownloadAndReconnectUsesTheNewSession() {
        connect(PanelProtocol.CLIENT_FEATURES.toSet(), 1L)
        connection.emit(PanelConnectionEvent.LoopMediaStateReceived(state))
        connection.emit(PanelConnectionEvent.Disconnected)
        assertFalse(session.uiState.value.loopMedia.hasPage)
        assertTrue(directory.listFiles().orEmpty().isEmpty())

        connect(PanelProtocol.CLIENT_FEATURES.toSet(), 2L)
        connection.emit(PanelConnectionEvent.LoopMediaStateReceived(state))
        assertEquals(listOf(LoopMediaRequestCommand(id, 0), LoopMediaRequestCommand(id, 0)),
            connection.sentCommands.filterIsInstance<LoopMediaRequestCommand>())
        assertEquals(2L, connection.sentConnectionIds.last())
    }

    private fun connect(features: Set<String>, id: Long) {
        connection.emit(PanelConnectionEvent.Connected(id, "Fictitious phase1 host", features))
    }
}
