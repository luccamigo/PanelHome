package com.panelos.panelhome.data.wifi

import java.io.ByteArrayOutputStream
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec

/** Exact transcript from seguranca-wifi.md; no JSON/string canonicalization. */
internal object WifiCrypto {
    fun publicKey(spki: ByteArray): ECPublicKey {
        require(spki.size in 1..512)
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki)) as ECPublicKey
        val expected = java.security.AlgorithmParameters.getInstance("EC").apply {
            init(ECGenParameterSpec("secp256r1"))
        }.getParameterSpec(java.security.spec.ECParameterSpec::class.java)
        require(key.params.curve == expected.curve && key.params.generator == expected.generator &&
            key.params.order == expected.order && key.params.cofactor == expected.cofactor)
        require(key.encoded.contentEquals(spki))
        return key
    }
    fun hash(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)
    fun message(challenge: ByteArray, hostId: ByteArray, spki: ByteArray, pairId: ByteArray, token: ByteArray): ByteArray {
        require(challenge.size == 32 && hostId.size == 32 && pairId.size == 16 && token.size == 32)
        publicKey(spki)
        return ByteArrayOutputStream().apply {
            write("PanelOS WiFi Pair v1".toByteArray(Charsets.US_ASCII)); write(0)
            write(challenge); write(hostId); write(hash(spki)); write(pairId); write(hash(token))
        }.toByteArray()
    }
    fun sign(key: PrivateKey, message: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(key); update(message); sign()
    }
    fun verify(spki: ByteArray, message: ByteArray, der: ByteArray): Boolean = try {
        require(der.size in 8..72)
        Signature.getInstance("SHA256withECDSA").run { initVerify(publicKey(spki)); update(message); verify(der) }
    } catch (_: Exception) { false }
}
