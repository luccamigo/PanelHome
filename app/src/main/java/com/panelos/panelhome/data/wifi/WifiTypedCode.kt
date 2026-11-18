package com.panelos.panelhome.data.wifi

internal class WifiTypedCodeFields(
    val hostId: ByteArray,
    val pairId: ByteArray,
    val ip: String,
    val pairPort: Int,
    val sessionPort: Int,
    val pin: String,
)

internal class InvalidPairingCode(cause: Exception? = null) : IllegalArgumentException("Invalid pairing code", cause)

/**
 * The typed-code fallback for a QR the camera cannot read (the PIN mode of seguranca-wifi.md 5.2). Mirror of the
 * PanelHost codec; both read protocol-fixtures/wifi/typed-code-v1.json.
 *
 *   bytes: version(1)=1 | hostId(32) | pairId(16) | ipv4(4) | pairPort(2) | sessionPort(2) | pin(3) | crc16(2)
 *   text : RFC 4648 Base32, no padding, 100 characters; spaces, dashes and case are ignored when typing.
 *
 * The CRC only catches typing mistakes. The host id inside is the full 256-bit pin of the PC; the PIN is worth five
 * attempts on the host and then the invitation is gone.
 */
internal object WifiTypedCode {
    const val PAYLOAD_BYTES = 62
    const val TEXT_LENGTH = 100
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"

    fun decode(text: String): WifiTypedCodeFields = try {
        decodeCore(text)
    } catch (error: Exception) {
        throw InvalidPairingCode(error)
    }

    private fun decodeCore(text: String): WifiTypedCodeFields {
        val clean = text.filter { it !in "- \t\r\n" }.uppercase()
        require(clean.length == TEXT_LENGTH && clean.all { it in ALPHABET })
        val bytes = fromBase32(clean)
        require(bytes.size == PAYLOAD_BYTES && bytes[0] == 1.toByte())
        require(crc16(bytes, 0, 60) == (u16(bytes, 60)))
        val octets = (49 until 53).map { bytes[it].toInt() and 255 }
        require(octets[0] == 10 || octets[0] == 172 && octets[1] in 16..31 || octets[0] == 192 && octets[1] == 168)
        val pairPort = u16(bytes, 53); val sessionPort = u16(bytes, 55)
        require(pairPort != sessionPort && validPort(pairPort) && validPort(sessionPort))
        val pin = ((bytes[57].toInt() and 255) shl 16) or ((bytes[58].toInt() and 255) shl 8) or (bytes[59].toInt() and 255)
        require(pin in 0..999_999)
        return WifiTypedCodeFields(
            hostId = bytes.copyOfRange(1, 33), pairId = bytes.copyOfRange(33, 49), ip = octets.joinToString("."),
            pairPort = pairPort, sessionPort = sessionPort, pin = pin.toString().padStart(6, '0'),
        )
    }

    /** Used by tests and by the fixture check; the app itself only decodes. */
    fun encode(fields: WifiTypedCodeFields): String {
        val octets = fields.ip.split('.').map { it.toInt() }
        require(octets.size == 4 && fields.pin.length == 6 && fields.pin.all { it in '0'..'9' })
        val bytes = ByteArray(PAYLOAD_BYTES)
        bytes[0] = 1
        fields.hostId.copyInto(bytes, 1); fields.pairId.copyInto(bytes, 33)
        octets.forEachIndexed { index, value -> bytes[49 + index] = value.toByte() }
        bytes[53] = (fields.pairPort shr 8).toByte(); bytes[54] = fields.pairPort.toByte()
        bytes[55] = (fields.sessionPort shr 8).toByte(); bytes[56] = fields.sessionPort.toByte()
        val pin = fields.pin.toInt()
        bytes[57] = (pin shr 16).toByte(); bytes[58] = (pin shr 8).toByte(); bytes[59] = pin.toByte()
        val crc = crc16(bytes, 0, 60)
        bytes[60] = (crc shr 8).toByte(); bytes[61] = crc.toByte()
        return toBase32(bytes).chunked(5).joinToString("-")
    }

    private fun validPort(port: Int) = port in 1024..65535 && port !in 8765..8767
    private fun u16(bytes: ByteArray, offset: Int) = ((bytes[offset].toInt() and 255) shl 8) or (bytes[offset + 1].toInt() and 255)

    /** CRC-16/CCITT-FALSE: polynomial 0x1021, initial 0xFFFF, no reflection, no final xor. */
    fun crc16(data: ByteArray, offset: Int = 0, length: Int = data.size): Int {
        var crc = 0xFFFF
        for (index in offset until offset + length) {
            crc = crc xor ((data[index].toInt() and 255) shl 8)
            repeat(8) { crc = if (crc and 0x8000 != 0) ((crc shl 1) xor 0x1021) and 0xFFFF else (crc shl 1) and 0xFFFF }
        }
        return crc
    }

    private fun toBase32(data: ByteArray): String {
        val text = StringBuilder()
        var buffer = 0; var bits = 0
        for (value in data) {
            buffer = ((buffer shl 8) or (value.toInt() and 255)) and 0xFFFFF; bits += 8
            while (bits >= 5) { text.append(ALPHABET[(buffer shr (bits - 5)) and 31]); bits -= 5 }
        }
        if (bits > 0) text.append(ALPHABET[(buffer shl (5 - bits)) and 31])
        return text.toString()
    }

    private fun fromBase32(text: String): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        var buffer = 0; var bits = 0
        for (c in text) {
            buffer = ((buffer shl 5) or ALPHABET.indexOf(c)) and 0xFFFFF; bits += 5
            if (bits >= 8) { out.write((buffer shr (bits - 8)) and 255); bits -= 8 }
        }
        // Canonical: the unused trailing bits are zero, so one payload has exactly one text.
        require(buffer and ((1 shl bits) - 1) == 0)
        return out.toByteArray()
    }
}