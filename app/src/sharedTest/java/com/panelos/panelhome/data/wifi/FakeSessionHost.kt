package com.panelos.panelhome.data.wifi

import com.panelos.panelhome.data.transport.LogicalChannel
import com.panelos.panelhome.data.transport.PlmxFrame
import com.panelos.panelhome.data.transport.PlmxFrameType
import com.panelos.panelhome.data.transport.PlmxSessionId
import org.conscrypt.Conscrypt
import java.io.Closeable
import java.io.InputStream
import java.net.InetAddress
import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509ExtendedTrustManager

/**
 * The PC's session listener as a Kotlin double over a REAL mutual-TLS 1.3 listener (JSSE, ALPN panelos-session/1): it
 * requires a client certificate that chains to the PanelOS root, then speaks the host side of PLMX v2 (HELLO, expects
 * HELLO_ACK, then reads the app's OPEN). Test-only; used to prove WifiTransport, and to expose a stuck writer with a dump.
 */
internal class FakeSessionHost(private val pki: TestPki.Pki, private val replyToOpen: ByteArray? = null) : Closeable {
    private val server: SSLServerSocket
    private val running = AtomicBoolean(true)
    val handshakes = AtomicInteger()
    val opens = CopyOnWriteArrayList<LogicalChannel>()
    val clientCertificates = CopyOnWriteArrayList<X509Certificate>()
    val port: Int get() = server.localPort

    init {
        val keys = object : X509ExtendedKeyManager() {
            override fun getServerAliases(t: String?, i: Array<out Principal>?): Array<String> = arrayOf("host")
            override fun chooseServerAlias(t: String?, i: Array<out Principal>?, s: Socket?): String = "host"
            override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(pki.server)
            override fun getPrivateKey(alias: String?): PrivateKey = pki.serverKey.private
            override fun getClientAliases(t: String?, i: Array<out Principal>?): Array<String>? = null
            override fun chooseClientAlias(t: Array<out String>?, i: Array<out Principal>?, s: Socket?): String? = null
        }
        val trust = object : X509ExtendedTrustManager() {
            override fun getAcceptedIssuers(): Array<X509Certificate> = arrayOf(pki.root)
            private fun check(chain: Array<X509Certificate>) {
                if (chain.isEmpty()) throw CertificateException("no client certificate")
                chain[0].verify(pki.root.publicKey)
                clientCertificates.add(chain[0])
            }
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String) = check(c)
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, s: Socket) = check(c)
            override fun checkClientTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = check(c)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String) = throw CertificateException("server role only")
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, s: Socket) = checkServerTrusted(c, a)
            override fun checkServerTrusted(c: Array<X509Certificate>, a: String, e: SSLEngine) = checkServerTrusted(c, a)
        }
        val context = SSLContext.getInstance("TLSv1.3", Conscrypt.newProvider()).apply { init(arrayOf(keys), arrayOf<TrustManager>(trust), SecureRandom()) }
        server = (context.serverSocketFactory.createServerSocket(0, 4, InetAddress.getByName("127.0.0.1")) as SSLServerSocket).apply {
            enabledProtocols = arrayOf("TLSv1.3")
            needClientAuth = true
        }
        Thread { loop() }.apply { isDaemon = true; name = "fake-session-host" }.start()
    }

    private fun loop() {
        while (running.get()) {
            val socket = try { server.accept() as SSLSocket } catch (_: Exception) { return }
            Thread { try { handle(socket) } catch (_: Exception) { } }.apply { isDaemon = true }.start()
        }
    }

    private fun readFully(input: InputStream, count: Int): ByteArray {
        val bytes = ByteArray(count); var offset = 0
        while (offset < count) { val n = input.read(bytes, offset, count - offset); if (n < 0) throw java.io.EOFException(); offset += n }
        return bytes
    }

    private fun handle(socket: SSLSocket) {
        socket.soTimeout = 15_000
        Conscrypt.setApplicationProtocols(socket, arrayOf(WifiTls.SESSION_ALPN))
        Conscrypt.setUseSessionTickets(socket, false)
        socket.startHandshake()
        handshakes.incrementAndGet()
        val out = socket.outputStream; val input = socket.inputStream
        val session = PlmxSessionId.create()
        // Host HELLO: capabilities = multiplexing (bit 0) is all the app needs here.
        out.write(PlmxFrame.encodeHeader(LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, PlmxFrame.HELLO_PAYLOAD_SIZE, 1, session.tag) +
            PlmxFrame.encodeHello(1, session))
        out.flush()
        val ackHeader = PlmxFrame.decodeHeader(readFully(input, PlmxFrame.HEADER_SIZE))
        check(ackHeader.type == PlmxFrameType.HELLO_ACK)
        readFully(input, ackHeader.payloadLength)
        // Then everything the app sends is a frame; record its OPENs and optionally answer with application data.
        while (running.get()) {
            val header = PlmxFrame.decodeHeader(readFully(input, PlmxFrame.HEADER_SIZE))
            if (header.payloadLength > 0) readFully(input, header.payloadLength)
            if (header.type == PlmxFrameType.OPEN) {
                opens.add(header.channel)
                replyToOpen?.let {
                    out.write(PlmxFrame.encodeHeader(header.channel, PlmxFrameType.DATA, it.size, 1, session.tag) + it)
                    out.flush()
                }
            }
        }
    }

    override fun close() { running.set(false); runCatching { server.close() } }
}