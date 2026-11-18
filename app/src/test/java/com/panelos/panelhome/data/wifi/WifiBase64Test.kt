package com.panelos.panelhome.data.wifi

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class WifiBase64Test {
    @Test fun matchesTheStandardLibraryForEveryLengthAndEveryByteValue() {
        val standard = java.util.Base64.getUrlEncoder().withoutPadding()
        for (size in 0..70) {
            val bytes = ByteArray(size) { (it * 37 + size).toByte() }
            assertEquals("size $size", standard.encodeToString(bytes), WifiBase64.encode(bytes))
            if (size > 0) assertArrayEquals(bytes, WifiBase64.decode(WifiBase64.encode(bytes)))
        }
        val all = ByteArray(256) { it.toByte() }
        assertEquals(standard.encodeToString(all), WifiBase64.encode(all))
        assertArrayEquals(all, WifiBase64.decode(standard.encodeToString(all)))
    }

    @Test fun refusesPaddingForeignAlphabetNonCanonicalTailsAndEmpty() {
        for (bad in listOf("", "AA==", "A", "AAAAA", "ab+/", "ab c", "AB", "AAB", "-_-_=", "é"))
            assertThrows(bad, IllegalArgumentException::class.java) { WifiBase64.decode(bad) }
        // "AB" would decode to the byte 0x00 with leftover bits set, so the canonical text is "AA".
        assertArrayEquals(byteArrayOf(0), WifiBase64.decode("AA"))
    }
}