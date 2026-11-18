package com.panelos.panelhome.data.transport

import com.panelos.panelhome.data.wifi.SoftwareWifiIdentity
import com.panelos.panelhome.data.wifi.WifiPairedHost
import java.io.EOFException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.security.cert.CertificateException
import javax.net.ssl.SSLHandshakeException
import javax.net.ssl.SSLException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class WifiTransportTest {
    private val host = WifiPairedHost(ByteArray(32), ByteArray(8), "192.168.15.4", 40002, 0L)
    private val events = mutableListOf<String>()
    private val listener = object : WifiTransportListener {
        override fun onConnecting() { events.add("connecting") }
        override fun onConnected() { events.add("connected") }
        override fun onFailed(failure: WifiFailure, paused: Boolean) { events.add("failed:$failure:$paused") }
        override fun onSessionLost() { events.add("lost") }
    }
    private var opens = 0
    private val queue = ArrayDeque<WifiFailure>()
    private fun transport() = WifiTransport(host, { _, _, _ ->
        opens++
        throw WifiConnectException(queue.removeFirst())
    }, listener)

    private fun attempt(transport: WifiTransport) =
        assertThrows(TransportDisconnectedException::class.java) { runBlocking { transport.connect(LogicalChannel.CONTROL) } }

    @Test fun transientFailuresKeepRetryingAndNeverPause() {
        val t = transport(); queue.addAll(List(5) { WifiFailure.UNREACHABLE })
        repeat(5) { attempt(t) }
        assertEquals(5, opens); assertTrue(events.none { it.endsWith(":true") })
    }

    @Test fun aWrongPcIdentityPausesAtOnceUntilTheUserResumes() {
        val t = transport(); queue.addAll(listOf(WifiFailure.IDENTITY_MISMATCH, WifiFailure.UNREACHABLE))
        attempt(t); assertEquals(1, opens); assertTrue("failed:IDENTITY_MISMATCH:true" in events)
        attempt(t); attempt(t)
        assertEquals("no network attempt while paused", 1, opens)
        t.resume(); attempt(t)
        assertEquals(2, opens)
    }

    @Test fun threeRefusalsInARowPauseButAnUnreachableInBetweenResetsTheCount() {
        val t = transport()
        queue.addAll(listOf(WifiFailure.REFUSED_BY_PC, WifiFailure.REFUSED_BY_PC, WifiFailure.UNREACHABLE, WifiFailure.REFUSED_BY_PC, WifiFailure.REFUSED_BY_PC, WifiFailure.REFUSED_BY_PC))
        repeat(5) { attempt(t) }
        assertEquals(5, opens); assertTrue("not paused yet", events.none { it.endsWith(":true") })
        attempt(t)
        assertEquals(6, opens); assertEquals("failed:REFUSED_BY_PC:true", events.last { it.startsWith("failed") })
        attempt(t); assertEquals("paused: no 7th attempt", 6, opens)
    }

    @Test fun permissionAndPlatformFailuresArePausesToo() {
        for (failure in listOf(WifiFailure.PERMISSION_DENIED, WifiFailure.UNSUPPORTED_PLATFORM, WifiFailure.NOT_PAIRED)) {
            events.clear(); opens = 0
            val t = transport(); queue.add(failure)
            attempt(t); attempt(t)
            assertEquals("$failure", 1, opens); assertTrue("failed:$failure:true" in events)
        }
    }

    @Test fun onlyControlAndVideoAreCarriedAndAClosedTransportRefuses() {
        val t = transport()
        for (channel in listOf(LogicalChannel.ASSETS, LogicalChannel.AUDIO, LogicalChannel.TRANSPORT))
            assertThrows(IllegalArgumentException::class.java) { runBlocking { t.connect(channel) } }
        t.close()
        attempt(t); assertEquals(0, opens)
    }

    @Test fun failuresAreClassifiedByWhereTheyHappened() {
        fun classify(error: Throwable, phase: WifiConnectPhase) = TlsWifiSessionOpener.classify(error, phase)
        assertEquals(WifiFailure.PERMISSION_DENIED, classify(SecurityException("ACCESS_LOCAL_NETWORK"), WifiConnectPhase.TCP))
        assertEquals(WifiFailure.UNREACHABLE, classify(ConnectException(), WifiConnectPhase.TCP))
        assertEquals(WifiFailure.UNREACHABLE, classify(SocketTimeoutException(), WifiConnectPhase.TCP))
        assertEquals(WifiFailure.TIMEOUT, classify(SocketTimeoutException(), WifiConnectPhase.PLMX))
        assertEquals(WifiFailure.IDENTITY_MISMATCH, classify(SSLHandshakeException("x").initCause(CertificateException("PanelOS server identity rejected")), WifiConnectPhase.TLS))
        assertEquals(WifiFailure.REFUSED_BY_PC, classify(EOFException(), WifiConnectPhase.PLMX))
        assertEquals(WifiFailure.REFUSED_BY_PC, classify(SSLException("Received fatal alert: certificate_required"), WifiConnectPhase.PLMX))
        assertEquals(WifiFailure.REFUSED_BY_PC, classify(SSLException("handshake_failure"), WifiConnectPhase.TLS))
        assertEquals(WifiFailure.PROTOCOL, classify(IllegalStateException("odd"), WifiConnectPhase.PLMX))
    }

    @Test fun belowApi28TheRealOpenerFailsAsUnsupportedWithoutTouchingTheNetwork() {
        val opener = TlsWifiSessionOpener(SoftwareWifiIdentity(), apiLevel = 27)
        val error = assertThrows(WifiConnectException::class.java) { opener.open(host, TransportMetrics()) { } }
        assertEquals(WifiFailure.UNSUPPORTED_PLATFORM, error.failure)
    }
}