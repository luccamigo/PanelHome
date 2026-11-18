package com.panelos.panelhome.data.video

import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

object VideoProtocol {
    const val VERSION = 1
    const val HELLO_SIZE = 24
    const val ACK_SIZE = 24
    const val FRAME_HEADER_SIZE = 44
    const val H264_PACKET_HEADER_SIZE = 40

    const val TYPE_HELLO = 1
    const val TYPE_ACK = 2
    const val TYPE_REJECT = 3
    const val TYPE_FRAME = 4
    const val TYPE_H264_PACKET = 5

    const val PIXEL_FORMAT_BGRA8888 = 1
    const val CODEC_ZLIB_BGRA8888 = 1
    const val CODEC_H264 = 2
    const val CODEC_MASK_ZLIB_BGRA8888 = 1 shl 0
    const val CODEC_MASK_H264 = 1 shl 1

    const val ZLIB_MAX_WIDTH = 640
    const val ZLIB_MAX_HEIGHT = 400
    const val ZLIB_MAX_FPS = 10
    const val MAX_FRAME_WIDTH = 4_096
    const val MAX_FRAME_HEIGHT = 4_096
    const val MAX_UNCOMPRESSED_BYTES = 64 * 1024 * 1024
    const val MAX_COMPRESSED_BYTES = MAX_UNCOMPRESSED_BYTES + (64 * 1024)

    private val magic = byteArrayOf('P'.code.toByte(), 'N'.code.toByte(), 'L'.code.toByte(), 'V'.code.toByte())

    fun encodeHello(hello: VideoHello = VideoHello()): ByteArray {
        require(hello.maxWidth in 1..MAX_FRAME_WIDTH) { "Invalid maximum width" }
        require(hello.maxHeight in 1..MAX_FRAME_HEIGHT) { "Invalid maximum height" }
        require(hello.maxFps in 1..240) { "Invalid maximum FPS" }
        require(hello.supportedCodecMask != 0) { "At least one codec is required" }

        return littleEndianBuffer(HELLO_SIZE)
            .put(magic)
            .putShort(VERSION.toShort())
            .putShort(TYPE_HELLO.toShort())
            .putInt(hello.maxWidth)
            .putInt(hello.maxHeight)
            .putInt(hello.maxFps)
            .putInt(hello.supportedCodecMask)
            .array()
    }

    fun parseHandshake(bytes: ByteArray): VideoHandshakeResponse {
        if (bytes.size != ACK_SIZE) {
            throw VideoProtocolException("Invalid video handshake size: ${bytes.size}")
        }
        val buffer = littleEndianBuffer(bytes)
        requireHeader(buffer, expectedType = null)
        return when (val type = buffer.getShort(6).toInt() and 0xffff) {
            TYPE_ACK -> {
                val codec = buffer.u32At(8).toIntExact("selected codec")
                val width = buffer.u32At(12).toIntExact("selected width")
                val height = buffer.u32At(16).toIntExact("selected height")
                val fps = buffer.u32At(20).toIntExact("selected FPS")
                if (codec !in setOf(CODEC_ZLIB_BGRA8888, CODEC_H264)) {
                    throw VideoProtocolException("Unsupported selected codec: $codec")
                }
                validateDimensions(width, height)
                if (fps !in 1..240) {
                    throw VideoProtocolException("Invalid selected FPS: $fps")
                }
                VideoHandshakeResponse.Accepted(
                    codec = codec,
                    maxWidth = width,
                    maxHeight = height,
                    maxFps = fps,
                )
            }

            TYPE_REJECT -> VideoHandshakeResponse.Rejected(
                errorCode = buffer.u32At(8).toIntExact("error code"),
            )

            else -> throw VideoProtocolException("Unexpected video handshake type: $type")
        }
    }

    fun parseFrameHeader(bytes: ByteArray): VideoFrameHeader {
        if (bytes.size != FRAME_HEADER_SIZE) {
            throw VideoProtocolException("Invalid frame header size: ${bytes.size}")
        }
        val buffer = littleEndianBuffer(bytes)
        requireHeader(buffer, TYPE_FRAME)
        val sequence = buffer.getLong(8)
        val timestampMicros = buffer.getLong(16)
        val width = buffer.u32At(24).toIntExact("frame width")
        val height = buffer.u32At(28).toIntExact("frame height")
        val pixelFormat = buffer.u32At(32).toIntExact("pixel format")
        val uncompressedBytes = buffer.u32At(36).toIntExact("uncompressed size")
        val compressedBytes = buffer.u32At(40).toIntExact("compressed size")

        if (sequence <= 0) throw VideoProtocolException("Invalid frame sequence: $sequence")
        if (timestampMicros < 0) throw VideoProtocolException("Invalid capture timestamp")
        validateDimensions(width, height)
        if (pixelFormat != PIXEL_FORMAT_BGRA8888) {
            throw VideoProtocolException("Unsupported pixel format: $pixelFormat")
        }

        val expectedBytes = checkedPixelBytes(width, height)
        if (uncompressedBytes != expectedBytes) {
            throw VideoProtocolException(
                "Frame size mismatch: expected $expectedBytes, received $uncompressedBytes",
            )
        }
        val frameCompressedLimit = minOf(
            MAX_COMPRESSED_BYTES,
            uncompressedBytes + (64 * 1024),
        )
        if (compressedBytes !in 1..frameCompressedLimit) {
            throw VideoProtocolException("Invalid compressed size: $compressedBytes")
        }

        return VideoFrameHeader(
            sequence = sequence,
            captureTimestampMicros = timestampMicros,
            width = width,
            height = height,
            pixelFormat = pixelFormat,
            uncompressedBytes = uncompressedBytes,
            compressedBytes = compressedBytes,
        )
    }

    fun parseH264PacketHeader(bytes: ByteArray): H264PacketHeader {
        if (bytes.size != H264_PACKET_HEADER_SIZE) {
            throw VideoProtocolException("Invalid H.264 packet header size: ${bytes.size}")
        }
        val buffer = littleEndianBuffer(bytes)
        requireHeader(buffer, TYPE_H264_PACKET)
        val sequence = buffer.getLong(8)
        val timestampMicros = buffer.getLong(16)
        val flags = buffer.u32At(24).toIntExact("H.264 flags")
        val payloadBytes = buffer.u32At(28).toIntExact("H.264 payload size")
        val encodeDurationMicros = buffer.getLong(32)
        val knownFlags = H264_FLAG_CONFIG or H264_FLAG_KEYFRAME or H264_FLAG_END_OF_ACCESS_UNIT

        if (sequence <= 0L || timestampMicros < 0L || encodeDurationMicros < 0L ||
            flags == 0 || (flags and knownFlags.inv()) != 0 ||
            payloadBytes !in 1..MAX_H264_PAYLOAD_BYTES ||
            (flags and H264_FLAG_CONFIG) == 0 &&
            (flags and H264_FLAG_END_OF_ACCESS_UNIT) == 0
        ) {
            throw VideoProtocolException("Invalid H.264 packet header")
        }

        return H264PacketHeader(
            sequence = sequence,
            captureTimestampMicros = timestampMicros,
            flags = flags,
            payloadBytes = payloadBytes,
            encodeDurationMicros = encodeDurationMicros,
        )
    }

    fun readExactly(input: InputStream, destination: ByteArray, length: Int = destination.size) {
        require(length in 0..destination.size)
        var offset = 0
        while (offset < length) {
            val read = input.read(destination, offset, length - offset)
            if (read < 0) {
                throw EOFException("Video payload truncated at $offset of $length bytes")
            }
            if (read == 0) continue
            offset += read
        }
    }

    /**
     * Reads the next fixed message header. EOF before any header byte is a
     * clean peer FIN; EOF after a partial header is a protocol truncation.
     */
    fun readMessageHeader(
        input: InputStream,
        destination: ByteArray,
        length: Int = destination.size,
    ): Boolean {
        require(length in 0..destination.size)
        var offset = 0
        while (offset < length) {
            val read = input.read(destination, offset, length - offset)
            if (read < 0) {
                if (offset == 0) return false
                throw EOFException("Video header truncated at $offset of $length bytes")
            }
            if (read == 0) continue
            offset += read
        }
        return true
    }

    fun checkedPixelBytes(width: Int, height: Int): Int {
        validateDimensions(width, height)
        val bytes = width.toLong() * height.toLong() * 4L
        if (bytes > MAX_UNCOMPRESSED_BYTES || bytes > Int.MAX_VALUE) {
            throw VideoProtocolException("Frame dimensions exceed the payload limit")
        }
        return bytes.toInt()
    }

    private fun validateDimensions(width: Int, height: Int) {
        if (width !in 1..MAX_FRAME_WIDTH || height !in 1..MAX_FRAME_HEIGHT) {
            throw VideoProtocolException("Invalid frame dimensions: ${width}x$height")
        }
    }

    private fun requireHeader(buffer: ByteBuffer, expectedType: Int?) {
        for (index in magic.indices) {
            if (buffer.get(index) != magic[index]) {
                throw VideoProtocolException("Invalid video protocol magic")
            }
        }
        val version = buffer.getShort(4).toInt() and 0xffff
        if (version != VERSION) {
            throw VideoProtocolException("Unsupported video protocol version: $version")
        }
        if (expectedType != null) {
            val type = buffer.getShort(6).toInt() and 0xffff
            if (type != expectedType) {
                throw VideoProtocolException("Unexpected video message type: $type")
            }
        }
    }

    private fun littleEndianBuffer(size: Int): ByteBuffer =
        ByteBuffer.allocate(size).order(ByteOrder.LITTLE_ENDIAN)

    private fun littleEndianBuffer(bytes: ByteArray): ByteBuffer =
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)

    private fun ByteBuffer.u32At(offset: Int): Long =
        getInt(offset).toLong() and 0xffff_ffffL

    private fun Long.toIntExact(label: String): Int {
        if (this > Int.MAX_VALUE) throw VideoProtocolException("Invalid $label: $this")
        return toInt()
    }

    const val H264_FLAG_CONFIG = 1 shl 0
    const val H264_FLAG_KEYFRAME = 1 shl 1
    const val H264_FLAG_END_OF_ACCESS_UNIT = 1 shl 2
    // At 10 Mbps and a one-second GOP this is generous for one access unit,
    // while preventing a malicious header from forcing a 16 MiB allocation.
    const val MAX_H264_PAYLOAD_BYTES = 2 * 1024 * 1024
}

data class VideoHello(
    val maxWidth: Int = VideoClientCapabilities.H264.maxWidth,
    val maxHeight: Int = VideoClientCapabilities.H264.maxHeight,
    val maxFps: Int = VideoClientCapabilities.H264.maxFps,
    val supportedCodecMask: Int = VideoClientCapabilities.H264.supportedCodecMask,
)

sealed interface VideoHandshakeResponse {
    data class Accepted(
        val codec: Int,
        val maxWidth: Int,
        val maxHeight: Int,
        val maxFps: Int,
    ) : VideoHandshakeResponse

    data class Rejected(val errorCode: Int) : VideoHandshakeResponse
}

data class VideoFrameHeader(
    val sequence: Long,
    val captureTimestampMicros: Long,
    val width: Int,
    val height: Int,
    val pixelFormat: Int,
    val uncompressedBytes: Int,
    val compressedBytes: Int,
)

data class H264PacketHeader(
    val sequence: Long,
    val captureTimestampMicros: Long,
    val flags: Int,
    val payloadBytes: Int,
    val encodeDurationMicros: Long,
) {
    val isConfig: Boolean get() = flags and VideoProtocol.H264_FLAG_CONFIG != 0
    val isKeyFrame: Boolean get() = flags and VideoProtocol.H264_FLAG_KEYFRAME != 0
    val isEndOfAccessUnit: Boolean
        get() = flags and VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT != 0
}

data class H264VideoPacket(
    val header: H264PacketHeader,
    val payload: ByteArray,
    val receivedAtNanos: Long = System.nanoTime(),
)

class VideoProtocolException(message: String, cause: Throwable? = null) : IOException(message, cause)
