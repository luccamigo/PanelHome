package com.panelos.panelhome.data.transport

import java.io.EOFException
import java.io.InputStream
import java.net.Socket

/** Development-only framing consumed before Panel Protocol/video/benchmark. */
internal object AdbTcpAuthentication {
    const val TOKEN_BYTES = 32
    const val TOKEN_HEX_CHARS = TOKEN_BYTES * 2
    const val TIMEOUT_MILLIS = 3_000
    const val MAX_CREDENTIAL_BYTES = 64
    private const val HEADER_BYTES = 8
    private const val ACK_BYTES = 8
    private val requestMagic = byteArrayOf('P'.code.toByte(), 'A'.code.toByte(), 'D'.code.toByte(), 'A'.code.toByte())
    private val ackMagic = byteArrayOf('P'.code.toByte(), 'A'.code.toByte(), 'D'.code.toByte(), 'O'.code.toByte())

    fun parseToken(encoded: String?): ByteArray? {
        if (encoded == null || encoded.length != TOKEN_HEX_CHARS) return null
        return try {
            ByteArray(TOKEN_BYTES) { index ->
                val high = encoded[index * 2].digitToInt(16)
                val low = encoded[index * 2 + 1].digitToInt(16)
                ((high shl 4) or low).toByte()
            }
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    fun authenticate(socket: Socket, channel: LogicalChannel, token: ByteArray) {
        require(token.size == TOKEN_BYTES) { "ADB/TCP authentication token must be 256 bits" }
        val previousTimeout = socket.soTimeout
        socket.soTimeout = TIMEOUT_MILLIS
        try {
            val request = encodeRequest(channel, token)
            socket.getOutputStream().apply {
                write(request)
                flush()
            }
            val ack = ByteArray(ACK_BYTES)
            readExactly(socket.getInputStream(), ack)
            if (!isValidAck(ack, channel)) {
                throw TransportDisconnectedException("ADB/TCP authentication was rejected")
            }
        } finally {
            socket.soTimeout = previousTimeout
        }
    }

    internal fun encodeRequest(channel: LogicalChannel, token: ByteArray): ByteArray {
        require(token.size <= MAX_CREDENTIAL_BYTES)
        return ByteArray(HEADER_BYTES + token.size).also { message ->
            requestMagic.copyInto(message)
            message[4] = 1
            message[5] = channel.id.toByte()
            message[6] = (token.size ushr 8).toByte()
            message[7] = token.size.toByte()
            token.copyInto(message, HEADER_BYTES)
        }
    }

    internal fun isValidAck(ack: ByteArray, channel: LogicalChannel): Boolean =
        ack.size == ACK_BYTES &&
            ack.sliceArray(0 until 4).contentEquals(ackMagic) &&
            ack[4].toInt() == 1 &&
            ack[5].toInt() == channel.id &&
            ack[6].toInt() == 0 &&
            ack[7].toInt() == 0

    private fun readExactly(input: InputStream, target: ByteArray) {
        var offset = 0
        while (offset < target.size) {
            val read = input.read(target, offset, target.size - offset)
            if (read < 0) throw EOFException("ADB/TCP authentication closed before acknowledgement")
            offset += read
        }
    }
}
