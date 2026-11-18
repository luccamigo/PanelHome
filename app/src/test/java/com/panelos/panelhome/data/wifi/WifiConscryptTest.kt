package com.panelos.panelhome.data.wifi

import java.security.Security
import java.security.Provider
import java.security.KeyStore
import java.net.InetAddress
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import org.conscrypt.Conscrypt
import org.junit.Assert.*
import org.junit.Test

class WifiConscryptTest {
    @Test fun explicitProviderIsLocalFreshAndTls13OnlyOnApi28() {
        val before = Security.getProviders().toList()
        val defaultProvider = SSLContext.getDefault().provider
        val pki = TestPki.create()
        val first = WifiTls.context(pki.hostId, pki.root, apiLevel = 28)
        val second = WifiTls.context(pki.hostId, pki.root, apiLevel = 28)
        val deferred = WifiTls.deferredPairingContext({}, apiLevel = 28)
        // Inspect the actual lazy property, even when another test already initialized it.
        val provider = (WifiTls::class.java.getDeclaredField("provider\$delegate").apply {
            isAccessible = true
        }.get(null) as Lazy<*>).value as Provider
        assertSame(provider, first.provider)
        assertFalse(Security.getProviders().any { it.name == provider.name })
        assertTrue(Conscrypt.isConscrypt(first))
        assertTrue(Conscrypt.isConscrypt(deferred))
        assertNotSame(first, second)
        assertEquals(before, Security.getProviders().toList())
        assertSame(defaultProvider, SSLContext.getDefault().provider)
        for (pairing in listOf(false, true)) {
            (first.socketFactory.createSocket() as SSLSocket).use { socket ->
                WifiTls.configure(socket, pki.hostId, pairing, apiLevel = 28)
                assertArrayEquals(arrayOf("TLSv1.3"), socket.enabledProtocols)
                assertEquals(WifiTls.serverName(pki.hostId), Conscrypt.getHostname(socket))
                assertArrayEquals(arrayOf(if (pairing) WifiTls.PAIR_ALPN else WifiTls.SESSION_ALPN), Conscrypt.getApplicationProtocols(socket))
            }
        }
    }

    @Test fun configureDisablesPreviouslyEnabledSessionTickets() {
        WifiTls.deferredPairingContext({}, 28).socketFactory.createSocket().use { raw ->
            val socket = raw as SSLSocket
            Conscrypt.setUseSessionTickets(socket, true)
            val engine = generateSequence(socket.javaClass as Class<*>?) { it.superclass }
                .mapNotNull { type -> type.declaredFields.firstOrNull { it.name == "engine" } }
                .first().apply { isAccessible = true }.get(socket)
            val parameters = engine.javaClass.getDeclaredField("sslParameters").apply { isAccessible = true }.get(engine)
            val tickets = parameters.javaClass.getDeclaredField("useSessionTickets").apply { isAccessible = true }
            assertTrue(tickets.getBoolean(parameters))
            WifiTls.configure(socket, ByteArray(32), false, 28)
            assertFalse("session tickets must be explicitly disabled", tickets.getBoolean(parameters))
        }
    }

    @Test fun configureRejectsAnotherProviderBeforeCallingConscryptApis() {
        val context = SSLContext.getInstance("TLS", "SunJSSE").apply { init(null, null, null) }
        (context.socketFactory.createSocket() as SSLSocket).use { socket ->
            assertFalse(Conscrypt.isConscrypt(socket))
            val error = assertThrows(IllegalArgumentException::class.java) {
                WifiTls.configure(socket, ByteArray(32), false, 28)
            }
            // Without the guard, a later Conscrypt API throws a different exception message.
            assertEquals("Failed requirement.", error.message)
        }
    }

    @Test fun negotiatedAlpnMustMatchEachRoleEvenWithTls13() {
        for (pairing in listOf(false, true)) {
            val wrong = if (pairing) WifiTls.SESSION_ALPN else WifiTls.PAIR_ALPN
            withNegotiatedSocket("TLSv1.3", wrong) { socket ->
                assertEquals("TLSv1.3", socket.session.protocol)
                assertEquals(wrong, Conscrypt.getApplicationProtocol(socket))
                assertThrows(IllegalArgumentException::class.java) { WifiTls.checkNegotiated(socket, pairing, 28) }
                WifiTls.checkNegotiated(socket, !pairing, 28)
            }
        }
    }

    @Test fun negotiatedTls12IsRejectedEvenWithCorrectAlpn() {
        for (pairing in listOf(false, true)) {
            val alpn = if (pairing) WifiTls.PAIR_ALPN else WifiTls.SESSION_ALPN
            withNegotiatedSocket("TLSv1.2", alpn) { socket ->
                assertEquals("TLSv1.2", socket.session.protocol)
                assertEquals(alpn, Conscrypt.getApplicationProtocol(socket))
                assertThrows(IllegalArgumentException::class.java) { WifiTls.checkNegotiated(socket, pairing, 28) }
            }
        }
    }

    private fun withNegotiatedSocket(protocol: String, alpn: String, check: (SSLSocket) -> Unit) {
        val pki = TestPki.create()
        val password = "test-only".toCharArray()
        val store = KeyStore.getInstance("PKCS12").apply {
            load(null, null)
            setKeyEntry("host", pki.serverKey.private, password, arrayOf(pki.server, pki.root))
        }
        val keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(store, password) }
        val serverContext = SSLContext.getInstance("TLS", "SunJSSE").apply { init(keys.keyManagers, null, null) }
        val worker = Executors.newSingleThreadExecutor()
        try {
            (serverContext.serverSocketFactory.createServerSocket(0, 1, InetAddress.getByName("127.0.0.1")) as SSLServerSocket).use { server ->
                server.soTimeout = 5000
                server.enabledProtocols = arrayOf(protocol)
                server.sslParameters = server.sslParameters.apply { applicationProtocols = arrayOf(alpn) }
                val finished = worker.submit {
                    (server.accept() as SSLSocket).use { peer ->
                        peer.soTimeout = 5000
                        peer.startHandshake()
                        assertEquals(7, peer.inputStream.read())
                        peer.outputStream.write(8)
                    }
                }
                (WifiTls.context(pki.hostId, pki.root, apiLevel = 28).socketFactory.createSocket("127.0.0.1", server.localPort) as SSLSocket).use { socket ->
                    WifiTls.configure(socket, pki.hostId, false, 28)
                    // Only this test enables an invalid negotiation, to exercise the post-handshake guard.
                    socket.enabledProtocols = arrayOf(protocol)
                    Conscrypt.setApplicationProtocols(socket, arrayOf(alpn))
                    socket.startHandshake()
                    check(socket)
                    socket.outputStream.write(7)
                    assertEquals(8, socket.inputStream.read())
                }
                finished.get(7, TimeUnit.SECONDS)
            }
        } finally {
            worker.shutdownNow()
            assertTrue(worker.awaitTermination(7, TimeUnit.SECONDS))
        }
    }
}
