package com.panelos.panelhome.domain.protocol

import com.panelos.panelhome.data.streamdeck.StreamDeckLayoutMapper
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.data.video.VideoHandshakeResponse
import com.panelos.panelhome.data.video.VideoHello
import java.io.File
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class ProtocolGoldenFixtureTest {
    @Test
    fun everySharedControlFixtureRoundTripsCanonically() {
        validControlFixtures().forEach { file ->
            val source = file.readText()
            val encoded = PanelProtocolJson.encode(PanelProtocolJson.decode(source))
            assertEquals(file.name, JSONObject(source).toString(), JSONObject(encoded).toString())
        }
    }

    @Test
    fun allFiveLoopMediaFixturesArePresentAndInvalidMediaIdsAreRejected() {
        val files = File(fixtureRoot(), "loop_media").listFiles().orEmpty()
        assertEquals(
            setOf("state.json", "state_none.json", "request.json", "chunk.json", "chunk_error.json"),
            files.map { it.name }.toSet(),
        )
        files.forEach { file ->
            val json = JSONObject(file.readText())
            if (json.has("mediaId")) {
                json.put("mediaId", "../x")
                assertThrows(file.name, PanelProtocolException::class.java) {
                    PanelProtocolJson.decode(json.toString())
                }
            }
        }
    }

    @Test
    fun sharedVideoVectorsMatchTheKotlinWireCodec() {
        assertVector(
            "hello.json",
            VideoProtocol.encodeHello(
                VideoHello(
                    maxWidth = 1280,
                    maxHeight = 800,
                    maxFps = 60,
                    supportedCodecMask = 3,
                ),
            ),
        )
        val ack = fixtureBytes("ack.json")
        val parsedAck = VideoProtocol.parseHandshake(ack) as VideoHandshakeResponse.Accepted
        assertEquals(VideoProtocol.CODEC_H264, parsedAck.codec)
        assertEquals(1280, parsedAck.maxWidth)
        assertEquals(800, parsedAck.maxHeight)
        assertEquals(60, parsedAck.maxFps)

        val reject = VideoProtocol.parseHandshake(fixtureBytes("reject.json"))
            as VideoHandshakeResponse.Rejected
        assertEquals(4, reject.errorCode)

        val header = VideoProtocol.parseH264PacketHeader(fixtureBytes("h264_packet_header.json"))
        assertEquals(1, header.sequence)
        assertEquals(5, header.payloadBytes)
        assertEquals(2500, header.encodeDurationMicros)
        assertEquals(true, header.isKeyFrame)
    }

    @Test
    fun everySharedInvalidFixtureIsRejected() {
        File(fixtureRoot(), "errors/invalid").listFiles().orEmpty().forEach { file ->
            val rejected = runCatching {
                val message = PanelProtocolJson.decode(file.readText())
                if (message is StreamDeckLayoutMessage) {
                    val layout = StreamDeckLayoutMapper.toLayout(message)
                    require(layout.pages.size == message.pages.size)
                }
            }.isFailure
            assertEquals(file.name, true, rejected)
        }
    }

    private fun validControlFixtures(): List<File> =
        listOf("control", "telemetry", "streamdeck", "loop_media", "errors").flatMap { directory ->
            File(fixtureRoot(), directory).listFiles()
                .orEmpty()
                .filter(File::isFile)
        }

    private fun assertVector(fileName: String, actual: ByteArray) {
        assertEquals(fileName, fixtureBytes(fileName).toList(), actual.toList())
    }

    private fun fixtureBytes(fileName: String): ByteArray {
        val hex = JSONObject(File(fixtureRoot(), "video/$fileName").readText()).getString("hex")
        return hex.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }

    private fun fixtureRoot(): File {
        var directory = File(System.getProperty("user.dir")).absoluteFile
        repeat(8) {
            File(directory, "protocol-fixtures").takeIf(File::isDirectory)?.let { return it }
            directory = directory.parentFile ?: return@repeat
        }
        error("protocol-fixtures was not found")
    }
}
