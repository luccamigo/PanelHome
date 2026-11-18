package com.panelos.panelhome.integration

import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.connection.TransportPanelConnection
import com.panelos.panelhome.data.transport.TlsWifiSessionOpener
import com.panelos.panelhome.data.transport.WifiTransport
import com.panelos.panelhome.data.wifi.PairingFailureReason
import com.panelos.panelhome.data.wifi.SoftwareWifiIdentity
import com.panelos.panelhome.data.wifi.WifiPairingClient
import com.panelos.panelhome.data.wifi.WifiPairingException
import com.panelos.panelhome.data.wifi.WifiPairingOffer
import com.panelos.panelhome.data.wifi.WifiTypedCode
import java.net.InetSocketAddress
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test

/**
 * Kotlin half of scripts/run-cross-language-wifi-pairing.ps1: the production Android pairing client (Conscrypt 2.9.0 TLS 1.3) against
 * the production PanelHost pairing session (.NET SslStream / Schannel). Two independent implementations of the same wire,
 * talking on loopback. Skipped unless the script provides the host manifest.
 */
class CrossLanguageWifiPairingTest {
    private lateinit var manifest: JSONObject

    @Before fun loadManifest() {
        val path = System.getenv("PANELOS_WIFI_LIVE_MANIFEST")
        assumeTrue("Run scripts/run-cross-language-wifi-pairing.ps1 to start the C# host", !path.isNullOrBlank())
        manifest = JSONObject(java.io.File(requireNotNull(path)).readText())
    }

    private fun client(identity: SoftwareWifiIdentity, port: Int) =
        WifiPairingClient(identity, apiLevel = 28, approvalWaitMillis = 30_000, endpoint = { _, _ -> InetSocketAddress("127.0.0.1", port) })

    @Test fun pairsWithTheQrTheControlCenterShowsAndTheHostIssuesACertificateThisClientAccepts() {
        val identity = SoftwareWifiIdentity()
        val offer = WifiPairingOffer.decodeSupported(manifest.getString("qrText").toByteArray())
        val paired = client(identity, manifest.getInt("qrPort")).pairWithOffer(offer, "Kotlin QR device")
        assertEquals(manifest.getString("qrHostId").lowercase(), paired.hostIdHex)
        assertTrue(identity.exists(offer.hostId))
        // A used token cannot pair a second time: the host closes the invitation as soon as it delivers the credential.
        val second = SoftwareWifiIdentity()
        val offerAgain = WifiPairingOffer.decodeSupported(manifest.getString("qrText").toByteArray())
        assertThrows(WifiPairingException::class.java) { client(second, manifest.getInt("qrPort")).pairWithOffer(offerAgain, "reuse") }
        assertTrue(second.hostIds().isEmpty())
    }

    @Test fun pairsWithOnlyTheTypedCodeAfterCheckingThePcAgainstTheTypedHostId() {
        val identity = SoftwareWifiIdentity()
        val fields = WifiTypedCode.decode(manifest.getString("codeText"))
        val paired = client(identity, manifest.getInt("codePort")).pairWithCode(fields, "Kotlin code device")
        assertEquals(manifest.getString("codeHostId").lowercase(), paired.hostIdHex)
        assertTrue(identity.exists(fields.hostId))
    }

    @Test fun aWrongPinOnTheLiveHostIsRefusedAndLeavesNoKey() {
        // Runs after the two good pairings consumed their invitations or not: either way the outcome is a refusal.
        val identity = SoftwareWifiIdentity()
        val fields = WifiTypedCode.decode(manifest.getString("codeText"))
        val wrong = com.panelos.panelhome.data.wifi.WifiTypedCodeFields(fields.hostId, fields.pairId, fields.ip, fields.pairPort,
            fields.sessionPort, if (fields.pin == "000000") "000001" else "000000")
        val error = assertThrows(WifiPairingException::class.java) { client(identity, manifest.getInt("codePort")).pairWithCode(wrong, "x") }
        assertTrue(error.reason == PairingFailureReason.REFUSED || error.reason == PairingFailureReason.UNREACHABLE)
        assertTrue(identity.hostIds().isEmpty())
    }

    /**
     * The whole Wi-Fi path with the app's real classes: pair by QR, then WifiTransport -> mutual TLS 1.3 -> PLMX v2 with the
     * WiFi limits -> the production TransportPanelConnection, against the host's real session loop. It stays up for 17 s,
     * which is longer than the host's 15 s heartbeat, so the app's own 5 s ping is what keeps it alive.
     */
    @Test fun pairedTabletOpensAMutualTlsSessionShowsTheHostAndOutlivesTheHostHeartbeat() = runBlocking {
        val identity = SoftwareWifiIdentity()
        val offer = WifiPairingOffer.decodeSupported(manifest.getString("liveText").toByteArray())
        val paired = client(identity, manifest.getInt("livePairPort")).pairWithOffer(offer, "Kotlin session device")
        val sessionPort = manifest.getInt("liveSessionPort")
        val failures = java.util.Collections.synchronizedList(mutableListOf<String>())
        val transport = WifiTransport(
            paired,
            TlsWifiSessionOpener(identity, apiLevel = 28, endpoint = { _, _ -> InetSocketAddress("127.0.0.1", sessionPort) }, threadId = { 1 }),
            object : com.panelos.panelhome.data.transport.WifiTransportListener {
                override fun onFailed(failure: com.panelos.panelhome.data.transport.WifiFailure, paused: Boolean) { failures.add("$failure/$paused") }
            },
        )
        val diagnostics = java.util.Collections.synchronizedList(mutableListOf<String>())
        val seen = java.util.Collections.synchronizedList(mutableListOf<String>())
        val connection = TransportPanelConnection(transport = transport, touchDebugLog = { diagnostics.add(it) })
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val events = Channel<PanelConnectionEvent>(Channel.UNLIMITED)
        scope.launch(start = kotlinx.coroutines.CoroutineStart.UNDISPATCHED) { connection.events.collect { seen.add(it::class.java.simpleName); events.send(it) } }
        scope.launch { connection.maintainConnection() }
        try {
            var connected: PanelConnectionEvent.Connected? = null
            var telemetry: String? = null
            try {
                withTimeout(20_000) {
                    while (connected == null || telemetry == null) {
                        when (val event = events.receive()) {
                            is PanelConnectionEvent.Connected -> connected = event
                            is PanelConnectionEvent.TelemetryReceived -> telemetry = event.telemetry.cpu.name
                            else -> Unit
                        }
                    }
                }
            } catch (timeout: kotlinx.coroutines.TimeoutCancellationException) {
                throw AssertionError("no session: events=$seen failures=$failures diagnostics=$diagnostics connected=${connected != null} telemetry=$telemetry")
            }
            assertEquals("PanelOS WiFi bench", connected!!.hostName)
            assertTrue("no Desktop, Stream Deck or volume over the bench Wi-Fi", connected!!.features.isEmpty())
            assertEquals("Synthetic bench; sensors unavailable", telemetry)
            // Stay connected past the host's 15 s heartbeat: only the app's pings can do that.
            val until = System.currentTimeMillis() + 17_000
            while (System.currentTimeMillis() < until) {
                val event = kotlinx.coroutines.withTimeoutOrNull(500) { events.receive() }
                assertTrue("the link dropped: $event", event !is PanelConnectionEvent.Disconnected)
            }
            assertTrue("no transport failure was reported: $failures", failures.isEmpty())
        } finally {
            connection.close(); transport.close(); scope.cancel()
        }
    }}
