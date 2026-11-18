package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.PanelProtocolException
import java.io.ByteArrayOutputStream
import java.io.EOFException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets

/** Incremental LF reader that rejects oversized or malformed UTF-8 before JSON parsing. */
internal class BoundedUtf8LineReader(
    private val input: InputStream,
    private val maximumBytes: Int = PanelProtocol.MAX_CONTROL_MESSAGE_BYTES,
    bufferSize: Int = DEFAULT_BUFFER_SIZE,
) {
    private val readBuffer = ByteArray(bufferSize)
    private val line = ByteArrayOutputStream(minOf(bufferSize, maximumBytes))
    private var bufferOffset = 0
    private var bufferCount = 0

    init {
        require(maximumBytes > 0)
        require(bufferSize > 0)
    }

    fun readLine(): String? {
        while (true) {
            if (bufferOffset < bufferCount) {
                var newline = bufferOffset
                while (newline < bufferCount && readBuffer[newline] != LF) newline++
                val segmentBytes = newline - bufferOffset
                if (line.size().toLong() + segmentBytes > maximumBytes) {
                    throw PanelProtocolException(
                        "JSONL message exceeds $maximumBytes UTF-8 bytes",
                    )
                }
                if (segmentBytes > 0) {
                    line.write(readBuffer, bufferOffset, segmentBytes)
                }
                bufferOffset = newline
                if (newline < bufferCount) {
                    bufferOffset++
                    return decodeAndReset()
                }
            }

            bufferOffset = 0
            bufferCount = input.read(readBuffer)
            if (bufferCount >= 0) continue
            if (line.size() == 0) return null
            line.reset()
            throw EOFException("PanelHost disconnected during a JSONL message")
        }
    }

    private fun decodeAndReset(): String {
        val bytes = line.toByteArray()
        line.reset()
        val length = if (bytes.lastOrNull() == CR) bytes.size - 1 else bytes.size
        return try {
            StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(bytes, 0, length))
                .toString()
        } catch (error: Exception) {
            throw PanelProtocolException("JSONL message contains invalid UTF-8", error)
        }
    }

    private companion object {
        const val DEFAULT_BUFFER_SIZE = 4 * 1024
        const val LF: Byte = 0x0A
        const val CR: Byte = 0x0D
    }
}
