package com.panelos.panelhome.data.wifi

import android.os.Build
import androidx.annotation.RequiresApi
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.Base64

/** Backend QR document only; scanning/permissions and LAN admission are separate. */
@RequiresApi(28)
internal class WifiPairingOffer(val hostId: ByteArray, val rootDer: ByteArray, val pairId: ByteArray, val token: ByteArray,
    val ip: String, val pairPort: Int, val sessionPort: Int, val expiresAt: Long) : AutoCloseable {
    override fun close() { token.fill(0) }
    companion object {
        fun decode(data: ByteArray): WifiPairingOffer {
            WifiTls.requirePlatform(Build.VERSION.SDK_INT)
            return decodeSupported(data)
        }
        // Same stateless validator called after the real API gate; JVM tests do
        // not replace Android classes or inject a fake JSON/certificate reader.
        internal fun decodeSupported(data: ByteArray, now: java.util.Date = java.util.Date()): WifiPairingOffer = try { decodeCore(data, now) }
            catch (error: Exception) { throw InvalidPairingQr(error) }
        private fun decodeCore(data: ByteArray, now: java.util.Date): WifiPairingOffer {
            require(data.size in 1..1024)
            val text = Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(data)).toString()
            val fields = WifiQrFields.read(text)
            require(fields["type"] == "PanelOSPair/1" && fields["version"] == "1")
            fun binary(name: String, size: Int): ByteArray {
                val value = fields.getValue(name)
                require(value.length == (size * 8 + 5) / 6 && value.matches(Regex("[A-Za-z0-9_-]+")))
                val bytes = Base64.getUrlDecoder().decode(value)
                require(bytes.size == size && Base64.getUrlEncoder().withoutPadding().encodeToString(bytes) == value)
                return bytes
            }
            val ip = fields.getValue("ip")
            require(ip.matches(Regex("[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+")))
            val octets = ip.split('.').map { it.toInt() }
            require(octets.all { it in 0..255 } && octets.joinToString(".") == ip)
            require(octets[0] == 10 || octets[0] == 172 && octets[1] in 16..31 || octets[0] == 192 && octets[1] == 168)
            val pair = fields.getValue("pairPort").toInt(); val session = fields.getValue("sessionPort").toInt()
            require(pair in 1..65535 && session in 1..65535 && pair != session && pair !in 8765..8767 && session !in 8765..8767)
            val hostId = binary("hostId", 32)
            val encodedRoot = fields.getValue("rootCa")
            require(encodedRoot.length in 1..683 && encodedRoot.matches(Regex("[A-Za-z0-9_-]+")))
            val rootDer = Base64.getUrlDecoder().decode(encodedRoot)
            require(rootDer.size in 1..512 && Base64.getUrlEncoder().withoutPadding().encodeToString(rootDer) == encodedRoot)
            val root = java.security.cert.CertificateFactory.getInstance("X.509")
                .generateCertificate(java.io.ByteArrayInputStream(rootDer)) as java.security.cert.X509Certificate
            require(root.encoded.contentEquals(rootDer)); WifiTls.validateRoot(root, hostId, now)
            return WifiPairingOffer(hostId, rootDer, binary("pairId", 16), binary("token", 32), ip, pair, session,
                fields.getValue("expiresAt").toLong())
        }
    }
}

internal class InvalidPairingQr(cause: Exception) : IllegalArgumentException("Invalid pairing QR", cause)
