package com.panelos.panelhome.ui.wifi

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class WifiCodeInputTest {
    @Test fun separatorsAreDroppedLettersAreUppercasedAndTheCodeIsGroupedByFive() {
        val raw = "abcde-fghij klmno\npqrst"
        val clean = WifiCodeInput.clean(raw)
        assertEquals("ABCDEFGHIJKLMNOPQRST", clean)
        assertEquals("ABCDE FGHIJ KLMNO PQRST", WifiCodeInput.format(clean))
    }

    @Test fun onlyAToZAndTwoToSevenAreValidAndTheWrongOnesAreNamedAtOnce() {
        assertEquals(listOf('0', '1', '8', '9'), WifiCodeInput.invalidCharacters("A0B1C8D9E0"))
        assertTrue(WifiCodeInput.invalidCharacters("AZ27").isEmpty())
    }

    @Test fun parearNeedsExactlyOneHundredValidCharacters() {
        val ok = "A2".repeat(50)
        assertTrue(WifiCodeInput.isComplete(ok))
        assertFalse("37 of 100 is not enough", WifiCodeInput.isComplete(ok.take(37)))
        assertFalse(WifiCodeInput.isComplete(ok.dropLast(1) + "1"))
        assertEquals("never more than 100 are kept", 100, WifiCodeInput.clean("A".repeat(250)).length)
        assertEquals(20, WifiCodeInput.format(ok).split(' ').size)
    }
}