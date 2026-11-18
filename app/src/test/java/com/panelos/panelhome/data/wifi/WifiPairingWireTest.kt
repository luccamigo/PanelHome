package com.panelos.panelhome.data.wifi

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.nio.ByteBuffer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class WifiPairingWireTest {
    private val names = setOf("type")
    private fun frame(json: String): ByteArray = json.toByteArray().let { ByteBuffer.allocate(4 + it.size).putInt(it.size).put(it).array() }
    private fun reader(vararg frames: ByteArray) = WifiPairingWire(ByteArrayInputStream(frames.reduce { a, b -> a + b }), ByteArrayOutputStream())

    @Test fun writesBigEndianLengthThenUtf8AndReadsItBack() {
        val out = ByteArrayOutputStream()
        WifiPairingWire(ByteArrayInputStream(ByteArray(0)), out).write("""{"type":"challenge"}""")
        assertArrayEquals(frame("""{"type":"challenge"}"""), out.toByteArray())
        assertEquals("challenge", reader(out.toByteArray()).read(names)["type"])
    }

    @Test fun anOversizedPrefixIsRefusedBeforeAnyBodyIsRead() {
        for (length in listOf(0, 16385, Int.MAX_VALUE, -1)) {
            val stream = ByteArrayInputStream(ByteBuffer.allocate(4).putInt(length).array())
            assertThrows("$length", IllegalStateException::class.java) { WifiPairingWire(stream, ByteArrayOutputStream()).read(names) }
            assertEquals("the body was never touched", 0, stream.available())
        }
    }

    @Test fun atMostFourMessagesAndThirtyTwoKilobytesPerDirection() {
        val wire = reader(*Array(5) { frame("""{"type":"a"}""") })
        repeat(4) { wire.read(names) }
        assertThrows(IllegalStateException::class.java) { wire.read(names) }
        val out = WifiPairingWire(ByteArrayInputStream(ByteArray(0)), ByteArrayOutputStream())
        repeat(4) { out.write("""{"type":"a"}""") }
        assertThrows(IllegalStateException::class.java) { out.write("""{"type":"a"}""") }
        val big = """{"type":"${"x".repeat(15 * 1024)}"}"""
        val sizes = WifiPairingWire(ByteArrayInputStream(ByteArray(0)), ByteArrayOutputStream())
        sizes.write(big); sizes.write(big)
        assertThrows("32 KiB total", IllegalStateException::class.java) { sizes.write(big) }
    }

    @Test fun truncatedAndNonUtf8InputFailsClosed() {
        assertThrows(EOFException::class.java) { WifiPairingWire(ByteArrayInputStream(byteArrayOf(0, 0)), ByteArrayOutputStream()).read(names) }
        assertThrows(EOFException::class.java) { WifiPairingWire(ByteArrayInputStream(frame("""{"type":"a"}""").copyOf(8)), ByteArrayOutputStream()).read(names) }
        val invalid = byteArrayOf(0x7b, 0x22, 0xC3.toByte(), 0x28, 0x22, 0x7d)
        val bytes = ByteBuffer.allocate(4 + invalid.size).putInt(invalid.size).put(invalid).array()
        assertThrows(Exception::class.java) { WifiPairingWire(ByteArrayInputStream(bytes), ByteArrayOutputStream()).read(names) }
    }
}