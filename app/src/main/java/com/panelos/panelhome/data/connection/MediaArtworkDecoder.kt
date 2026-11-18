package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.protocol.MediaArtworkMessage
import java.util.Locale
import kotlin.io.encoding.Base64

internal object MediaArtworkDecoder {
    const val MAX_ARTWORK_BYTES = 1_048_576
    private const val MAX_BASE64_CHARACTERS = ((MAX_ARTWORK_BYTES + 2) / 3) * 4

    private val allowedMimeTypes = setOf(
        "image/jpeg",
        "image/png",
        "image/webp",
    )

    fun decode(message: MediaArtworkMessage): MediaArtwork? {
        if (!message.available || message.mediaId.isBlank()) return null
        val mimeType = message.mimeType?.lowercase(Locale.ROOT)
            ?.takeIf(allowedMimeTypes::contains)
            ?: return null
        val encoded = message.dataBase64
            ?.takeIf { it.isNotBlank() && it.length <= MAX_BASE64_CHARACTERS }
            ?: return null
        val bytes = runCatching { Base64.Default.decode(encoded) }.getOrNull()
            ?.takeIf { it.isNotEmpty() && it.size <= MAX_ARTWORK_BYTES }
            ?: return null
        if (!hasExpectedSignature(mimeType, bytes)) return null

        return MediaArtwork(
            mediaId = message.mediaId,
            mimeType = mimeType,
            bytes = bytes,
        )
    }

    private fun hasExpectedSignature(mimeType: String, bytes: ByteArray): Boolean =
        when (mimeType) {
            "image/jpeg" -> bytes.size >= 3 &&
                bytes[0] == 0xFF.toByte() &&
                bytes[1] == 0xD8.toByte() &&
                bytes[2] == 0xFF.toByte()

            "image/png" -> bytes.startsWith(
                byteArrayOf(
                    0x89.toByte(), 0x50, 0x4E, 0x47,
                    0x0D, 0x0A, 0x1A, 0x0A,
                ),
            )

            "image/webp" -> bytes.size >= 12 &&
                bytes.copyOfRange(0, 4).contentEquals("RIFF".encodeToByteArray()) &&
                bytes.copyOfRange(8, 12).contentEquals("WEBP".encodeToByteArray())

            else -> false
        }

    private fun ByteArray.startsWith(prefix: ByteArray): Boolean =
        size >= prefix.size && prefix.indices.all { index -> this[index] == prefix[index] }
}
