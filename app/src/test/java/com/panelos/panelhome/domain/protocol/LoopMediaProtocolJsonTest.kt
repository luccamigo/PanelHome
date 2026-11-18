package com.panelos.panelhome.domain.protocol

import kotlin.io.encoding.Base64
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class LoopMediaProtocolJsonTest {
    @Test
    fun theClientAnnouncesLoopMediaInItsHello() {
        val hello = JSONObject(PanelProtocolJson.encode(HelloMessage()))
        val features = hello.getJSONArray("features").toList()
        assertTrue("loop_media" in features)
    }

    @Test
    fun anAvailableStateIsDecoded() {
        val state = PanelProtocolJson.decode(state()) as LoopMediaStateMessage

        assertEquals(LoopMediaStateMessage(true, MEDIA_ID, LoopMediaKind.GIF, 1024), state)
    }

    @Test
    fun anUnavailableStateCarriesNothingElse() {
        val state = PanelProtocolJson.decode(
            """{"type":"loop_media.state","available":false}""",
        ) as LoopMediaStateMessage

        assertFalse(state.available)
        assertNull(state.mediaId)
        assertEquals(setOf("type", "available"), JSONObject(PanelProtocolJson.encode(state)).keys().asSequence().toSet())
    }

    @Test
    fun aVideoStateIsDecoded() {
        val state = PanelProtocolJson.decode(
            state(kind = "video", mime = "video/mp4", sizeBytes = 67_108_864),
        ) as LoopMediaStateMessage

        assertEquals(LoopMediaKind.VIDEO, state.kind)
        assertEquals(67_108_864, state.sizeBytes)
    }

    @Test
    fun invalidStatesAreRejectedWithoutBeingProtocolFailures() {
        listOf(
            state(mediaId = "../x"),
            state(mediaId = MEDIA_ID.drop(1)),
            state(mediaId = MEDIA_ID.uppercase()),
            state(kind = "gif", mime = "video/mp4"),
            state(kind = "video", mime = "image/gif"),
            state(kind = "png", mime = "image/png"),
            state(sizeBytes = 0),
            state(sizeBytes = 67_108_865),
            """{"type":"loop_media.state","available":true}""",
        ).forEach { line ->
            assertThrows(line, PanelMessageRejectedException::class.java) {
                PanelProtocolJson.decode(line)
            }
        }
    }

    @Test
    fun aDataChunkIsDecodedAndRoundTrips() {
        val data = Base64.Default.encode(ByteArray(10) { it.toByte() })
        val line = """{"type":"loop_media.chunk","mediaId":"$MEDIA_ID","offset":0,""" +
            """"totalBytes":10,"data":"$data","last":true}"""

        val chunk = PanelProtocolJson.decode(line) as LoopMediaChunkMessage

        assertEquals(LoopMediaChunkMessage(MEDIA_ID, 0, 10, data, last = true), chunk)
        assertEquals(JSONObject(line).toString(), JSONObject(PanelProtocolJson.encode(chunk)).toString())
    }

    @Test
    fun anErrorChunkCarriesNoData() {
        val line = """{"type":"loop_media.chunk","mediaId":"$MEDIA_ID","offset":5,"error":"stale_media"}"""

        val chunk = PanelProtocolJson.decode(line) as LoopMediaChunkMessage

        assertEquals(LoopMediaErrorCode.STALE_MEDIA, chunk.error)
        assertNull(chunk.dataBase64)
        assertEquals(JSONObject(line).toString(), JSONObject(PanelProtocolJson.encode(chunk)).toString())
    }

    @Test
    fun invalidChunksAreRejected() {
        val tooLarge = Base64.Default.encode(ByteArray(LoopMediaLimits.MAX_CHUNK_BYTES + 1))
        val ok = Base64.Default.encode(ByteArray(4))
        listOf(
            chunk(data = tooLarge, totalBytes = LoopMediaLimits.MAX_CHUNK_BYTES + 1, last = true),
            chunk(mediaId = "../x"),
            chunk(data = "!!not-base64!!"),
            chunk(data = ""),
            chunk(data = ok, totalBytes = 4, last = false),
            chunk(data = ok, totalBytes = 10, last = true),
            chunk(data = ok, totalBytes = 2, last = true),
            """{"type":"loop_media.chunk","mediaId":"$MEDIA_ID","offset":-1,"error":"x"}""",
        ).forEach { line ->
            assertThrows(line, PanelMessageRejectedException::class.java) {
                PanelProtocolJson.decode(line)
            }
        }
    }

    @Test
    fun aChunkOfExactlyTheLimitIsAccepted() {
        val data = Base64.Default.encode(ByteArray(LoopMediaLimits.MAX_CHUNK_BYTES))
        val chunk = PanelProtocolJson.decode(
            chunk(data = data, totalBytes = LoopMediaLimits.MAX_CHUNK_BYTES * 2, last = false),
        ) as LoopMediaChunkMessage

        assertFalse(chunk.last)
    }

    @Test
    fun aRequestCarriesOnlyTheMediaIdAndTheOffset() {
        val json = JSONObject(
            PanelProtocolJson.encode(CommandMessage(LoopMediaRequestCommand(MEDIA_ID, 262_144))),
        )

        assertEquals("loop_media.request", json.getString("command"))
        assertEquals(MEDIA_ID, json.getString("mediaId"))
        assertEquals(262_144, json.getInt("offset"))
        assertEquals(setOf("type", "command", "mediaId", "offset"), json.keys().asSequence().toSet())
        assertEquals(
            CommandMessage(LoopMediaRequestCommand(MEDIA_ID, 262_144)),
            PanelProtocolJson.decode(json.toString()),
        )
    }

    @Test
    fun nonIntegerNumbersAreRejectedInsteadOfTruncated() {
        val data = Base64.Default.encode(ByteArray(4))
        listOf(
            state().replace("\"sizeBytes\":1024", "\"sizeBytes\":1.5"),
            state().replace("\"sizeBytes\":1024", "\"sizeBytes\":\"1024\""),
            state().replace("\"sizeBytes\":1024", "\"sizeBytes\":1e3"),
            chunk().replace("\"offset\":0", "\"offset\":0.5"),
            chunk().replace("\"totalBytes\":4", "\"totalBytes\":4.0"),
            """{"type":"loop_media.chunk","mediaId":"$MEDIA_ID","offset":"0","totalBytes":4,"data":"$data","last":true}""",
            """{"type":"loop_media.chunk","mediaId":"$MEDIA_ID","offset":1.5,"error":"stale_media"}""",
        ).forEach { line ->
            assertThrows(line, PanelMessageRejectedException::class.java) {
                PanelProtocolJson.decode(line)
            }
        }
        assertThrows(PanelProtocolException::class.java) {
            PanelProtocolJson.decode(
                """{"type":"command","command":"loop_media.request","mediaId":"$MEDIA_ID","offset":2.5}""",
            )
        }
    }

    @Test
    fun malformedJsonIsStillAProtocolFailure() {
        val error = assertThrows(PanelProtocolException::class.java) {
            PanelProtocolJson.decode("""{"type":"loop_media.state",""")
        }
        assertFalse(error is PanelMessageRejectedException)
    }

    private fun state(
        mediaId: String = MEDIA_ID,
        kind: String = "gif",
        mime: String = "image/gif",
        sizeBytes: Long = 1024,
    ) = """{"type":"loop_media.state","available":true,"mediaId":"$mediaId",""" +
        """"kind":"$kind","mime":"$mime","sizeBytes":$sizeBytes}"""

    private fun chunk(
        mediaId: String = MEDIA_ID,
        data: String = Base64.Default.encode(ByteArray(4)),
        totalBytes: Int = 4,
        last: Boolean = true,
    ) = """{"type":"loop_media.chunk","mediaId":"$mediaId","offset":0,""" +
        """"totalBytes":$totalBytes,"data":"$data","last":$last}"""

    private fun org.json.JSONArray.toList() = (0 until length()).map(::getString)

    private companion object {
        val MEDIA_ID = "a".repeat(64)
    }
}
