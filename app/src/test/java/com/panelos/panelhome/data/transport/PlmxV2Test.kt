package com.panelos.panelhome.data.transport

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.InputStream
import java.io.SequenceInputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Collections
import kotlin.random.Random
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * PLMX v2 exists because v1 could not tell its own bytes from its predecessor's.
 * These tests pin that: a reader handed a stream that still holds a previous
 * session's frames must resynchronize onto a genuine handshake, ignore
 * everything before it, and never mistake a magic inside a payload for a frame.
 */
class PlmxV2Test {
    private val sessionA = PlmxSessionId(ByteArray(16) { (it + 1).toByte() })
    private val sessionB = PlmxSessionId(ByteArray(16) { (0x40 + it).toByte() })

    @Test
    fun aSessionIdIsRandom128BitsAndNeverRepeats() {
        val seen = HashSet<String>()
        repeat(512) { assertTrue(seen.add(PlmxSessionId.create().bytes.toHex())) }
        assertTrue(!PlmxSessionId.create().isEmpty)
        assertTrue(PlmxSessionId.EMPTY.isEmpty)
    }

    @Test
    fun theTagIsTheFirstFourBytesOfTheNonce() {
        val expected = ByteBuffer.wrap(sessionA.bytes, 0, 4).order(ByteOrder.LITTLE_ENDIAN).int
        assertEquals(expected, sessionA.tag)
    }

    @Test
    fun theLoggedPrefixNeverExposesTheWholeNonce() {
        assertEquals(9, sessionA.prefix.length)
        assertTrue(sessionA.prefix.endsWith("..."))
        assertEquals("<none>", PlmxSessionId.EMPTY.prefix)
    }

    @Test
    fun aHeaderRoundTripsAndRejectsV1ReservedAndOversize() {
        val header = PlmxFrame.encodeHeader(
            LogicalChannel.VIDEO,
            PlmxFrameType.DATA,
            payloadLength = 4096,
            sequence = 7,
            sessionTag = sessionA.tag,
        )
        assertEquals(PlmxFrame.HEADER_SIZE, header.size)
        val decoded = PlmxFrame.decodeHeader(header)
        assertEquals(LogicalChannel.VIDEO, decoded.channel)
        assertEquals(PlmxFrameType.DATA, decoded.type)
        assertEquals(4096, decoded.payloadLength)
        assertEquals(7, decoded.sequence)
        assertEquals(sessionA.tag, decoded.sessionTag)

        // A v1 header must not be accepted on a v2 carrier: silently downgrading
        // would reintroduce identity-free framing.
        val downgraded = header.copyOf().also { it[4] = 1 }
        assertNull(PlmxFrame.tryDecodeHeader(downgraded, 0))

        val reserved = header.copyOf()
        ByteBuffer.wrap(reserved, 20, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(1)
        assertNull(PlmxFrame.tryDecodeHeader(reserved, 0))

        val oversize = header.copyOf()
        ByteBuffer.wrap(oversize, 8, 4).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(MultiplexFrame.MAX_VIDEO_PAYLOAD + 1)
        assertNull("Hardening Wave 1 ceilings still apply", PlmxFrame.tryDecodeHeader(oversize, 0))
    }

    @Test
    fun aHelloOnlyValidatesWhenItsHeaderAndPayloadAgree() {
        val payload = PlmxFrame.encodeHello(CAPABILITIES, sessionA)
        assertEquals(PlmxFrame.HELLO_PAYLOAD_SIZE, payload.size)
        val hello = PlmxFrame.tryDecodeHello(payload, 0, payload.size)
        assertEquals(sessionA.bytes.toHex(), hello!!.sessionId.bytes.toHex())

        val header = PlmxFrame.decodeHeader(
            PlmxFrame.encodeHeader(
                LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, payload.size, 1, sessionA.tag,
            ),
        )
        assertTrue(PlmxFrame.readHandshake(header, payload, 0, PlmxFrameType.HELLO) != null)

        // A header tagged for one session carrying another session's nonce is
        // not a handshake, it is corruption or a splice.
        val mismatched = PlmxFrame.decodeHeader(
            PlmxFrame.encodeHeader(
                LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, payload.size, 1, sessionB.tag,
            ),
        )
        assertNull(PlmxFrame.readHandshake(mismatched, payload, 0, PlmxFrameType.HELLO))

        val zeroed = PlmxFrame.encodeHello(CAPABILITIES, PlmxSessionId.EMPTY)
        assertNull(
            "An all-zero nonce defeats session identity",
            PlmxFrame.tryDecodeHello(zeroed, 0, zeroed.size),
        )
    }

    @Test
    fun staleSessionBytesAreSkippedAndTheHandshakeIsFound() {
        val stale = ByteArrayOutputStream().apply {
            writeFrame(LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, helloPayload(sessionA), 1, sessionA.tag)
            writeFrame(LogicalChannel.CONTROL, PlmxFrameType.OPEN, ByteArray(0), 2, sessionA.tag)
            writeFrame(LogicalChannel.CONTROL, PlmxFrameType.DATA, "old-telemetry".toByteArray(), 3, sessionA.tag)
            // A frame that was cut off mid-header when the previous host died.
            write(
                PlmxFrame.encodeHeader(LogicalChannel.VIDEO, PlmxFrameType.DATA, 4096, 4, sessionA.tag)
                    .copyOf(11),
            )
        }.toByteArray()

        val reader = readerOver(stale + helloFrame(sessionB))
        // The first match is session A's own stale greeting; the scanner is
        // asked to skip it, which is what the accessory does when the host
        // rejects the resulting acknowledgement.
        val first = reader.searchHandshake(PlmxFrameType.HELLO) { true }
        assertEquals(sessionA.bytes.toHex(), first.sessionId.bytes.toHex())
        val second = reader.searchHandshake(PlmxFrameType.HELLO) { it.sessionId.tag == sessionB.tag }
        assertEquals(sessionB.bytes.toHex(), second.sessionId.bytes.toHex())
        assertTrue("The stale prefix must be reported as discarded", reader.discardedBytes > 0)
    }

    @Test
    fun aMagicHiddenInsideAStalePayloadDoesNotFalselySynchronize() {
        val decoy = ByteArray(64)
        ByteBuffer.wrap(decoy, 8, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(PlmxFrame.MAGIC)
        ByteBuffer.wrap(decoy, 40, 4).order(ByteOrder.LITTLE_ENDIAN).putInt(PlmxFrame.MAGIC)
        val stale = ByteArrayOutputStream().apply {
            writeFrame(LogicalChannel.CONTROL, PlmxFrameType.DATA, decoy, 5, sessionA.tag)
        }.toByteArray()

        val hello = readerOver(stale + helloFrame(sessionB)).searchHandshake(PlmxFrameType.HELLO) { true }

        assertEquals(sessionB.bytes.toHex(), hello.sessionId.bytes.toHex())
    }

    @Test
    fun randomGarbageBeforeTheHandshakeIsAbsorbedWithoutCrashing() {
        val random = Random(20260812)
        for (length in intArrayOf(0, 1, 3, 17, 512, 4096, 64 * 1024)) {
            val garbage = random.nextBytes(length)
            val hello = readerOver(garbage + helloFrame(sessionB)).searchHandshake(PlmxFrameType.HELLO) { true }
            assertEquals("garbage length $length", sessionB.bytes.toHex(), hello.sessionId.bytes.toHex())
        }
    }

    @Test
    fun aTruncatedHeaderOrPayloadIsSurvivable() {
        val truncatedHeader = PlmxFrame
            .encodeHeader(LogicalChannel.CONTROL, PlmxFrameType.DATA, 128, 9, sessionA.tag)
            .copyOf(5)
        assertEquals(
            sessionB.bytes.toHex(),
            readerOver(truncatedHeader + helloFrame(sessionB))
                .searchHandshake(PlmxFrameType.HELLO) { true }.sessionId.bytes.toHex(),
        )

        val partialPayload = PlmxFrame
            .encodeHeader(LogicalChannel.CONTROL, PlmxFrameType.DATA, 4096, 9, sessionA.tag) + ByteArray(1000)
        assertEquals(
            sessionB.bytes.toHex(),
            readerOver(partialPayload + helloFrame(sessionB))
                .searchHandshake(PlmxFrameType.HELLO) { true }.sessionId.bytes.toHex(),
        )
    }

    @Test
    fun resyncGivesUpAfterItsBoundedBudgetInsteadOfSpinning() {
        val garbage = Random(7).nextBytes(64 * 1024)
        val reader = PlmxStreamReader(
            EndlessInputStream(garbage),
            TransportIoProfile(),
            limits = PlmxResyncLimits(maximumBytes = 8 * 1024, maximumDurationMillis = 2_000),
        )

        val error = assertThrows(PlmxResyncException::class.java) {
            reader.searchHandshake(PlmxFrameType.HELLO) { true }
        }

        assertTrue(error.discardedBytes >= 8 * 1024)
    }

    @Test
    fun aFrameFromAnotherSessionIsRecognisableAsStale() {
        val header = PlmxFrame.decodeHeader(
            PlmxFrame.encodeHeader(LogicalChannel.CONTROL, PlmxFrameType.DATA, 5, 9, sessionA.tag),
        )
        assertNotEquals(sessionB.tag, header.sessionTag)
        assertEquals(sessionA.tag, header.sessionTag)
    }

    @Test
    fun theSharedFixturesDecodeExactlyAsTheCSharpSideExpects() {
        val session = JSONObject(fixtureFile("session.json").readText())
        val sessionTag = session.getLong("sessionTag").toInt()
        assertEquals(PlmxFrame.HEADER_SIZE, session.getInt("headerSize"))
        assertEquals(PlmxFrame.VERSION, session.getInt("version"))
        assertEquals(PlmxFrame.HELLO_PAYLOAD_SIZE, session.getInt("helloPayloadSize"))

        val hello = fixtureBytes("hello-v2.json")
        val helloHeader = PlmxFrame.tryDecodeHeader(hello, 0)!!
        assertEquals(PlmxFrameType.HELLO, helloHeader.type)
        assertEquals(sessionTag, helloHeader.sessionTag)
        val parsed = PlmxFrame.readHandshake(helloHeader, hello, PlmxFrame.HEADER_SIZE, PlmxFrameType.HELLO)!!
        assertEquals(session.getString("sessionId"), parsed.sessionId.bytes.toHex())
        assertEquals(session.getInt("capabilities"), parsed.capabilities)

        val ack = fixtureBytes("hello-ack-v2.json")
        assertEquals(PlmxFrameType.HELLO_ACK, PlmxFrame.tryDecodeHeader(ack, 0)!!.type)

        assertEquals(PlmxFrameType.OPEN, PlmxFrame.decodeHeader(fixtureBytes("open-v2.json")).type)
        assertEquals(PlmxFrameType.FIN, PlmxFrame.decodeHeader(fixtureBytes("fin-v2.json")).type)
        val data = PlmxFrame.decodeHeader(fixtureBytes("data-v2.json"))
        assertEquals(LogicalChannel.CONTROL, data.channel)
        assertEquals(11, data.payloadLength)
        assertEquals(LogicalChannel.VIDEO, PlmxFrame.decodeHeader(fixtureBytes("video-data-v2.json")).channel)

        // A frame for another session decodes but must not match this one.
        assertNotEquals(sessionTag, PlmxFrame.decodeHeader(fixtureBytes("stale-session.json")).sessionTag)

        for (invalid in listOf(
            "invalid-session-zero.json",
            "invalid-version.json",
            "invalid-reserved.json",
            "invalid-open-payload.json",
            "invalid-hello-length.json",
            "invalid-hello-reserved.json",
        )) {
            val bytes = fixtureBytes(invalid)
            val header = PlmxFrame.tryDecodeHeader(bytes, 0)
            val accepted = header != null &&
                (header.type !in setOf(PlmxFrameType.HELLO, PlmxFrameType.HELLO_ACK) ||
                    PlmxFrame.readHandshake(header, bytes, PlmxFrame.HEADER_SIZE, header.type) != null)
            assertTrue(invalid, !accepted)
        }

        val mismatched = fixtureBytes("invalid-hello-tag.json")
        val mismatchedHeader = PlmxFrame.tryDecodeHeader(mismatched, 0)!!
        assertNull(
            "invalid-hello-tag.json",
            PlmxFrame.readHandshake(mismatchedHeader, mismatched, PlmxFrame.HEADER_SIZE, PlmxFrameType.HELLO),
        )
    }

    @Test
    fun theFixtureHelloMatchesWhatThisSideEncodes() {
        val session = JSONObject(fixtureFile("session.json").readText())
        val sessionId = PlmxSessionId(session.getString("sessionId").fromHex())
        val encoded = PlmxFrame.encodeHeader(
            LogicalChannel.TRANSPORT,
            PlmxFrameType.HELLO,
            PlmxFrame.HELLO_PAYLOAD_SIZE,
            sequence = 1,
            sessionTag = sessionId.tag,
        ) + PlmxFrame.encodeHello(session.getInt("capabilities"), sessionId)

        assertArrayEquals(fixtureBytes("hello-v2.json"), encoded)
    }

    private fun ByteArrayOutputStream.writeFrame(
        channel: LogicalChannel,
        type: PlmxFrameType,
        payload: ByteArray,
        sequence: Int,
        sessionTag: Int,
    ) {
        write(PlmxFrame.encodeHeader(channel, type, payload.size, sequence, sessionTag))
        write(payload)
    }

    private fun helloPayload(id: PlmxSessionId) = PlmxFrame.encodeHello(CAPABILITIES, id)

    private fun helloFrame(id: PlmxSessionId) =
        PlmxFrame.encodeHeader(
            LogicalChannel.TRANSPORT,
            PlmxFrameType.HELLO,
            PlmxFrame.HELLO_PAYLOAD_SIZE,
            1,
            id.tag,
        ) + helloPayload(id)

    /**
     * The stream never reports end of stream after the script: a live USB pipe
     * with nothing left to deliver blocks, and returning EOF would fail a
     * session for a reason the real carrier never produces.
     */
    private fun readerOver(bytes: ByteArray) = PlmxStreamReader(
        SequenceInputStream(
            Collections.enumeration(listOf(ByteArrayInputStream(bytes), BlockingInputStream())),
        ),
        TransportIoProfile(),
    )

    private class BlockingInputStream : InputStream() {
        override fun read(): Int = throw IllegalStateException("The scanner must not read one byte at a time")
        override fun read(buffer: ByteArray, offset: Int, length: Int): Int =
            throw AssertionError("The scanner ran past the handshake it was supposed to find")
    }

    private class EndlessInputStream(private val pattern: ByteArray) : InputStream() {
        private var position = 0
        override fun read(): Int = throw IllegalStateException("unused")
        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            val count = minOf(length, pattern.size - position)
            pattern.copyInto(buffer, offset, position, position + count)
            position = (position + count) % pattern.size
            return count
        }
    }

    private fun fixtureBytes(name: String) =
        JSONObject(fixtureFile(name).readText()).getString("hex").fromHex()

    private fun fixtureFile(name: String): File {
        var directory: File? = File(System.getProperty("user.dir")!!)
        while (directory != null) {
            val candidate = File(directory, "protocol-fixtures/plmx/$name")
            if (candidate.exists()) return candidate
            directory = directory.parentFile
        }
        throw AssertionError("protocol-fixtures/plmx/$name was not found")
    }

    private fun ByteArray.toHex() = joinToString("") { "%02x".format(it) }

    private fun String.fromHex() = ByteArray(length / 2) {
        substring(it * 2, it * 2 + 2).toInt(16).toByte()
    }

    private companion object {
        const val CAPABILITIES = 0b11111
    }
}
