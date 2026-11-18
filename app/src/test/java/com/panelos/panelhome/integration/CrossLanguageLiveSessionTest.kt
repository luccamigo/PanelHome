package com.panelos.panelhome.integration

import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.connection.TransportPanelConnection
import com.panelos.panelhome.data.transport.LogicalChannel
import com.panelos.panelhome.data.transport.PanelByteConnection
import com.panelos.panelhome.data.transport.PanelTransport
import com.panelos.panelhome.data.transport.TransportMetrics
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.domain.protocol.HelloMessage
import com.panelos.panelhome.domain.protocol.LoopMediaRequestCommand
import com.panelos.panelhome.domain.protocol.PanelBuildIdentity
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.StreamDeckErrorCode
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeCommand
import com.panelos.panelhome.presentation.LoopMediaController
import com.panelos.panelhome.presentation.LoopMediaPhase
import java.io.File
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.file.Files
import java.security.MessageDigest
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/**
 * Kotlin half of the live C#/Kotlin harness. TransportPanelConnection and all
 * PanelProtocolJson parsing/writing are production code; only the carrier is a
 * raw TCP loopback socket so the test has no ADB authentication or USB needs.
 */
class CrossLanguageLiveSessionTest {
    private lateinit var manifest: LiveManifest
    private val activeSessions = mutableListOf<ClientSession>()

    @Before
    fun loadHostManifest() {
        val path = System.getenv(MANIFEST_ENVIRONMENT)
        assumeTrue(
            "Run scripts/run-cross-language-live-session.ps1 to start the C# host",
            !path.isNullOrBlank(),
        )
        manifest = LiveManifest(JSONObject(java.io.File(requireNotNull(path)).readText()))
    }

    @After
    fun closeSessions() = runBlocking {
        activeSessions.toList().forEach { it.close() }
        activeSessions.clear()
    }

    @Test
    fun productionSessionExchangesAllStateAndRejectsStaleStreamDeckRevision() = runBlocking {
        val session = start(manifest.positivePort)
        val connected = session.await<PanelConnectionEvent.Connected>()
        assertEquals(manifest.hostBuildIdentity, connected.hostBuildIdentity)
        assertEquals(manifest.hostBuildNumber, connected.hostBuildNumber)
        assertTrue(PanelProtocol.FEATURE_DESKTOP in connected.features)
        assertTrue(PanelProtocol.FEATURE_VOLUME in connected.features)
        assertTrue(PanelProtocol.FEATURE_STREAM_DECK in connected.features)
        assertTrue(PanelProtocol.FEATURE_LOOP_MEDIA in connected.features)

        val directory = Files.createTempDirectory("loop-media-live").toFile()
        val requests = mutableListOf<LoopMediaRequestCommand>()
        val controller = LoopMediaController(directory, SilentVideoLogger, { command ->
            requests += command as LoopMediaRequestCommand
            true
        })
        controller.onConnected(connected.features)
        var telemetry = false
        var media = false
        var volume = false
        var desktop = false
        var layoutRevision: String? = null
        var requestsSent = 0
        try {
            withTimeout(EVENT_TIMEOUT_MILLIS) {
                while (
                    !telemetry || !media || !volume || !desktop || layoutRevision == null ||
                    controller.state.value.phase != LoopMediaPhase.READY
                ) {
                    when (val event = session.events.receive()) {
                        is PanelConnectionEvent.TelemetryReceived -> {
                            telemetry = event.telemetry.cpu.name == "Live CPU"
                        }
                        is PanelConnectionEvent.MediaStateReceived -> {
                            media = event.mediaState.title == "Live Session"
                        }
                        is PanelConnectionEvent.VolumeStateReceived -> {
                            volume = event.volumeState.volumePercent == 73
                        }
                        is PanelConnectionEvent.DesktopStateReceived -> desktop = true
                        is PanelConnectionEvent.StreamDeckLayoutReceived -> {
                            layoutRevision = event.layout.revision
                        }
                        is PanelConnectionEvent.LoopMediaStateReceived -> controller.applyState(event.state)
                        is PanelConnectionEvent.LoopMediaChunkReceived -> controller.applyChunk(event.chunk)
                        else -> Unit
                    }
                    if (requestsSent < requests.size) {
                        session.connection.send(requests[requestsSent++], connected.connectionId)
                    }
                }
            }
            assertEquals(listOf(0, 262144, 524288), requests.map { it.offset })
            val file = requireNotNull(controller.state.value.file)
            assertArrayEquals(File(manifest.loopMediaPath).readBytes(), file.readBytes())
            assertEquals(
                controller.state.value.mediaId,
                MessageDigest.getInstance("SHA-256").digest(file.readBytes())
                    .joinToString("") { "%02x".format(it) },
            )
            assertEquals(listOf(file.name), directory.listFiles().orEmpty().map { it.name })
        } finally {
            controller.onDisconnected()
            directory.deleteRecursively()
        }

        session.connection.send(
            StreamDeckInvokeCommand("btn-media", "stale-cross-language-revision"),
            connected.connectionId,
        )
        val stale = session.await<PanelConnectionEvent.StreamDeckInvokeResultReceived>()
        assertFalse(stale.result.success)
        assertEquals(StreamDeckErrorCode.STALE_LAYOUT, stale.result.errorCode)
        val refreshed = session.await<PanelConnectionEvent.StreamDeckLayoutReceived>()
        assertEquals(layoutRevision, refreshed.layout.revision)

        session.connection.close()
        session.await<PanelConnectionEvent.Disconnected>()
        Unit
    }

    @Test
    fun missingFeatureIsNegotiatedWithoutRejectingTheProtocol() = runBlocking {
        val session = start(manifest.missingFeaturePort)
        val connected = session.await<PanelConnectionEvent.Connected>()

        assertFalse(PanelProtocol.FEATURE_STREAM_DECK in connected.features)
        assertFalse(PanelProtocol.FEATURE_LOOP_MEDIA in connected.features)
        assertTrue(PanelProtocol.FEATURE_DESKTOP in connected.features)
        session.connection.close()
    }

    @Test
    fun wrongProtocolVersionIsRejectedByTheProductionHost() = runBlocking {
        val session = start(
            manifest.wrongVersionPort,
            HelloMessage(
                protocolVersion = PanelProtocol.PROTOCOL_VERSION + 1,
                buildIdentity = PanelBuildIdentity.current.identity,
                buildNumber = PanelBuildIdentity.current.number,
            ),
        )

        session.await<PanelConnectionEvent.Disconnected>()
        assertFalse(session.seen.any { it is PanelConnectionEvent.Connected })
        session.connection.close()
    }

    @Test
    fun invalidJsonlDisconnectsTheProductionClientParser() = runBlocking {
        val session = start(manifest.invalidJsonPort)
        session.await<PanelConnectionEvent.Connected>()
        session.await<PanelConnectionEvent.Disconnected>()
        session.connection.close()
    }

    @Test
    fun connectionDroppedDuringHandshakeNeverBecomesConnected() = runBlocking {
        val session = start(manifest.droppedHandshakePort)
        session.await<PanelConnectionEvent.Disconnected>()
        assertFalse(session.seen.any { it is PanelConnectionEvent.Connected })
        session.connection.close()
    }

    @Test
    fun productionClientReconnectsAndGetsANewSessionIdentity() = runBlocking {
        val session = start(manifest.reconnectPort)
        val first = session.await<PanelConnectionEvent.Connected>()
        session.await<PanelConnectionEvent.Disconnected>()
        val second = session.await<PanelConnectionEvent.Connected>()

        assertNotEquals(first.connectionId, second.connectionId)
        assertTrue(second.connectionId > first.connectionId)
        session.connection.close()
    }

    private fun start(port: Int, hello: HelloMessage? = null): ClientSession {
        val transport = LoopbackTransport(port)
        val diagnostics = java.util.Collections.synchronizedList(mutableListOf<String>())
        val connection = if (hello == null) {
            TransportPanelConnection(transport = transport, touchDebugLog = diagnostics::add)
        } else {
            TransportPanelConnection(
                transport = transport,
                hello = hello,
                touchDebugLog = diagnostics::add,
            )
        }
        return ClientSession(connection, transport, diagnostics).also {
            activeSessions += it
            it.start()
        }
    }

    private class ClientSession(
        val connection: TransportPanelConnection,
        private val transport: LoopbackTransport,
        private val diagnostics: List<String>,
    ) {
        val events = Channel<PanelConnectionEvent>(Channel.UNLIMITED)
        val seen = java.util.Collections.synchronizedList(mutableListOf<PanelConnectionEvent>())
        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        private lateinit var collector: Job
        private lateinit var maintainer: Job

        fun start() {
            collector = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                connection.events.collect { event ->
                    seen += event
                    events.send(event)
                }
            }
            maintainer = scope.launch { connection.maintainConnection() }
        }

        suspend inline fun <reified T : PanelConnectionEvent> await(): T {
            val matched = withTimeoutOrNull(EVENT_TIMEOUT_MILLIS) {
                while (true) {
                    val event = events.receive()
                    if (event is T) return@withTimeoutOrNull event
                }
                error("unreachable")
            }
            return matched ?: error(
                "Timed out waiting for ${T::class.java.simpleName}; seen=${seen.toList()}; " +
                    "diagnostics=${diagnostics.toList()}",
            )
        }

        suspend fun close() {
            connection.close()
            transport.close()
            if (::maintainer.isInitialized) maintainer.cancelAndJoin()
            if (::collector.isInitialized) collector.cancelAndJoin()
            events.close()
            scope.cancel()
        }
    }

    private class LoopbackTransport(private val port: Int) : PanelTransport {
        override val name = "cross-language-loopback"
        override val metrics = TransportMetrics()
        private val sockets = java.util.Collections.synchronizedList(mutableListOf<Socket>())

        override suspend fun connect(channel: LogicalChannel): PanelByteConnection {
            require(channel == LogicalChannel.CONTROL)
            val socket = Socket()
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress("127.0.0.1", port), 3_000)
            sockets += socket
            return object : PanelByteConnection {
                override val channel = LogicalChannel.CONTROL
                override val input = socket.getInputStream()
                override val output = socket.getOutputStream()
                override val isClosed: Boolean get() = socket.isClosed
                override fun setReadTimeout(timeoutMillis: Int) {
                    socket.soTimeout = timeoutMillis
                }
                override fun close() {
                    socket.close()
                    sockets.remove(socket)
                }
            }
        }

        override fun close() {
            sockets.toList().forEach { runCatching { it.close() } }
            sockets.clear()
        }
    }

    private class LiveManifest(json: JSONObject) {
        val positivePort = json.getInt("PositivePort")
        val missingFeaturePort = json.getInt("MissingFeaturePort")
        val wrongVersionPort = json.getInt("WrongVersionPort")
        val invalidJsonPort = json.getInt("InvalidJsonPort")
        val droppedHandshakePort = json.getInt("DroppedHandshakePort")
        val reconnectPort = json.getInt("ReconnectPort")
        val hostBuildIdentity = json.getString("HostBuildIdentity")
        val hostBuildNumber = json.getLong("HostBuildNumber")
        val loopMediaPath = json.getString("LoopMediaPath")
    }

    private companion object {
        const val MANIFEST_ENVIRONMENT = "PANELOS_LIVE_SESSION_MANIFEST"
        const val EVENT_TIMEOUT_MILLIS = 15_000L
    }
}
