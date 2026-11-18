package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.protocol.MediaArtworkMessage
import java.util.Base64
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class MediaArtworkDecoderTest {
    @Test
    fun validPngIsDecoded() {
        val bytes = pngBytes(payloadSize = 16)

        val artwork = MediaArtworkDecoder.decode(message(bytes = bytes))

        requireNotNull(artwork)
        assertEquals("media-1", artwork.mediaId)
        assertEquals("image/png", artwork.mimeType)
        assertArrayEquals(bytes, artwork.bytes)
    }

    @Test
    fun unavailableArtworkIsAbsent() {
        assertNull(
            MediaArtworkDecoder.decode(
                message(bytes = pngBytes(), available = false),
            ),
        )
    }

    @Test
    fun unsupportedMimeTypeIsAbsent() {
        assertNull(
            MediaArtworkDecoder.decode(
                message(bytes = pngBytes(), mimeType = "image/gif"),
            ),
        )
    }

    @Test
    fun malformedBase64IsAbsent() {
        assertNull(
            MediaArtworkDecoder.decode(
                MediaArtworkMessage(
                    mediaId = "media-1",
                    available = true,
                    mimeType = "image/png",
                    dataBase64 = "not valid base64%%%",
                ),
            ),
        )
    }

    @Test
    fun mimeSignatureMismatchIsAbsent() {
        assertNull(
            MediaArtworkDecoder.decode(
                message(bytes = byteArrayOf(1, 2, 3, 4)),
            ),
        )
    }

    @Test
    fun artworkLargerThanOneMebibyteIsAbsent() {
        val bytes = pngBytes(
            payloadSize = MediaArtworkDecoder.MAX_ARTWORK_BYTES,
        )

        assertNull(MediaArtworkDecoder.decode(message(bytes = bytes)))
    }

    private fun message(
        bytes: ByteArray,
        available: Boolean = true,
        mimeType: String = "image/png",
    ) = MediaArtworkMessage(
        mediaId = "media-1",
        available = available,
        mimeType = mimeType,
        dataBase64 = Base64.getEncoder().encodeToString(bytes),
    )

    private fun pngBytes(payloadSize: Int = 0): ByteArray =
        byteArrayOf(
            0x89.toByte(), 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A,
        ) + ByteArray(payloadSize)
}
