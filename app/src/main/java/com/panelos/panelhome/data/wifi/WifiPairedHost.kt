package com.panelos.panelhome.data.wifi


/**
 * The one thing the app remembers about a paired PC: its host id (the pin), its public root, where to reach it, and
 * when. It holds no secret: the private key is in the AndroidKeyStore and this record is also stored encrypted with a
 * Keystore key, in a directory excluded from backup and device transfer.
 */
internal class WifiPairedHost(
    val hostId: ByteArray,
    val rootDer: ByteArray,
    val ip: String,
    val sessionPort: Int,
    val pairedAtMillis: Long,
) {
    /** All 64 hex digits of the host id; the UI shows groups of it, never an abbreviation to compare by. */
    val hostIdHex: String get() = hostId.joinToString("") { "%02x".format(it.toInt() and 255) }

    fun encode(): ByteArray {
        fun b64(bytes: ByteArray) = WifiBase64.encode(bytes)
        return ("{\"hostId\":${WifiFlatJson.quote(b64(hostId))},\"rootCa\":${WifiFlatJson.quote(b64(rootDer))}," +
            "\"ip\":${WifiFlatJson.quote(ip)},\"sessionPort\":$sessionPort,\"pairedAt\":$pairedAtMillis}").toByteArray(Charsets.UTF_8)
    }

    companion object {
        private val names = setOf("hostId", "rootCa", "ip", "sessionPort", "pairedAt")

        fun decode(bytes: ByteArray): WifiPairedHost {
            require(bytes.size in 1..16384)
            val fields = WifiFlatJson.read(String(bytes, Charsets.UTF_8), names, numeric = setOf("sessionPort", "pairedAt"))
            val hostId = WifiBase64.decode(fields.getValue("hostId"))
            val root = WifiBase64.decode(fields.getValue("rootCa"))
            require(hostId.size == 32 && root.size in 1..512)
            val ip = fields.getValue("ip")
            val octets = ip.split('.').map { it.toInt() }
            require(octets.size == 4 && octets.all { it in 0..255 } && octets.joinToString(".") == ip)
            require(octets[0] == 10 || octets[0] == 172 && octets[1] in 16..31 || octets[0] == 192 && octets[1] == 168)
            val port = fields.getValue("sessionPort").toInt()
            require(port in 1024..65535 && port !in 8765..8767)
            return WifiPairedHost(hostId, root, ip, port, fields.getValue("pairedAt").toLong())
        }
    }
}