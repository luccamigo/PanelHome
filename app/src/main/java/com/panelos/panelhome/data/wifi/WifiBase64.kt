package com.panelos.panelhome.data.wifi

/**
 * Base64url without padding, canonical only: the encoding of the decoded bytes must equal the input, so one value has
 * one text. It exists because java.util.Base64 needs API 26 while this code must also load (and refuse politely) below
 * API 28, and android.util.Base64 does not run in the host-side tests that cover this wire.
 */
internal object WifiBase64 {
    private const val ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"

    fun encode(bytes: ByteArray): String {
        val text = StringBuilder((bytes.size * 4 + 2) / 3)
        var index = 0
        while (index + 2 < bytes.size) {
            val value = ((bytes[index].toInt() and 255) shl 16) or ((bytes[index + 1].toInt() and 255) shl 8) or (bytes[index + 2].toInt() and 255)
            text.append(ALPHABET[value shr 18 and 63]).append(ALPHABET[value shr 12 and 63]).append(ALPHABET[value shr 6 and 63]).append(ALPHABET[value and 63])
            index += 3
        }
        when (bytes.size - index) {
            1 -> {
                val value = (bytes[index].toInt() and 255) shl 16
                text.append(ALPHABET[value shr 18 and 63]).append(ALPHABET[value shr 12 and 63])
            }
            2 -> {
                val value = ((bytes[index].toInt() and 255) shl 16) or ((bytes[index + 1].toInt() and 255) shl 8)
                text.append(ALPHABET[value shr 18 and 63]).append(ALPHABET[value shr 12 and 63]).append(ALPHABET[value shr 6 and 63])
            }
        }
        return text.toString()
    }

    fun decode(text: String): ByteArray {
        require(text.isNotEmpty() && text.length % 4 != 1 && text.all { it in ALPHABET })
        val out = java.io.ByteArrayOutputStream(text.length * 3 / 4)
        var buffer = 0; var bits = 0
        for (c in text) {
            buffer = ((buffer shl 6) or ALPHABET.indexOf(c)) and 0xFFFFFF; bits += 6
            if (bits >= 8) { out.write((buffer shr (bits - 8)) and 255); bits -= 8 }
        }
        val bytes = out.toByteArray()
        require(encode(bytes) == text) { "Non-canonical Base64url" }
        return bytes
    }
}