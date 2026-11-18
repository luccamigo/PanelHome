package com.panelos.panelhome.data.wifi

import org.conscrypt.Conscrypt
import java.io.Closeable
import java.net.InetAddress
import java.net.ServerSocket
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import javax.net.ssl.KeyManager
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLParameters
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedKeyManager

/**
 * The PC's pairing endpoint as a Kotlin test double over a REAL TLS 1.3 listener on 127.0.0.1 (JSSE, ALPN
 * panelos-pair/1): same wire as WifiPairingSession on the PC. It checks the proof exactly as the PC does, so a client that
 * signs the wrong transcript is refused here too. Test-only; the product never listens.
 */
internal class FakePairingHost(
    private val pki: TestPki.Pki,
    private val pin: String? = null,
    private val behavior: Behavior = Behavior.APPROVE,
    private val approvalDelayMillis: Long = 0,
    private val token: ByteArray = ByteArray(32) { it.toByte() },
    private val pairId: ByteArray = ByteArray(16) { (0x40 + it).toByte() },
) : Closeable {
    enum class Behavior { APPROVE, DENY, WRONG_LEAF_KEY, WRONG_ROOT_IN_RESULT, CLOSE_AFTER_CHALLENGE }

    private val server: SSLServerSocket
    private val running = AtomicBoolean(true)
    val requestSeen = AtomicBoolean(false)
    val requests = CopyOnWriteArrayList<Map<String, String>>()
    @Volatile var proofValid: Boolean? = null
    val port: Int get() = server.localPort
    val expectedToken: ByteArray get() = if (pin != null) WifiCrypto.hash(pin.toByteArray(Charsets.US_ASCII)) else token

    init {
        val serverKeyManager = object : X509ExtendedKeyManager() {
            override fun getServerAliases(t: String?, i: Array<out Principal>?): Array<String> = arrayOf("host")
            override fun chooseServerAlias(t: String?, i: Array<out Principal>?, s: java.net.Socket?): String = "host"
            override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(pki.server)
            override fun getPrivateKey(alias: String?): PrivateKey = pki.serverKey.private
            override fun getClientAliases(t: String?, i: Array<out Principal>?): Array<String>? = null
            override fun chooseClientAlias(t: Array<out String>?, i: Array<out Principal>?, s: java.net.Socket?): String? = null
        }
        val context = SSLContext.getInstance("TLSv1.3", Conscrypt.newProvider()).apply { init(arrayOf<KeyManager>(serverKeyManager), null, SecureRandom()) }
        server = (context.serverSocketFactory.createServerSocket(0, 4, InetAddress.getByName("127.0.0.1")) as SSLServerSocket).apply {
            enabledProtocols = arrayOf("TLSv1.3")
        }
        Thread { loop() }.apply { isDaemon = true; name = "fake-pairing-host" }.start()
    }

    private fun loop() {
        while (running.get()) {
            val socket = try { server.accept() as SSLSocket } catch (_: Exception) { return }
            try { handle(socket) } catch (_: Exception) { } finally { runCatching { socket.close() } }
        }
    }

    private fun b64(bytes: ByteArray) = Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    private fun unb64(text: String) = Base64.getUrlDecoder().decode(text)

    private fun handle(socket: SSLSocket) {
        socket.soTimeout = 10_000
        Conscrypt.setApplicationProtocols(socket, arrayOf(WifiTls.PAIR_ALPN))
        Conscrypt.setUseSessionTickets(socket, false)
        socket.startHandshake()
        val wire = WifiPairingWire(socket.inputStream, socket.outputStream)
        if (pin != null) wire.write("""{"type":"host_root","rootCa":"${b64(pki.root.encoded)}"}""")
        val challenge = ByteArray(32).also { SecureRandom().nextBytes(it) }
        wire.write("""{"type":"challenge","challenge":"${b64(challenge)}"}""")
        if (behavior == Behavior.CLOSE_AFTER_CHALLENGE) return
        val request = wire.read(setOf("type", "pairId", "token", "spki", "label", "signature"))
        requestSeen.set(true); requests.add(request)
        val spki = unb64(request.getValue("spki"))
        val proof = WifiCrypto.message(challenge, pki.hostId, spki, unb64(request.getValue("pairId")), unb64(request.getValue("token")))
        val valid = WifiCrypto.verify(spki, proof, unb64(request.getValue("signature"))) &&
            java.security.MessageDigest.isEqual(unb64(request.getValue("token")), expectedToken) &&
            java.security.MessageDigest.isEqual(unb64(request.getValue("pairId")), pairId)
        proofValid = valid
        if (approvalDelayMillis > 0) Thread.sleep(approvalDelayMillis)
        if (!valid || behavior == Behavior.DENY) { wire.write("""{"type":"pair_result","status":"pairing_failed"}"""); return }
        val leafSpki = if (behavior == Behavior.WRONG_LEAF_KEY) TestPki.run { java.security.KeyPairGenerator.getInstance("EC")
            .apply { initialize(java.security.spec.ECGenParameterSpec("secp256r1")) }.generateKeyPair().public.encoded } else spki
        val leaf = TestPki.clientLeaf(pki, leafSpki)
        val rootInResult = if (behavior == Behavior.WRONG_ROOT_IN_RESULT) TestPki.create().root else pki.root
        wire.write("""{"type":"pair_result","status":"approved","certificate":"${b64(leaf.encoded)}","rootCa":"${b64(rootInResult.encoded)}"}""")
    }

    val offerPairId: ByteArray get() = pairId
    val offerToken: ByteArray get() = token

    override fun close() { running.set(false); runCatching { server.close() } }
}
