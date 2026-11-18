package com.panelos.panelhome.data.wifi

import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction

/**
 * Length-prefixed JSON of the pairing TLS stream (seguranca-wifi.md section 5): a big-endian uint32, then 1..16 KiB of
 * UTF-8, at most four messages and 32 KiB per direction. The length is checked BEFORE anything is allocated. Read
 * deadlines are the socket's: five seconds per message, and the caller widens only the wait for the operator's approval.
 */
internal class WifiPairingWire(private val input: InputStream, private val output: OutputStream) {
    private var readMessages = 0
    private var readBytes = 0
    private var writtenMessages = 0
    private var writtenBytes = 0

    fun read(names: Set<String>, numeric: Set<String> = emptySet(), optional: Set<String> = emptySet()): Map<String, String> {
        val prefix = ByteArray(4)
        readFully(prefix)
        val length = ByteBuffer.wrap(prefix).int
        check(length in 1..MAX_MESSAGE_BYTES && readMessages < MAX_MESSAGES && length <= MAX_TOTAL_BYTES - readBytes) { "pairing_failed" }
        readMessages++; readBytes += length
        val body = ByteArray(length)
        readFully(body)
        val text = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(body)).toString()
        return WifiFlatJson.read(text, names, numeric, optional)
    }

    fun write(json: String) {
        val body = json.toByteArray(Charsets.UTF_8)
        check(body.size in 1..MAX_MESSAGE_BYTES && writtenMessages < MAX_MESSAGES && body.size <= MAX_TOTAL_BYTES - writtenBytes) { "pairing_failed" }
        writtenMessages++; writtenBytes += body.size
        output.write(ByteBuffer.allocate(4).putInt(body.size).array())
        output.write(body)
        output.flush()
    }

    private fun readFully(target: ByteArray) {
        var offset = 0
        while (offset < target.size) {
            val count = input.read(target, offset, target.size - offset)
            if (count < 0) throw EOFException("pairing stream closed")
            offset += count
        }
    }

    companion object {
        const val MAX_MESSAGE_BYTES = 16 * 1024
        const val MAX_TOTAL_BYTES = 32 * 1024
        const val MAX_MESSAGES = 4
    }
}