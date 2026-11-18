package com.panelos.panelhome.data.transport

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class MultiplexFrameTest {
    @Test
    fun headerIsFixedLittleEndianAndRoundTrips() {
        val bytes = MultiplexFrame.encodeHeader(
            LogicalChannel.VIDEO,
            flags = 0x1234,
            payloadLength = 65_536,
            sequence = 0x10203040,
        )

        assertArrayEquals(byteArrayOf('P'.code.toByte(), 'L'.code.toByte(), 'M'.code.toByte(), 'X'.code.toByte()), bytes.copyOf(4))
        assertEquals(16, bytes.size)
        val decoded = MultiplexFrame.decodeHeader(bytes)
        assertEquals(LogicalChannel.VIDEO, decoded.channel)
        assertEquals(0x1234, decoded.flags)
        assertEquals(65_536, decoded.payloadLength)
        assertEquals(0x10203040, decoded.sequence)
    }

    @Test(expected = TransportProtocolException::class)
    fun corruptedMagicIsRejected() {
        val bytes = MultiplexFrame.encodeHeader(LogicalChannel.CONTROL, 0, 1, 1)
        bytes[0] = 0
        MultiplexFrame.decodeHeader(bytes)
    }

    @Test(expected = TransportProtocolException::class)
    fun oversizedPayloadIsRejectedBeforeAllocation() {
        val bytes = MultiplexFrame.encodeHeader(LogicalChannel.VIDEO, 0, 1, 1)
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(8, MultiplexFrame.MAX_VIDEO_PAYLOAD + 1)
        MultiplexFrame.decodeHeader(bytes)
    }

    @Test(expected = TransportProtocolException::class)
    fun unknownChannelIsRejected() {
        val bytes = MultiplexFrame.encodeHeader(LogicalChannel.CONTROL, 0, 1, 1)
        bytes[5] = 0x7f
        MultiplexFrame.decodeHeader(bytes)
    }
}
