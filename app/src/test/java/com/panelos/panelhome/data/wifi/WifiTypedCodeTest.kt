package com.panelos.panelhome.data.wifi

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class WifiTypedCodeTest {
    private val fixture = WifiFixtures.json("typed-code-v1.json")
    private val code = fixture.getString("code")

    @Test fun decodesTheSharedFixtureMadeByThePcToItsFields() {
        val fields = WifiTypedCode.decode(code)
        assertArrayEquals(WifiFixtures.hex(fixture.getString("hostId")), fields.hostId)
        assertArrayEquals(WifiFixtures.hex(fixture.getString("pairId")), fields.pairId)
        assertEquals(fixture.getString("ip"), fields.ip)
        assertEquals(fixture.getInt("pairPort"), fields.pairPort)
        assertEquals(fixture.getInt("sessionPort"), fields.sessionPort)
        assertEquals("a leading zero survives", "004217", fields.pin)
    }

    @Test fun encodesBackToTheExactSameText() {
        assertEquals(code, WifiTypedCode.encode(WifiTypedCode.decode(code)))
    }

    @Test fun typingToleratesCaseSpacesAndDashesButNotAMistake() {
        val plain = WifiTypedCode.decode(code).pin
        assertEquals(plain, WifiTypedCode.decode(code.lowercase()).pin)
        assertEquals(plain, WifiTypedCode.decode(code.replace("-", " ")).pin)
        assertEquals(plain, WifiTypedCode.decode(code.replace("-", "")).pin)
        for (index in code.indices) {
            if (code[index] == '-') continue
            val changed = code.substring(0, index) + (if (code[index] == 'A') 'B' else 'A') + code.substring(index + 1)
            assertThrows("typo at $index", InvalidPairingCode::class.java) { WifiTypedCode.decode(changed) }
        }
    }

    @Test fun refusesWrongShapeAlphabetAndHostileContent() {
        for (bad in listOf("", code.dropLast(1), code + "A", code.replace('A', '0'), "A".repeat(100)))
            assertThrows(bad, InvalidPairingCode::class.java) { WifiTypedCode.decode(bad) }
        fun with(ip: String = "192.168.15.4", pairPort: Int = 40001, sessionPort: Int = 40002): String =
            WifiTypedCode.decode(code).let { WifiTypedCode.encode(WifiTypedCodeFields(it.hostId, it.pairId, ip, pairPort, sessionPort, it.pin)) }
        for (bad in listOf(with(ip = "8.8.8.8"), with(sessionPort = 40001), with(pairPort = 8765), with(sessionPort = 80)))
            assertThrows(bad, InvalidPairingCode::class.java) { WifiTypedCode.decode(bad) }
    }

    @Test fun crcMatchesTheStandardCheckValueTheHostAlsoAsserts() {
        assertEquals(0x29B1, WifiTypedCode.crc16("123456789".toByteArray()))
    }
}