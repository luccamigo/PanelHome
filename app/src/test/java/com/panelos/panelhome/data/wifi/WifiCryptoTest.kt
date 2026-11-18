package com.panelos.panelhome.data.wifi

import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WifiCryptoTest {
    @Test fun goldenJavaProofMatchesContractAndRejectsEveryTranscriptMutation() {
        var directory: File? = File(System.getProperty("user.dir")!!)
        while (directory != null && !File(directory, "protocol-fixtures/wifi/proof-v1.json").exists()) directory = directory.parentFile
        val fixture = JSONObject(File(directory ?: error("fixture not found"), "protocol-fixtures/wifi/proof-v1.json").readText())
        fun bytes(name: String): ByteArray = fixture.getString(name).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
        val message = WifiCrypto.message(bytes("challenge"), bytes("hostId"), bytes("spki"), bytes("pairId"), bytes("token"))
        assertArrayEquals(bytes("message"), message)
        assertTrue(WifiCrypto.verify(bytes("spki"), message, bytes("signature")))
        message.indices.forEach { index ->
            val changed = message.clone(); changed[index] = (changed[index].toInt() xor 1).toByte()
            assertFalse("mutation $index", WifiCrypto.verify(bytes("spki"), changed, bytes("signature")))
        }
        assertFalse(WifiCrypto.verify(bytes("spki"), message, bytes("signature") + byteArrayOf(0)))
    }
    @Test fun signaturesRoundTripForWholeByteMessagesAndKeys() {
        val random = java.util.Random(0x504C4D58)
        repeat(32) {
            val generator = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
            val key = generator.generateKeyPair()
            val message = ByteArray(it * 13).apply(random::nextBytes)
            assertTrue(WifiCrypto.verify(key.public.encoded, message, WifiCrypto.sign(key.private, message)))
        }
    }
    @Test fun oldAndroidIsRefusedWithoutChangingCableSupport() {
        assertThrows(IllegalArgumentException::class.java) { WifiTls.requirePlatform(27) }
        WifiTls.requirePlatform(28)
    }
}
