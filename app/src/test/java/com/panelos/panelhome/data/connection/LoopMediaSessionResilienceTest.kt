package com.panelos.panelhome.data.connection

import com.panelos.panelhome.data.transport.LogicalChannel
import com.panelos.panelhome.data.transport.PanelByteConnection
import com.panelos.panelhome.data.transport.PanelTransport
import com.panelos.panelhome.data.transport.TransportMetrics
import java.io.PipedInputStream
import java.io.PipedOutputStream
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The production reader drops an invalid loop_media.* line and keeps the
 * session, instead of tearing it down as it does for malformed JSON.
 */
class LoopMediaSessionResilienceTest {
    @Test(timeout = 30_000)
    fun invalidLoopMediaMessagesAreDroppedAndTheSessionStaysUp() = runBlocking {
        val transport = PipeTransport()
        val connection = TransportPanelConnection(touchDebugLog = {}, transport = transport)
        val events = Channel<PanelConnectionEvent>(Channel.UNLIMITED)
        val collector = launch(Dispatchers.Default, start = CoroutineStart.UNDISPATCHED) {
            connection.events.collect { events.send(it) }
        }
        val maintainer = launch(Dispatchers.Default) { connection.maintainConnection() }
        try {
            assertInvalidLinesAreSkipped(transport, events)
        } finally {
            connection.close()
            maintainer.cancel()
            collector.cancel()
        }
    }

    private suspend fun assertInvalidLinesAreSkipped(
        transport: PipeTransport,
        events: Channel<PanelConnectionEvent>,
    ) {
        val validId = "a".repeat(64)
        transport.hostWrites(
            """{"type":"hello_ack","protocolVersion":1,"hostName":"PC","features":["loop_media"]}""",
            """{"type":"loop_media.state","available":true,"mediaId":"../x","kind":"gif","mime":"image/gif","sizeBytes":1}""",
            """{"type":"loop_media.state","available":true,"mediaId":"${"a".repeat(63)}","kind":"gif","mime":"image/gif","sizeBytes":1}""",
            """{"type":"loop_media.state","available":true,"mediaId":"$validId","kind":"gif","mime":"video/mp4","sizeBytes":1}""",
            """{"type":"loop_media.state","available":true,"mediaId":"$validId","kind":"gif","mime":"image/gif","sizeBytes":0}""",
            """{"type":"loop_media.state","available":true,"mediaId":"$validId","kind":"gif","mime":"image/gif","sizeBytes":67108865}""",
            """{"type":"loop_media.state","available":true,"mediaId":"$validId","kind":"gif","mime":"image/gif","sizeBytes":10}""",
        )

        val received = withTimeout(10_000) {
            buildList {
                while (none { it is PanelConnectionEvent.LoopMediaStateReceived }) add(events.receive())
            }
        }

        assertTrue(received.first() is PanelConnectionEvent.Connecting)
        assertTrue(received.any { it is PanelConnectionEvent.Connected })
        assertTrue(received.none { it is PanelConnectionEvent.Disconnected })
        val state = (received.last() as PanelConnectionEvent.LoopMediaStateReceived).state
        assertEquals(validId, state.mediaId)
        assertEquals(10, state.sizeBytes)
    }

    private class PipeTransport : PanelTransport {
        override val name = "pipe"
        override val metrics = TransportMetrics()
        private val hostToPanel = PipedOutputStream()
        private val panelInput = PipedInputStream(hostToPanel, 64 * 1024)

        fun hostWrites(vararg lines: String) {
            lines.forEach { hostToPanel.write("$it\n".toByteArray()) }
            hostToPanel.flush()
        }

        override suspend fun connect(channel: LogicalChannel): PanelByteConnection =
            object : PanelByteConnection {
                override val channel = channel
                override val input = panelInput
                override val output = java.io.ByteArrayOutputStream()
                override var isClosed = false
                override fun setReadTimeout(timeoutMillis: Int) = Unit
                override fun close() {
                    isClosed = true
                    // Closing the write side is what wakes a blocked PipedInputStream.read.
                    runCatching { hostToPanel.close() }
                }
            }

        override fun close() = Unit
    }
}
