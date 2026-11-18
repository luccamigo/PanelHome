package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import com.panelos.panelhome.ui.desktop.BgraToArgbConverter
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.zip.DeflaterOutputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertThrows
import org.junit.Test

class VideoFrameDecoderTest {
    @Test
    fun zlibFrameRoundTripsAndReusesTheDecodeBuffer() {
        val firstPixels = byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8)
        val secondPixels = byteArrayOf(8, 7, 6, 5, 4, 3, 2, 1)

        VideoFrameDecoder(maxWidth = 2, maxHeight = 1).use { decoder ->
            val first = requireNotNull(
                decoder.readFrame(
                    ByteArrayInputStream(frame(sequence = 1, width = 2, pixels = firstPixels)),
                ),
            )
            assertArrayEquals(firstPixels, first.pixels.copyOf(first.pixelCountBytes))
            val second = requireNotNull(
                decoder.readFrame(
                    ByteArrayInputStream(frame(sequence = 2, width = 2, pixels = secondPixels)),
                ),
            )
            assertSame(first.pixels, second.pixels)
            assertArrayEquals(secondPixels, second.pixels.copyOf(second.pixelCountBytes))
        }
    }

    @Test
    fun staleSequenceIsConsumedAndDiscarded() {
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1).use { decoder ->
            val newest = decoder.readFrame(
                ByteArrayInputStream(frame(sequence = 4, pixels = byteArrayOf(1, 2, 3, 4))),
            )
            val stale = decoder.readFrame(
                ByteArrayInputStream(frame(sequence = 3, pixels = byteArrayOf(4, 3, 2, 1))),
            )

            assertEquals(4L, newest?.sequence)
            assertNull(stale)
        }
    }

    @Test
    fun truncatedAndInvalidCompressedPayloadsFailDefensively() {
        val valid = frame(sequence = 1, pixels = byteArrayOf(1, 2, 3, 4))
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1).use { decoder ->
            assertThrows(Exception::class.java) {
                decoder.readFrame(ByteArrayInputStream(valid.copyOf(valid.size - 1)))
            }
        }

        val invalid = frameWithPayload(byteArrayOf(1, 2, 3, 4))
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1).use { decoder ->
            assertThrows(VideoProtocolException::class.java) {
                decoder.readFrame(ByteArrayInputStream(invalid))
            }
        }
    }

    @Test
    fun inflateFailureReportsItsStageAndSequence() {
        val logger = RecordingVideoLogger()
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1, logger = logger).use { decoder ->
            assertThrows(VideoProtocolException::class.java) {
                decoder.readFrame(ByteArrayInputStream(frameWithPayload(byteArrayOf(1, 2, 3, 4))))
            }
        }

        assertEquals("inflate", logger.lastErrorStage)
        assertEquals(1L, logger.lastErrorSequence)
    }

    @Test
    fun eofBetweenFramesIsLeftForConnectionLifecycleClassification() {
        val logger = RecordingVideoLogger()
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1, logger = logger).use { decoder ->
            assertThrows(Exception::class.java) {
                decoder.readFrame(ByteArrayInputStream(ByteArray(0)))
            }
        }

        assertNull(logger.lastErrorStage)
        assertNull(logger.lastErrorSequence)
    }

    @Test
    fun negotiatedDimensionsAreEnforced() {
        val pixels = ByteArray(2 * 2 * 4)
        VideoFrameDecoder(maxWidth = 1, maxHeight = 1).use { decoder ->
            assertThrows(VideoProtocolException::class.java) {
                decoder.readFrame(
                    ByteArrayInputStream(frame(sequence = 1, width = 2, height = 2, pixels = pixels)),
                )
            }
        }
    }

    @Test
    fun zlibRgbwTestPatternDecodesWithKnownOpaqueArgbPixels() {
        val width = 640
        val height = 400
        val pixels = rgbwPattern(width, height)

        val decoded = VideoFrameDecoder(maxWidth = width, maxHeight = height).use { decoder ->
            requireNotNull(
                decoder.readFrame(
                    ByteArrayInputStream(
                        frame(sequence = 31, width = width, height = height, pixels = pixels),
                    ),
                ),
            )
        }
        assertArrayEquals(pixels, decoded.pixels.copyOf(decoded.pixelCountBytes))

        val argb = BgraToArgbConverter().convert(decoded.pixels, width, height)
        assertEquals(0xffff0000L, argb[(100 * width) + 100].toLong() and 0xffff_ffffL)
        assertEquals(0xff00ff00L, argb[(100 * width) + 500].toLong() and 0xffff_ffffL)
        assertEquals(0xff0000ffL, argb[(300 * width) + 100].toLong() and 0xffff_ffffL)
        assertEquals(0xffffffffL, argb[(300 * width) + 500].toLong() and 0xffff_ffffL)
    }

    private fun frame(
        sequence: Long,
        width: Int = 1,
        height: Int = 1,
        pixels: ByteArray,
    ): ByteArray {
        val compressed = ByteArrayOutputStream().also { output ->
            DeflaterOutputStream(output).use { it.write(pixels) }
        }.toByteArray()
        return frameWithPayload(compressed, sequence, width, height, pixels.size)
    }

    private fun rgbwPattern(width: Int, height: Int): ByteArray {
        val pixels = ByteArray(width * height * 4)
        for (y in 0 until height) {
            for (x in 0 until width) {
                val offset = ((y * width) + x) * 4
                val right = x >= width / 2
                val bottom = y >= height / 2
                val (blue, green, red) = when {
                    !right && !bottom -> Triple(0, 0, 255)
                    right && !bottom -> Triple(0, 255, 0)
                    !right -> Triple(255, 0, 0)
                    else -> Triple(255, 255, 255)
                }
                pixels[offset] = blue.toByte()
                pixels[offset + 1] = green.toByte()
                pixels[offset + 2] = red.toByte()
                pixels[offset + 3] = 0 // prove that Android presentation makes alpha opaque
            }
        }
        return pixels
    }

    private fun frameWithPayload(
        payload: ByteArray,
        sequence: Long = 1,
        width: Int = 1,
        height: Int = 1,
        uncompressedBytes: Int = 4,
    ): ByteArray {
        val header = ByteBuffer.allocate(VideoProtocol.FRAME_HEADER_SIZE)
            .order(ByteOrder.LITTLE_ENDIAN)
            .put(byteArrayOf(0x50, 0x4e, 0x4c, 0x56))
            .putShort(VideoProtocol.VERSION.toShort())
            .putShort(VideoProtocol.TYPE_FRAME.toShort())
            .putLong(sequence)
            .putLong(100)
            .putInt(width)
            .putInt(height)
            .putInt(VideoProtocol.PIXEL_FORMAT_BGRA8888)
            .putInt(uncompressedBytes)
            .putInt(payload.size)
            .array()
        return header + payload
    }

    private class RecordingVideoLogger : VideoLogger {
        var lastErrorStage: String? = null
        var lastErrorSequence: Long? = null

        override fun transition(message: String) = Unit

        override fun debug(message: String) = Unit

        override fun error(stage: String, sequence: Long?, error: Throwable) {
            lastErrorStage = stage
            lastErrorSequence = sequence
        }
    }
}
