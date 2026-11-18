package com.panelos.panelhome.data.wifi

import android.os.Build
import androidx.annotation.RequiresApi
import java.net.Socket
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.*
import java.util.Date
import org.conscrypt.Conscrypt

internal object WifiTls {
    const val SESSION_ALPN = "panelos-session/1"
    const val PAIR_ALPN = "panelos-pair/1"
    private val provider by lazy { Conscrypt.newProvider() }
    fun requirePlatform(api: Int) { require(api >= 28) { "WiFi requires Android API 28+" } }

    fun validateRoot(root: X509Certificate, hostId: ByteArray, now: Date = Date()) {
        require(hostId.size == 32 && root.encoded.size <= 512)
        require(java.security.MessageDigest.isEqual(WifiCrypto.hash(root.publicKey.encoded), hostId))
        WifiCrypto.publicKey(root.publicKey.encoded); root.checkValidity(now); root.verify(root.publicKey)
        require(root.subjectX500Principal == root.issuerX500Principal && root.basicConstraints == 0)
        require(root.keyUsage?.getOrNull(5) == true && root.keyUsage.withIndex().all { !it.value || it.index == 5 })
        require(root.sigAlgOID == "1.2.840.10045.4.3.2" && root.notAfter.time - root.notBefore.time <= (366L * 5 + 1) * 86400000)
        require(root.criticalExtensionOIDs.orEmpty().all { it == "2.5.29.19" || it == "2.5.29.15" })
        require("2.5.29.19" in root.criticalExtensionOIDs.orEmpty())
    }
    fun validateServer(chain: Array<X509Certificate>, hostId: ByteArray, root: X509Certificate, now: Date = Date()) {
        try {
            require(chain.size in 1..2 && (chain.size == 1 || chain[1].encoded.contentEquals(root.encoded)))
            val leaf = chain[0]
            validateLeaf(leaf, root, hostId, client = false, now)
            val name = serverName(hostId)
            require(leaf.subjectAlternativeNames?.any { it[0] == 2 && it[1] == name } == true)
        } catch (error: Exception) { throw CertificateException("PanelOS server identity rejected", error) }
    }
    fun validateClientLeaf(leaf: X509Certificate, root: X509Certificate, hostId: ByteArray,
        expectedSpki: ByteArray, now: Date = Date()) {
        try {
            validateLeaf(leaf, root, hostId, client = true, now)
            require(java.security.MessageDigest.isEqual(leaf.publicKey.encoded, expectedSpki))
        } catch (error: Exception) { throw CertificateException("PanelOS client identity rejected", error) }
    }
    private fun validateLeaf(leaf: X509Certificate, root: X509Certificate, hostId: ByteArray, client: Boolean, now: Date) {
        validateRoot(root, hostId, now)
        WifiCrypto.publicKey(leaf.publicKey.encoded); leaf.checkValidity(now); leaf.verify(root.publicKey)
        require(leaf.getExtensionValue("2.5.29.19") != null && leaf.basicConstraints == -1)
        require(leaf.keyUsage?.getOrNull(0) == true && leaf.keyUsage.withIndex().all { !it.value || it.index == 0 })
        require(leaf.extendedKeyUsage == listOf(if (client) "1.3.6.1.5.5.7.3.2" else "1.3.6.1.5.5.7.3.1"))
        require(leaf.sigAlgOID == "1.2.840.10045.4.3.2" && leaf.notAfter.time - leaf.notBefore.time <= 367L * 86400000)
        val known = setOf("2.5.29.19", "2.5.29.15", "2.5.29.37", "2.5.29.17", "2.5.29.14", "2.5.29.35")
        require(leaf.criticalExtensionOIDs.orEmpty().all { it in known })
    }
    fun serverName(hostId: ByteArray) = "panelos-${hostId.take(16).joinToString("") { "%02x".format(it.toInt() and 255) }}.local"

    // A fresh SSLContext on every connection prevents client ticket reuse;
    // server policy also disables resumption. No system CA store is consulted.
    @RequiresApi(28)
    fun context(hostId: ByteArray, root: X509Certificate, identity: KeyStore.PrivateKeyEntry? = null,
        apiLevel: Int = Build.VERSION.SDK_INT): SSLContext {
        requirePlatform(apiLevel)
        validateRoot(root, hostId)
        val trust = object : X509ExtendedTrustManager() {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String) = throw CertificateException("Client role only")
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, s: Socket) = checkClientTrusted(c, a)
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = checkClientTrusted(c, a)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String) = validateServer(c, hostId, root)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, s: Socket) = validateServer(c, hostId, root)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = validateServer(c, hostId, root)
        }
        val managers = identity?.let { entry -> arrayOf<KeyManager>(object : X509ExtendedKeyManager() {
            override fun getPrivateKey(alias: String): PrivateKey? = if (alias == "pair") entry.privateKey else null
            override fun getCertificateChain(alias: String): Array<X509Certificate>? = if (alias == "pair")
                entry.certificateChain.map { it as X509Certificate }.toTypedArray() else null
            override fun getClientAliases(t: String?, i: Array<out Principal>?): Array<String>? = if (t == "EC") arrayOf("pair") else null
            override fun chooseClientAlias(t: Array<out String>?, i: Array<out Principal>?, s: Socket?): String? =
                if (t?.contains("EC") == true) "pair" else null
            override fun chooseEngineClientAlias(t: Array<out String>?, i: Array<out Principal>?, e: SSLEngine?): String? =
                if (t?.contains("EC") == true) "pair" else null
            override fun getServerAliases(t: String?, i: Array<out Principal>?): Array<String>? = null
            override fun chooseServerAlias(t: String?, i: Array<out Principal>?, s: Socket?): String? = null
        }) }
        return SSLContext.getInstance("TLSv1.3", provider).apply { init(managers, arrayOf<TrustManager>(trust), SecureRandom()) }
    }
    /**
     * PIN-mode pairing only. The user typed the host id but there is no QR carrying the root, so the server certificate
     * cannot be validated during the handshake. This context lets the handshake finish with whatever certificate the PC
     * shows and hands that chain to [onServerChain]; the caller MUST validate it against the root the host then sends
     * (which must hash to the typed host id) before it writes one byte of the PairRequest, and must close on failure.
     * No secret has left the device at that point. It never carries a client identity and is never used for a session.
     */
    @RequiresApi(28)
    @android.annotation.SuppressLint("CustomX509TrustManager") // deliberate and bounded: see the contract above
    fun deferredPairingContext(onServerChain: (Array<X509Certificate>) -> Unit, apiLevel: Int = Build.VERSION.SDK_INT): SSLContext {
        requirePlatform(apiLevel)
        val trust = object : X509ExtendedTrustManager() {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String) = throw CertificateException("Client role only")
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, s: Socket) = checkClientTrusted(c, a)
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = checkClientTrusted(c, a)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String) = onServerChain(c)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, s: Socket) = onServerChain(c)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = onServerChain(c)
        }
        return SSLContext.getInstance("TLSv1.3", provider).apply { init(null, arrayOf<TrustManager>(trust), SecureRandom()) }
    }
    @RequiresApi(28)
    fun configure(socket: SSLSocket, hostId: ByteArray, pairing: Boolean, apiLevel: Int = Build.VERSION.SDK_INT) {
        requirePlatform(apiLevel)
        socket.enabledProtocols = arrayOf("TLSv1.3")
        require(Conscrypt.isConscrypt(socket))
        // Public Conscrypt APIs also work on API 28, whose JSSE has no public ALPN methods.
        Conscrypt.setApplicationProtocols(socket, arrayOf(if (pairing) PAIR_ALPN else SESSION_ALPN))
        Conscrypt.setHostname(socket, serverName(hostId))
        Conscrypt.setUseSessionTickets(socket, false)
        socket.soTimeout = 5000
    }
    @RequiresApi(28)
    fun checkNegotiated(socket: SSLSocket, pairing: Boolean, apiLevel: Int = Build.VERSION.SDK_INT) {
        requirePlatform(apiLevel)
        require(socket.session.protocol == "TLSv1.3" && Conscrypt.getApplicationProtocol(socket) == if (pairing) PAIR_ALPN else SESSION_ALPN)
    }
}
