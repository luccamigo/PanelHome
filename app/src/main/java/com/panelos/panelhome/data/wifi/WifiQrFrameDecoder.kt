package com.panelos.panelhome.data.wifi

import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.NotFoundException
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.qrcode.QRCodeReader

/**
 * Reads one QR from one camera frame's luminance plane. It is the only thing the analyser does with a frame: the
 * pixels never leave this function and nothing is stored. A frame without a readable QR is simply null.
 */
internal object WifiQrFrameDecoder {
    private val hints = mapOf(
        DecodeHintType.TRY_HARDER to true,
        DecodeHintType.CHARACTER_SET to "UTF-8",
        DecodeHintType.POSSIBLE_FORMATS to listOf(com.google.zxing.BarcodeFormat.QR_CODE),
    )

    /** [rowStride] is the distance between rows in [luma]; [width] x [height] is the visible frame. */
    fun decode(luma: ByteArray, width: Int, height: Int, rowStride: Int = width): ByteArray? {
        require(width > 0 && height > 0 && rowStride >= width && luma.size >= rowStride * (height - 1) + width)
        val reader = QRCodeReader()
        return try {
            val source = PlanarYUVLuminanceSource(luma, rowStride, height, 0, 0, width, height, false)
            val result = reader.decode(BinaryBitmap(HybridBinarizer(source)), hints)
            // Never accept more than the document limit; the offer decoder re-checks every field anyway.
            result.text.toByteArray(Charsets.UTF_8).takeIf { it.size in 1..1024 }
        } catch (_: NotFoundException) {
            null
        } catch (_: com.google.zxing.ChecksumException) {
            null
        } catch (_: com.google.zxing.FormatException) {
            null
        } finally {
            reader.reset()
        }
    }
}