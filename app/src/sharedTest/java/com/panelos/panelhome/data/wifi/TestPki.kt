package com.panelos.panelhome.data.wifi

import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.text.SimpleDateFormat
import java.util.Date
import java.util.TimeZone

/**
 * A throw-away PanelOS PKI built at test time, byte for byte what seguranca-wifi.md asks of the PC (P-256, ECDSA SHA-256,
 * root with critical BasicConstraints cA pathLen 0 and keyCertSign, server and client leaves with digitalSignature, one
 * EKU, a SAN for the server). Generating it per run keeps every test valid whatever the date, and keeps any private key
 * out of the repository.
 */
internal object TestPki {
    class Pki(val root: X509Certificate, val rootKey: KeyPair, val hostId: ByteArray, val serverKey: KeyPair, val server: X509Certificate)

    private object Der {
        fun length(n: Int): ByteArray = when {
            n < 128 -> byteArrayOf(n.toByte())
            n < 256 -> byteArrayOf(0x81.toByte(), n.toByte())
            else -> byteArrayOf(0x82.toByte(), (n shr 8).toByte(), n.toByte())
        }
        fun tlv(tag: Int, content: ByteArray) = byteArrayOf(tag.toByte()) + length(content.size) + content
        fun seq(vararg parts: ByteArray) = tlv(0x30, parts.fold(ByteArray(0)) { a, b -> a + b })
        fun set(vararg parts: ByteArray) = tlv(0x31, parts.fold(ByteArray(0)) { a, b -> a + b })
        fun integer(bytes: ByteArray) = tlv(0x02, bytes)
        val bool = tlv(0x01, byteArrayOf(0xFF.toByte()))
        fun octets(bytes: ByteArray) = tlv(0x04, bytes)
        fun bitString(unusedBits: Int, bytes: ByteArray) = tlv(0x03, byteArrayOf(unusedBits.toByte()) + bytes)
        fun utf8(text: String) = tlv(0x0C, text.toByteArray())
        fun utc(date: Date): ByteArray {
            val format = SimpleDateFormat("yyMMddHHmmss'Z'").apply { timeZone = TimeZone.getTimeZone("UTC") }
            return tlv(0x17, format.format(date).toByteArray())
        }
        fun oid(dotted: String): ByteArray {
            val arcs = dotted.split('.').map { it.toLong() }
            val body = java.io.ByteArrayOutputStream()
            body.write((arcs[0] * 40 + arcs[1]).toInt())
            for (arc in arcs.drop(2)) {
                val groups = ArrayList<Int>()
                var value = arc
                do { groups.add(0, (value and 0x7F).toInt()); value = value shr 7 } while (value > 0)
                groups.forEachIndexed { index, group -> body.write(if (index < groups.size - 1) group or 0x80 else group) }
            }
            return tlv(0x06, body.toByteArray())
        }
    }

    private const val ECDSA_SHA256 = "1.2.840.10045.4.3.2"
    private fun name(common: String) = Der.seq(Der.set(Der.seq(Der.oid("2.5.4.3"), Der.utf8(common))))
    private fun extension(oid: String, critical: Boolean, value: ByteArray) =
        if (critical) Der.seq(Der.oid(oid), Der.bool, Der.octets(value)) else Der.seq(Der.oid(oid), Der.octets(value))

    private fun certificate(subject: String, issuer: String, spki: ByteArray, signer: PrivateKey, notBefore: Date, notAfter: Date,
        extensions: List<ByteArray>): X509Certificate {
        val serial = ByteArray(16).also { SecureRandom().nextBytes(it); it[0] = (it[0].toInt() and 0x7F or 0x01).toByte() }
        val algorithm = Der.seq(Der.oid(ECDSA_SHA256))
        val tbs = Der.seq(
            Der.tlv(0xA0, Der.integer(byteArrayOf(2))), Der.integer(serial), algorithm, name(issuer),
            Der.seq(Der.utc(notBefore), Der.utc(notAfter)), name(subject), spki,
            Der.tlv(0xA3, Der.seq(*extensions.toTypedArray())),
        )
        val signature = Signature.getInstance("SHA256withECDSA").run { initSign(signer); update(tbs); sign() }
        val der = Der.seq(tbs, algorithm, Der.bitString(0, signature))
        return CertificateFactory.getInstance("X.509").generateCertificate(der.inputStream()) as X509Certificate
    }

    private fun newKey(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    private const val ROOT_NAME = "PanelOS private WiFi root"

    fun create(now: Date = Date()): Pki {
        val rootKey = newKey()
        val start = Date(now.time - 60_000)
        val root = certificate(ROOT_NAME, ROOT_NAME, rootKey.public.encoded, rootKey.private, start, Date(now.time + 5 * 365L * 86_400_000),
            listOf(
                extension("2.5.29.19", true, Der.seq(Der.bool, Der.integer(byteArrayOf(0)))),
                extension("2.5.29.15", true, Der.bitString(2, byteArrayOf(0x04))),
            ))
        val hostId = WifiCrypto.hash(root.publicKey.encoded)
        val serverKey = newKey()
        val server = leaf(rootKey, serverKey.public.encoded, "PanelOS host", server = true, hostId = hostId, now = now)
        return Pki(root, rootKey, hostId, serverKey, server)
    }

    fun clientLeaf(pki: Pki, spki: ByteArray, now: Date = Date()): X509Certificate =
        leaf(pki.rootKey, spki, "PanelOS paired device", server = false, hostId = pki.hostId, now = now)

    private fun leaf(rootKey: KeyPair, spki: ByteArray, common: String, server: Boolean, hostId: ByteArray, now: Date): X509Certificate {
        val eku = if (server) "1.3.6.1.5.5.7.3.1" else "1.3.6.1.5.5.7.3.2"
        val extensions = mutableListOf(
            extension("2.5.29.19", true, Der.seq()),
            extension("2.5.29.15", true, Der.bitString(7, byteArrayOf(0x80.toByte()))),
            extension("2.5.29.37", false, Der.seq(Der.oid(eku))),
        )
        if (server) extensions.add(extension("2.5.29.17", false, Der.seq(Der.tlv(0x82, WifiTls.serverName(hostId).toByteArray()))))
        return certificate(common, ROOT_NAME, spki, rootKey.private, Date(now.time - 60_000), Date(now.time + 365L * 86_400_000), extensions)
    }
}