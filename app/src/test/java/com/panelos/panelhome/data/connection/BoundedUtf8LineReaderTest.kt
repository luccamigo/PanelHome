package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.protocol.PanelProtocolException
import java.io.ByteArrayInputStream
import java.io.EOFException
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class BoundedUtf8LineReaderTest {
    @Test
    fun normalPartialAndMultipleLinesArePreserved() {
        val reader = BoundedUtf8LineReader(
            ByteArrayInputStream("one\ntwÃ³\nthree\n".toByteArray()),
            maximumBytes = 16,
            bufferSize = 3,
        )

        assertEquals("one", reader.readLine())
        assertEquals("twÃ³", reader.readLine())
        assertEquals("three", reader.readLine())
        assertNull(reader.readLine())
    }

    @Test
    fun exactByteLimitIncludingMultibyteUtf8IsAccepted() {
        val reader = BoundedUtf8LineReader(
            ByteArrayInputStream("\u00e9\u00e9\n".toByteArray()),
            maximumBytes = 4,
            bufferSize = 1,
        )

        assertEquals("\u00e9\u00e9", reader.readLine())
    }

    @Test(expected = PanelProtocolException::class)
    fun limitPlusOneIsRejected() {
        BoundedUtf8LineReader(
            ByteArrayInputStream("12345\n".toByteArray()),
            maximumBytes = 4,
            bufferSize = 2,
        ).readLine()
    }

    @Test(expected = PanelProtocolException::class)
    fun oversizedLineWithoutNewlineIsRejected() {
        BoundedUtf8LineReader(
            ByteArrayInputStream(ByteArray(33)),
            maximumBytes = 32,
            bufferSize = 7,
        ).readLine()
    }

    @Test(expected = PanelProtocolException::class)
    fun invalidUtf8IsRejected() {
        BoundedUtf8LineReader(
            ByteArrayInputStream(byteArrayOf(0xc3.toByte(), 0x28, 0x0a)),
            maximumBytes = 8,
        ).readLine()
    }

    @Test(expected = EOFException::class)
    fun disconnectDuringLineIsTruncation() {
        BoundedUtf8LineReader(
            ByteArrayInputStream("partial".toByteArray()),
            maximumBytes = 32,
        ).readLine()
    }

    @Test
    fun tenThousandSmallMessagesDoNotGrowTheReader() {
        val reader = BoundedUtf8LineReader(
            ByteArrayInputStream(buildString { repeat(10_000) { append("{}\n") } }.toByteArray()),
            maximumBytes = 16,
            bufferSize = 31,
        )

        repeat(10_000) { assertEquals("{}", reader.readLine()) }
        assertNull(reader.readLine())
    }
}
