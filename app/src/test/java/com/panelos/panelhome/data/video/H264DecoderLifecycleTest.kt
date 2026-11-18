package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import org.junit.Assert.assertEquals
import org.junit.Test

class H264DecoderLifecycleTest {
    @Test
    fun repeatedStopProducesOneCleanupTransition() {
        val logger = RecordingLogger()
        val controller = MediaCodecH264DecoderController(
            capabilities = H264DecoderCapabilityProvider { null },
            adapterFactory = H264CodecAdapterFactory { _, _, _, _, _ ->
                error("No Surface is attached in this test")
            },
            logger = logger,
        )
        controller.beginSession(
            generation = 1,
            session = VideoSessionInfo(VideoProtocol.CODEC_H264, 1280, 800, 30),
            onFrameRendered = { _, _, _ -> },
            onError = { _, _, _, _ -> },
        )

        controller.stopSession()
        controller.stopSession()
        controller.close()

        assertEquals(
            1,
            logger.transitions.count { it == "H.264 decoder session stopped" },
        )
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
