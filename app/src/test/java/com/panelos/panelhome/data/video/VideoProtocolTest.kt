package com.panelos.panelhome.data.video

import java.io.ByteArrayInputStream
import java.io.EOFException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class VideoProtocolTest {
    @Test
    fun helloUsesFixedLittleEndianLayout() {
        val bytes = VideoProtocol.encodeHello(
            VideoHello(maxWidth = 640, maxHeight = 400, maxFps = 10),
        )
        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)

        assertEquals(VideoProtocol.HELLO_SIZE, bytes.size)
        assertArrayEquals(byteArrayOf(0x50, 0x4e, 0x4c, 0x56), bytes.copyOfRange(0, 4))
        assertEquals(1, buffer.getShort(4).toInt())
        assertEquals(VideoProtocol.TYPE_HELLO, buffer.getShort(6).toInt())
        assertEquals(640, buffer.getInt(8))
        assertEquals(400, buffer.getInt(12))
        assertEquals(10, buffer.getInt(16))
        assertEquals(
            VideoProtocol.CODEC_MASK_H264 or VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
            buffer.getInt(20),
        )
    }

    @Test
    fun ackAndRejectUseTheShared24ByteHandshakeLayout() {
        val ack = VideoProtocol.parseHandshake(
            handshake(
                type = VideoProtocol.TYPE_ACK,
                field0 = VideoProtocol.CODEC_ZLIB_BGRA8888,
                field1 = 640,
                field2 = 400,
                field3 = 10,
            ),
        ) as VideoHandshakeResponse.Accepted
        assertEquals(640, ack.maxWidth)
        assertEquals(400, ack.maxHeight)
        assertEquals(10, ack.maxFps)

        val reject = VideoProtocol.parseHandshake(
            handshake(type = VideoProtocol.TYPE_REJECT, field0 = 7),
        ) as VideoHandshakeResponse.Rejected
        assertEquals(7, reject.errorCode)
    }

    @Test
    fun frameHeaderParsesAllFixedFields() {
        val header = VideoProtocol.parseFrameHeader(
            frameHeader(
                sequence = 23,
                timestampMicros = 456_789,
                width = 2,
                height = 3,
                uncompressed = 24,
                compressed = 19,
            ),
        )

        assertEquals(23, header.sequence)
        assertEquals(456_789, header.captureTimestampMicros)
        assertEquals(2, header.width)
        assertEquals(3, header.height)
        assertEquals(VideoProtocol.PIXEL_FORMAT_BGRA8888, header.pixelFormat)
        assertEquals(24, header.uncompressedBytes)
        assertEquals(19, header.compressedBytes)
    }

    @Test
    fun invalidDimensionsAndByteCountsAreRejected() {
        assertThrows(VideoProtocolException::class.java) {
            VideoProtocol.parseFrameHeader(
                frameHeader(width = 0, height = 1, uncompressed = 0),
            )
        }
        assertThrows(VideoProtocolException::class.java) {
            VideoProtocol.parseFrameHeader(
                frameHeader(width = 2, height = 2, uncompressed = 15),
            )
        }
        assertThrows(VideoProtocolException::class.java) {
            VideoProtocol.parseHandshake(
                handshake(
                    type = VideoProtocol.TYPE_ACK,
                    field0 = VideoProtocol.CODEC_ZLIB_BGRA8888,
                    field1 = VideoProtocol.MAX_FRAME_WIDTH + 1,
                    field2 = 400,
                    field3 = 10,
                ),
            )
        }
    }

    @Test
    fun truncatedPayloadIsReported() {
        val destination = ByteArray(4)
        assertThrows(EOFException::class.java) {
            VideoProtocol.readExactly(ByteArrayInputStream(byteArrayOf(1, 2)), destination)
        }
    }

    @Test
    fun eofBeforeNextHeaderIsCleanPeerClose() {
        assertEquals(
            false,
            VideoProtocol.readMessageHeader(
                ByteArrayInputStream(byteArrayOf()),
                ByteArray(VideoProtocol.H264_PACKET_HEADER_SIZE),
            ),
        )
    }

    @Test
    fun partialNextHeaderIsProtocolTruncation() {
        val error = assertThrows(EOFException::class.java) {
            VideoProtocol.readMessageHeader(
                ByteArrayInputStream(ByteArray(12)),
                ByteArray(VideoProtocol.H264_PACKET_HEADER_SIZE),
            )
        }

        assertTrue(error.message.orEmpty().contains("header truncated at 12 of 40"))
    }

    @Test
    fun h264IsPreferredAndZlibRemainsAdvertisedByDefault() {
        assertTrue(VideoProtocol.CODEC_H264 > VideoProtocol.CODEC_ZLIB_BGRA8888)
        assertEquals(
            VideoProtocol.CODEC_MASK_H264 or VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
            VideoHello().supportedCodecMask,
        )
    }

    private fun handshake(
        type: Int,
        field0: Int = 0,
        field1: Int = 0,
        field2: Int = 0,
        field3: Int = 0,
    ): ByteArray = ByteBuffer.allocate(VideoProtocol.ACK_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf(0x50, 0x4e, 0x4c, 0x56))
        .putShort(VideoProtocol.VERSION.toShort())
        .putShort(type.toShort())
        .putInt(field0)
        .putInt(field1)
        .putInt(field2)
        .putInt(field3)
        .array()

    private fun frameHeader(
        sequence: Long = 1,
        timestampMicros: Long = 2,
        width: Int = 1,
        height: Int = 1,
        uncompressed: Int = 4,
        compressed: Int = 1,
    ): ByteArray = ByteBuffer.allocate(VideoProtocol.FRAME_HEADER_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf(0x50, 0x4e, 0x4c, 0x56))
        .putShort(VideoProtocol.VERSION.toShort())
        .putShort(VideoProtocol.TYPE_FRAME.toShort())
        .putLong(sequence)
        .putLong(timestampMicros)
        .putInt(width)
        .putInt(height)
        .putInt(VideoProtocol.PIXEL_FORMAT_BGRA8888)
        .putInt(uncompressed)
        .putInt(compressed)
        .array()
}
