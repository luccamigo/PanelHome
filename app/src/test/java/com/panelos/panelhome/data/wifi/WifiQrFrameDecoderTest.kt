package com.panelos.panelhome.data.wifi

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * The QR in the fixture was produced by the Control Center (QRCoder, ECC M). Decoding it here with ZXing, the decoder
 * the camera path uses, is the interoperability check between the two sides; the camera itself is not tested here.
 */
class WifiQrFrameDecoderTest {
    private val fixture = WifiFixtures.json("qr-v1.json")
    private val text = fixture.getString("text")
    private val rows = fixture.getJSONArray("rows").let { array -> (0 until array.length()).map { array.getString(it) } }

    /** Dark modules as 0x00 and light as 0xFF on a 4-module quiet zone, like a frame filled by the QR on a monitor. */
    private fun frame(scale: Int, border: Int = 4): Triple<ByteArray, Int, Int> {
        val size = (rows.size + 2 * border) * scale
        val luma = ByteArray(size * size) { 0xFF.toByte() }
        for (y in rows.indices) for (x in rows[y].indices) if (rows[y][x] == '1')
            for (dy in 0 until scale) for (dx in 0 until scale) luma[((y + border) * scale + dy) * size + (x + border) * scale + dx] = 0
        return Triple(luma, size, size)
    }

    private fun rotate(luma: ByteArray, size: Int): ByteArray = ByteArray(size * size) { luma[(size - 1 - it % size) * size + it / size] }

    @Test fun decodesTheDocumentTheControlCenterEncodedAtSeveralScales() {
        for (scale in listOf(3, 4, 6)) {
            val (luma, width, height) = frame(scale)
            assertArrayEquals("scale $scale", text.toByteArray(), WifiQrFrameDecoder.decode(luma, width, height))
        }
    }

    @Test fun decodesRotatedFramesAndFramesWithPaddedRows() {
        val (luma, size, _) = frame(4)
        var turned = luma
        repeat(3) { turned = rotate(turned, size); assertArrayEquals(text.toByteArray(), WifiQrFrameDecoder.decode(turned, size, size)) }
        val stride = size + 24
        val padded = ByteArray(stride * size) { 0x80.toByte() }
        for (y in 0 until size) System.arraycopy(luma, y * size, padded, y * stride, size)
        assertArrayEquals("row stride larger than the width", text.toByteArray(), WifiQrFrameDecoder.decode(padded, size, size, stride))
    }

    @Test fun frameDecodeFeedsTheStrictOfferValidatorSoAHostileQrIsStillRefused() {
        // The synthetic document has no real root, so the offer decoder must refuse it: scanning never implies trust.
        val decoded = WifiQrFrameDecoder.decode(frame(4).first, frame(4).second, frame(4).third)!!
        assertThrows(InvalidPairingQr::class.java) { WifiPairingOffer.decodeSupported(decoded) }
    }

    @Test fun framesWithoutAReadableQrAreSimplyNull() {
        assertNull(WifiQrFrameDecoder.decode(ByteArray(64 * 64) { 0xFF.toByte() }, 64, 64))
        assertNull(WifiQrFrameDecoder.decode(ByteArray(64 * 64) { (it * 31).toByte() }, 64, 64))
        val (luma, size, _) = frame(4)
        assertNull("a quarter of the symbol is not enough", WifiQrFrameDecoder.decode(luma.copyOf(size * size / 4), size, size / 4))
        assertEquals(109, rows.size)
    }

    @Test fun refusesAFrameBufferThatCannotHoldTheDeclaredSize() {
        assertThrows(IllegalArgumentException::class.java) { WifiQrFrameDecoder.decode(ByteArray(10), 64, 64) }
    }
}