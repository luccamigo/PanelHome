package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import java.io.EOFException
import java.io.InputStream
import java.util.zip.DataFormatException
import java.util.zip.Inflater

class VideoFrameDecoder(
    private val maxWidth: Int,
    private val maxHeight: Int,
    private val logger: VideoLogger = SilentVideoLogger,
) : AutoCloseable {
    private val frameHeader = ByteArray(VideoProtocol.FRAME_HEADER_SIZE)
    private var compressed = ByteArray(0)
    private var decompressed = ByteArray(0)
    private val overflowProbe = ByteArray(1)
    private val inflater = Inflater()
    private var lastSequence = 0L
    private var inspectedFrames = 0

    /** Returns null for an old/duplicate sequence after consuming its payload. */
    fun readFrame(input: InputStream): DecodedVideoFrame? {
        stage("header_read", null, logEndOfStream = false) {
            if (!VideoProtocol.readMessageHeader(input, frameHeader)) {
                throw VideoPeerClosedException()
            }
        }
        val header = stage("header_parse", null) {
            VideoProtocol.parseFrameHeader(frameHeader)
        }
        val debugFrame = inspectedFrames++ < DEBUG_FRAME_LIMIT
        if (debugFrame) {
            logger.debug(
                "Frame header received: sequence=${header.sequence} " +
                    "${header.width}x${header.height} compressed=${header.compressedBytes} " +
                    "uncompressed=${header.uncompressedBytes}",
            )
        }
        if (header.width > maxWidth || header.height > maxHeight) {
            val error = VideoProtocolException(
                "Frame ${header.width}x${header.height} exceeds negotiated ${maxWidth}x$maxHeight",
            )
            logger.error("dimensions", header.sequence, error)
            throw error
        }

        ensureCompressedCapacity(header.compressedBytes)
        stage("payload_read", header.sequence) {
            VideoProtocol.readExactly(input, compressed, header.compressedBytes)
        }
        if (debugFrame) {
            logger.debug(
                "Payload received: sequence=${header.sequence} bytes=${header.compressedBytes}",
            )
        }
        if (header.sequence <= lastSequence) return null

        ensureDecompressedCapacity(header.uncompressedBytes)
        stage("inflate", header.sequence) {
            inflateExactly(header)
        }
        if (debugFrame) {
            logger.debug("Inflate success: sequence=${header.sequence} bytes=${header.uncompressedBytes}")
            logger.debug(
                VideoFrameDiagnostics.describeBgra(
                    label = "Android BGRA",
                    sequence = header.sequence,
                    width = header.width,
                    height = header.height,
                    pixels = decompressed,
                    byteCount = header.uncompressedBytes,
                ),
            )
            logger.debug("Frame accepted: sequence=${header.sequence}")
        }
        lastSequence = header.sequence
        return DecodedVideoFrame(
            sequence = header.sequence,
            captureTimestampMicros = header.captureTimestampMicros,
            width = header.width,
            height = header.height,
            pixels = decompressed,
            pixelCountBytes = header.uncompressedBytes,
        )
    }

    override fun close() {
        inflater.end()
    }

    private fun inflateExactly(header: VideoFrameHeader) {
        inflater.reset()
        inflater.setInput(compressed, 0, header.compressedBytes)
        var outputOffset = 0
        try {
            while (outputOffset < header.uncompressedBytes && !inflater.finished()) {
                val count = inflater.inflate(
                    decompressed,
                    outputOffset,
                    header.uncompressedBytes - outputOffset,
                )
                if (count == 0) {
                    if (inflater.needsDictionary()) {
                        throw VideoProtocolException("Frame zlib payload requires a dictionary")
                    }
                    if (inflater.needsInput()) {
                        throw VideoProtocolException("Frame zlib payload is truncated")
                    }
                    throw VideoProtocolException("Frame zlib decoder made no progress")
                }
                outputOffset += count
            }

            if (outputOffset != header.uncompressedBytes) {
                throw VideoProtocolException(
                    "Decoded frame size mismatch: $outputOffset/${header.uncompressedBytes}",
                )
            }
            if (!inflater.finished()) {
                val overflow = inflater.inflate(overflowProbe)
                if (overflow > 0 || !inflater.finished()) {
                    throw VideoProtocolException("Decoded frame exceeds its declared size")
                }
            }
            if (inflater.remaining != 0) {
                throw VideoProtocolException("Frame zlib payload contains trailing data")
            }
        } catch (error: DataFormatException) {
            throw VideoProtocolException("Invalid frame zlib payload", error)
        }
    }

    private fun ensureCompressedCapacity(size: Int) {
        if (compressed.size < size) compressed = ByteArray(size)
    }

    private fun ensureDecompressedCapacity(size: Int) {
        if (decompressed.size < size) decompressed = ByteArray(size)
    }

    private inline fun <T> stage(
        stage: String,
        sequence: Long?,
        logEndOfStream: Boolean = true,
        block: () -> T,
    ): T =
        try {
            block()
        } catch (error: EOFException) {
            // EOF between frames is classified by TcpVideoConnection, which
            // knows whether stop/disconnect closed the socket intentionally.
            if (logEndOfStream) logger.error(stage, sequence, error)
            throw error
        } catch (error: Exception) {
            logger.error(stage, sequence, error)
            throw error
        }

    private companion object {
        const val DEBUG_FRAME_LIMIT = 3
    }
}

internal class VideoPeerClosedException : EOFException("Video peer closed between messages")
