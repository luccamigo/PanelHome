package com.panelos.panelhome.data.wifi

import java.io.File
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.Date
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class WifiValidationTest {
    private val fixture: JSONObject = run {
        var root: File? = File(System.getProperty("user.dir")!!)
        while (root != null && !File(root, "protocol-fixtures/wifi/certificates-v1.json").exists()) root = root.parentFile
        JSONObject(File(root ?: error("fixture not found"), "protocol-fixtures/wifi/certificates-v1.json").readText())
    }
    private val now = Date(fixture.getLong("asOfUnixMillis"))
    private val hostId = bytes("hostId")
    private fun bytes(name: String) = Base64.getDecoder().decode(fixture.getString(name))
    private fun certificate(name: String) = CertificateFactory.getInstance("X.509")
        .generateCertificate(bytes(name).inputStream()) as X509Certificate
    private fun b64(bytes: ByteArray) = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    private fun qr(root: ByteArray = bytes("root"), ip: String = "192.168.1.2"): String = JSONObject().apply {
        put("type", "PanelOSPair/1"); put("version", 1); put("hostId", b64(hostId)); put("rootCa", b64(root))
        put("ip", ip); put("pairPort", 40001); put("sessionPort", 40002)
        put("pairId", b64(ByteArray(16))); put("token", b64(ByteArray(32))); put("expiresAt", 1780272120L)
    }.toString()
    private fun decode(text: String) = WifiPairingOffer.decodeSupported(text.toByteArray(), now)

    @Test fun realQrValidatorAcceptsThePublicRootAndRejectsSwappedRootAndBadStructure() {
        decode(qr()).use { assertArrayEquals(bytes("root"), it.rootDer); assertArrayEquals(hostId, it.hostId) }
        for (bad in listOf(qr(bytes("rootB")), qr(bytes("rootBadSignature")), qr(bytes("rootNonCritical")),
            qr(ip = "127.0.0.1"), qr(ip = "192.168.001.2"), qr(ip = "8.8.8.8"),
            qr().replaceFirst("{", "{\"version\":1,"), qr().dropLast(1) + ",}",
            qr().replace("40001", "1e2"), qr().replace("40001", "\"40001\""))) {
            val error = assertThrows(InvalidPairingQr::class.java) { decode(bad) }
            assertEquals("Invalid pairing QR", error.message)
        }
        val oversizedRoot = qr(ByteArray(513)); assertTrue(oversizedRoot.toByteArray().size <= 1024)
        assertThrows(InvalidPairingQr::class.java) { decode(oversizedRoot) }
        assertThrows(InvalidPairingQr::class.java) { WifiPairingOffer.decodeSupported(byteArrayOf(0xc3.toByte(), 0x28), now) }
        assertThrows(InvalidPairingQr::class.java) { WifiPairingOffer.decodeSupported(ByteArray(1025), now) }
    }
    @Test fun rootValidationExecutesRealSignaturesCriticalConstraintsAndUsage() {
        WifiTls.validateRoot(certificate("root"), hostId, now)
        for (name in listOf("rootB", "rootBadSignature", "rootNonCritical", "rootWrongUsage", "rootTooLarge"))
            assertThrows("root $name", Exception::class.java) { WifiTls.validateRoot(certificate(name), hostId, now) }
    }
    @Test fun serverValidatorRejectsMitmWithCorrectSanAndOtherLeafViolations() {
        val root = certificate("root")
        WifiTls.validateServer(arrayOf(certificate("server")), hostId, root, now)
        val mitm = certificate("mitmSameSan")
        assertTrue(mitm.subjectAlternativeNames.any { it[0] == 2 && it[1] == WifiTls.serverName(hostId) })
        for (name in listOf("mitmSameSan", "wrongSan", "client", "missingBasicConstraints", "extraKeyUsage",
            "missingKeyUsage", "expired", "future", "serverBadSignature"))
            assertThrows("server $name", CertificateException::class.java) {
                WifiTls.validateServer(arrayOf(certificate(name)), hostId, root, now)
            }
    }
    @Test fun clientLeafValidatorUsedBeforeKeystoreInstallRejectsWrongRoleOrKey() {
        val root = certificate("root"); val spki = bytes("leafSpki")
        WifiTls.validateClientLeaf(certificate("client"), root, hostId, spki, now)
        assertThrows(CertificateException::class.java) { WifiTls.validateClientLeaf(certificate("server"), root, hostId, spki, now) }
        assertThrows(CertificateException::class.java) {
            WifiTls.validateClientLeaf(certificate("client"), root, hostId, certificate("rootB").publicKey.encoded, now)
        }
    }
}
