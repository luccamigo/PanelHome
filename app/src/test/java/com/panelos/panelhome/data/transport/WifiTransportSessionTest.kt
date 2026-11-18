package com.panelos.panelhome.data.transport

import com.panelos.panelhome.data.wifi.FakeSessionHost
import com.panelos.panelhome.data.wifi.SoftwareWifiIdentity
import com.panelos.panelhome.data.wifi.TestPki
import com.panelos.panelhome.data.wifi.WifiPairedHost
import java.net.InetSocketAddress
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** WifiTransport over a real mutual-TLS 1.3 listener (JSSE) that speaks the host side of PLMX v2. */
class WifiTransportSessionTest {
    private val pki = TestPki.create()
    private val identity = SoftwareWifiIdentity()
    private val hosts = mutableListOf<FakeSessionHost>()

    @After fun stop() { hosts.forEach { it.close() } }

    private fun pairedHost(): WifiPairedHost {
        val spki = identity.create(pki.hostId)
        identity.installCertificate(pki.hostId, TestPki.clientLeaf(pki, spki), pki.root)
        val host = WifiPairedHost(pki.hostId, pki.root.encoded, "192.168.15.4", 40002, 0L)
        identity.writeMetadata(pki.hostId, host.encode())
        return host
    }

    private fun transport(host: FakeSessionHost, paired: WifiPairedHost, listener: WifiTransportListener = NoWifiTransportListener) =
        WifiTransport(paired, TlsWifiSessionOpener(identity, apiLevel = 28, endpoint = { _, _ -> InetSocketAddress("127.0.0.1", host.port) }, threadId = { 1 }), listener)

    private fun fakeHost(reply: ByteArray? = null) = FakeSessionHost(pki, reply).also { hosts.add(it) }

    @Test fun opensAMutuallyAuthenticatedPlmxSessionAndCarriesBothChannelsOnOneConnection() = runBlocking {
        val host = fakeHost(reply = "pong".toByteArray())
        val paired = pairedHost()
        val events = mutableListOf<String>()
        val transport = transport(host, paired, object : WifiTransportListener {
            override fun onConnecting() { events.add("connecting") }
            override fun onConnected() { events.add("connected") }
        })
        try {
            val control = transport.connect(LogicalChannel.CONTROL)
            val video = transport.connect(LogicalChannel.VIDEO)
            val bytes = ByteArray(4); var read = 0
            while (read < 4) read += control.input.read(bytes, read, 4 - read)
            assertArrayEquals("data from the PC reaches the app channel", "pong".toByteArray(), bytes)
            val deadline = System.currentTimeMillis() + 5_000
            while (host.opens.size < 2 && System.currentTimeMillis() < deadline) Thread.sleep(10)
            assertEquals(listOf(LogicalChannel.CONTROL, LogicalChannel.VIDEO), host.opens.toList())
            assertEquals("one TLS session carries both channels", 1, host.handshakes.get())
            assertEquals(1, host.clientCertificates.size)
            assertEquals("the PC saw this tablet's certificate", pki.hostId.size, 32)
            assertEquals(listOf("connecting", "connected"), events)
            assertTrue(video.isClosed.not())
        } finally { transport.close() }
    }

    @Test fun aSessionThatDiedIsReplacedByAFreshOneOnTheNextConnect() = runBlocking {
        val host = fakeHost()
        val transport = transport(host, pairedHost())
        try {
            transport.connect(LogicalChannel.CONTROL)
            transport.javaClass.getDeclaredField("session").apply { isAccessible = true }.get(transport).let { (it as MultiplexedTransportSession).close() }
            transport.connect(LogicalChannel.CONTROL)
            assertEquals("a new TLS session after the old one died", 2, host.handshakes.get())
        } finally { transport.close() }
    }

    @Test fun aPcWhoseRootIsNotThePairedOneIsAnIdentityMismatchAndPausesTheTransport() = runBlocking {
        val impostor = TestPki.create()
        val host = FakeSessionHost(impostor).also { hosts.add(it) }
        val failures = mutableListOf<String>()
        val transport = transport(host, pairedHost(), object : WifiTransportListener {
            override fun onFailed(failure: WifiFailure, paused: Boolean) { failures.add("$failure/$paused") }
        })
        try {
            runCatching { transport.connect(LogicalChannel.CONTROL) }
            assertEquals(listOf("IDENTITY_MISMATCH/true"), failures)
            val before = host.handshakes.get()
            runCatching { transport.connect(LogicalChannel.CONTROL) }
            assertEquals("paused: no new attempt reaches the network", before, host.handshakes.get())
        } finally { transport.close() }
    }
}