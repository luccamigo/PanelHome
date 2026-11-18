package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class VideoClientDiagnosticsTest {
    @Test
    fun `network codec queue and render ages remain independent`() {
        var now = 1_000_000_000L
        val metrics = VideoClientSessionMetrics { now }

        metrics.packetReceived(bytes = 140)
        now += 10_000_000L
        metrics.accessUnitQueued()
        now += 5_000_000L
        val renderedAt = now
        now += 25_000_000L

        val snapshot = metrics.snapshot(
            VideoClientRuntimeState(
                renderedFrames = 1,
                lastRenderedAtNanos = renderedAt,
            ),
        )

        assertEquals(40L, snapshot.lastNetworkPacketAgeMs)
        assertEquals(30L, snapshot.lastQueuedAccessUnitAgeMs)
        assertEquals(25L, snapshot.lastRenderedFrameAgeMs)
        assertEquals(1L, snapshot.packetsReceived)
        assertEquals(1L, snapshot.accessUnitsQueued)
    }

    @Test
    fun `close is structured and emitted once with explicit reason`() {
        var now = 1_000_000_000L
        val metrics = VideoClientSessionMetrics { now }
        val logger = RecordingLogger()
        metrics.packetReceived(bytes = 512)
        now += 20_000_000L
        val runtime = VideoClientRuntimeState(
            surfaceState = "attached:7",
            decoderState = "error",
            renderedFrames = 3,
            codecErrors = 1,
            lastRenderedAtNanos = now - 5_000_000L,
        )

        metrics.logClose(
            logger,
            VideoClientCloseReason.DECODER_ERROR,
            IllegalStateException("codec stopped"),
            runtime,
        )
        metrics.logClose(logger, VideoClientCloseReason.RECONNECT, null, runtime)

        assertEquals(1, logger.transitions.size)
        val close = logger.transitions.single()
        assertTrue(close.startsWith("[VideoClientClose] reason=decoder_error"))
        assertTrue(close.contains("exception=IllegalStateException:codec stopped"))
        assertTrue(close.contains("surfaceState=attached:7"))
        assertTrue(close.contains("decoderState=error"))
        assertTrue(close.contains("receivedFrames=1"))
        assertTrue(close.contains("renderedFrames=3"))
        assertTrue(close.contains("lastFrameAgeMs=5"))
    }

    private class RecordingLogger : VideoLogger {
        val transitions = mutableListOf<String>()

        override fun transition(message: String) {
            transitions += message
        }

        override fun debug(message: String) = Unit

        override fun error(stage: String, sequence: Long?, error: Throwable) = Unit
    }
}
