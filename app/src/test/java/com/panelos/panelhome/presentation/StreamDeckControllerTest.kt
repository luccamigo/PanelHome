package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import android.view.Surface
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.StreamDeckButtonMessage
import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import com.panelos.panelhome.domain.protocol.StreamDeckIconsRequestCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeResultMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.StreamDeckPageMessage
import java.io.File
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The panel's half of the Stream Deck: adopting a layout, asking for the icons
 * it does not have, and sending an id when a button is touched.
 */
class StreamDeckControllerTest {
    private val connection = FakeStreamDeckConnection()
    private val icons = RecordingIconStore()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val coordinator = PanelSessionCoordinator(
        connection = connection,
        videoConnection = IdleVideoConnection(),
        h264Decoder = IdleH264DecoderController(),
        videoLogger = SilentVideoLogger,
        streamDeckIcons = icons,
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
    fun aHostWithoutTheFeatureLeavesThePanelAsADashboard() {
        connect(features = setOf(PanelProtocol.FEATURE_DESKTOP))

        val streamDeck = viewModel.uiState.value.streamDeck
        assertFalse(streamDeck.available)
        assertEquals(0, streamDeck.carouselPageCount)
    }

    @Test
    fun aNegotiatedHostWithoutAProfileStillGetsTheEmptyPage() {
        connect()
        connection.emit(layoutEvent(revision = "0", pages = emptyList()))

        val streamDeck = viewModel.uiState.value.streamDeck
        assertTrue(streamDeck.available)
        assertEquals(0, streamDeck.pageCount)
        assertEquals(1, streamDeck.carouselPageCount)
    }

    @Test
    fun theFirstLayoutIsAdoptedAndItsMissingIconsAreRequested() {
        connect()

        connection.emit(defaultLayout())

        val streamDeck = viewModel.uiState.value.streamDeck
        assertEquals("rev-1", streamDeck.layout.revision)
        assertEquals(2, streamDeck.pageCount)
        assertEquals(2, streamDeck.carouselPageCount)
        assertEquals(
            listOf(StreamDeckIconsRequestCommand(listOf(ICON_ID))),
            connection.sentCommands,
        )
    }

    @Test
    fun anIconAlreadyCachedIsNotRequestedAgain() {
        icons.cached += ICON_ID
        connect()

        connection.emit(defaultLayout())

        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun aReceivedIconIsStoredAndMakesTheGridLookAgain() {
        connect()
        connection.emit(defaultLayout())
        val before = viewModel.uiState.value.streamDeck.iconGeneration

        connection.emit(
            PanelConnectionEvent.StreamDeckIconReceived(
                StreamDeckIconMessage(ICON_ID, available = true, mime = "image/png"),
            ),
        )

        assertEquals(listOf(ICON_ID), icons.stored)
        assertEquals(before + 1, viewModel.uiState.value.streamDeck.iconGeneration)
    }

    @Test
    fun aMalformedIconChangesNothing() {
        connect()
        connection.emit(defaultLayout())
        val before = viewModel.uiState.value.streamDeck.iconGeneration
        icons.accept = false

        connection.emit(
            PanelConnectionEvent.StreamDeckIconReceived(
                StreamDeckIconMessage(ICON_ID, available = true, mime = "image/jpeg"),
            ),
        )

        assertEquals(before, viewModel.uiState.value.streamDeck.iconGeneration)
    }

    @Test
    fun touchingAButtonSendsOnlyItsIdAndTheLayoutRevision() {
        connect()
        connection.emit(defaultLayout())
        connection.sentCommands.clear()

        viewModel.invokeStreamDeckButton("btn-media")

        assertEquals(
            listOf(StreamDeckInvokeCommand(buttonId = "btn-media", revision = "rev-1")),
            connection.sentCommands,
        )
        assertEquals("btn-media", viewModel.uiState.value.streamDeck.pendingButtonId)
    }

    @Test
    fun aDisabledButtonNeverSendsAnything() {
        connect()
        connection.emit(defaultLayout())
        connection.sentCommands.clear()

        viewModel.invokeStreamDeckButton("btn-off")

        assertTrue(connection.sentCommands.isEmpty())
        assertNull(viewModel.uiState.value.streamDeck.pendingButtonId)
    }

    @Test
    fun anUnknownButtonNeverSendsAnything() {
        connect()
        connection.emit(defaultLayout())
        connection.sentCommands.clear()

        viewModel.invokeStreamDeckButton("btn-inventado")

        assertTrue(connection.sentCommands.isEmpty())
    }

    @Test
    fun successIsOnlyShownWhenThePcConfirmsIt() {
        connect()
        connection.emit(defaultLayout())
        viewModel.invokeStreamDeckButton("btn-media")

        // Still pending: the panel never decides on its own that it worked.
        assertEquals("btn-media", viewModel.uiState.value.streamDeck.pendingButtonId)

        connection.emit(resultEvent("btn-media", success = true))

        val streamDeck = viewModel.uiState.value.streamDeck
        assertNull(streamDeck.pendingButtonId)
        assertNull(streamDeck.failedButtonId)
    }

    @Test
    fun aFailureIsMarkedDiscreetlyAndCanBeCleared() {
        connect()
        connection.emit(defaultLayout())
        viewModel.invokeStreamDeckButton("btn-media")

        connection.emit(
            resultEvent("btn-media", success = false, errorCode = "application_not_found"),
        )

        assertNull(viewModel.uiState.value.streamDeck.pendingButtonId)
        assertEquals("btn-media", viewModel.uiState.value.streamDeck.failedButtonId)

        viewModel.clearStreamDeckFailure()
        assertNull(viewModel.uiState.value.streamDeck.failedButtonId)
    }

    @Test
    fun aResultForAPressThePanelIsNoLongerWaitingOnIsIgnored() {
        connect()
        connection.emit(defaultLayout())
        viewModel.invokeStreamDeckButton("btn-media")

        connection.emit(resultEvent("btn-outro", success = false, errorCode = "x"))

        assertEquals("btn-media", viewModel.uiState.value.streamDeck.pendingButtonId)
        assertNull(viewModel.uiState.value.streamDeck.failedButtonId)
    }

    @Test
    fun aNewLayoutThatDropsThePressedButtonClearsThePendingPress() {
        connect()
        connection.emit(defaultLayout())
        viewModel.invokeStreamDeckButton("btn-media")

        connection.emit(layoutEvent(revision = "rev-2", pages = emptyList()))

        val streamDeck = viewModel.uiState.value.streamDeck
        assertEquals("rev-2", streamDeck.layout.revision)
        assertNull(streamDeck.pendingButtonId)
    }

    @Test
    fun anEditOnThePcReplacesTheLayoutWithoutAReconnect() {
        connect()
        connection.emit(defaultLayout())

        connection.emit(
            layoutEvent(
                revision = "rev-2",
                pages = listOf(
                    StreamDeckPageMessage(
                        id = "page-a",
                        name = "Renomeada",
                        rows = 2,
                        columns = 2,
                        buttons = listOf(
                            StreamDeckButtonMessage("btn-media", 0, 0, "Novo rótulo"),
                        ),
                    ),
                ),
            ),
        )

        val layout = viewModel.uiState.value.streamDeck.layout
        assertEquals("rev-2", layout.revision)
        assertEquals(1, layout.pages.size)
        assertEquals("Renomeada", layout.pages[0].name)
        assertEquals("Novo rótulo", layout.findButton("btn-media")?.label)
    }

    @Test
    fun reconnectingDropsTheOldLayoutAndWaitsForAFreshOne() {
        connect()
        connection.emit(defaultLayout())

        connection.emit(PanelConnectionEvent.Disconnected)

        val disconnected = viewModel.uiState.value.streamDeck
        assertFalse(disconnected.available)
        assertEquals(0, disconnected.pageCount)

        connect(connectionId = 2)
        assertTrue(viewModel.uiState.value.streamDeck.available)
        assertEquals(0, viewModel.uiState.value.streamDeck.pageCount)

        connection.emit(defaultLayout())
        assertEquals(2, viewModel.uiState.value.streamDeck.pageCount)
    }

    @Test
    fun aButtonCannotBeInvokedWhileTheHostIsGone() {
        connect()
        connection.emit(defaultLayout())
        connection.emit(PanelConnectionEvent.Disconnected)
        connection.sentCommands.clear()

        viewModel.invokeStreamDeckButton("btn-media")

        assertTrue(connection.sentCommands.isEmpty())
    }

    private fun connect(
        connectionId: Long = 1,
        features: Set<String> = setOf(PanelProtocol.FEATURE_STREAM_DECK),
    ) {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = connectionId,
                hostName = "PC",
                features = features,
            ),
        )
    }

    private fun defaultLayout() = layoutEvent(
        revision = "rev-1",
        pages = listOf(
            StreamDeckPageMessage(
                id = "page-a",
                name = "Principal",
                rows = 2,
                columns = 2,
                buttons = listOf(
                    StreamDeckButtonMessage(
                        id = "btn-media",
                        row = 0,
                        column = 0,
                        label = "Play",
                        iconId = ICON_ID,
                    ),
                    StreamDeckButtonMessage(
                        id = "btn-off",
                        row = 0,
                        column = 1,
                        label = "Desligado",
                        enabled = false,
                    ),
                ),
            ),
            StreamDeckPageMessage(
                id = "page-b",
                name = "Mídia",
                rows = 2,
                columns = 2,
                buttons = emptyList(),
            ),
        ),
    )

    private fun layoutEvent(revision: String, pages: List<StreamDeckPageMessage>) =
        PanelConnectionEvent.StreamDeckLayoutReceived(
            StreamDeckLayoutMessage(
                revision = revision,
                profileId = "profile-a",
                profileName = "Principal",
                pages = pages,
            ),
        )

    private fun resultEvent(
        buttonId: String,
        success: Boolean,
        errorCode: String? = null,
    ) = PanelConnectionEvent.StreamDeckInvokeResultReceived(
        StreamDeckInvokeResultMessage(
            buttonId = buttonId,
            success = success,
            errorCode = errorCode,
        ),
    )

    private companion object {
        const val ICON_ID = "icon-aaaaaaaaaa"
    }
}

private class FakeStreamDeckConnection : PanelConnection {
    private val mutableEvents = MutableSharedFlow<PanelConnectionEvent>(
        extraBufferCapacity = 32,
    )

    override val events: Flow<PanelConnectionEvent> = mutableEvents
    val sentCommands = mutableListOf<PanelCommand>()

    override suspend fun maintainConnection(): Nothing = awaitCancellation()

    override suspend fun send(command: PanelCommand, expectedConnectionId: Long) {
        sentCommands += command
    }

    override fun close() = Unit

    fun emit(event: PanelConnectionEvent) {
        check(mutableEvents.tryEmit(event))
    }
}

/** Video is irrelevant here; the session simply never starts. */
private class IdleVideoConnection : VideoConnection {
    override suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    ): Nothing = awaitCancellation()

    override fun disconnect() = Unit

    override fun close() = Unit
}

private class IdleH264DecoderController : H264DecoderController {
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

private class RecordingIconStore : StreamDeckIconStore {
    val cached = mutableSetOf<String>()
    val stored = mutableListOf<String>()
    var accept = true

    override fun cachedIconIds(): Set<String> = cached.toSet()

    override fun store(message: StreamDeckIconMessage): Boolean {
        if (!accept) return false
        stored += message.iconId
        cached += message.iconId
        return true
    }

    override fun iconFile(iconId: String?): File? = null
}
