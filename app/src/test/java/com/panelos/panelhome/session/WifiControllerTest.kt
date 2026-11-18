package com.panelos.panelhome.session

import com.panelos.panelhome.data.transport.WifiFailure
import com.panelos.panelhome.data.wifi.PairingFailureReason
import com.panelos.panelhome.data.wifi.PairingProgress
import com.panelos.panelhome.data.wifi.PairingStage
import com.panelos.panelhome.data.wifi.SoftwareWifiIdentity
import com.panelos.panelhome.data.wifi.TestPki
import com.panelos.panelhome.data.wifi.WifiFixtures
import com.panelos.panelhome.data.wifi.WifiPairedHost
import com.panelos.panelhome.data.wifi.WifiPairingException
import com.panelos.panelhome.data.wifi.WifiPairingOffer
import com.panelos.panelhome.data.wifi.WifiTypedCodeFields
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WifiControllerTest {
    private val pki = TestPki.create()
    private val identity = SoftwareWifiIdentity()
    private val mode = object : WifiModeStore { override var enabled = false }
    private val host = object : WifiSessionHost {
        val starts = mutableListOf<WifiPairedHost>(); var stops = 0; var cableRestores = 0
        override fun startWifi(host: WifiPairedHost) { starts.add(host) }
        override fun stopWifi(restoreCable: Boolean) { stops++; if (restoreCable) cableRestores++ }
    }

    private class FakePairer(private val identity: SoftwareWifiIdentity, private val pki: TestPki.Pki) : WifiPairer {
        var failure: WifiPairingException? = null
        var gate: CountDownLatch? = null
        var offers = 0; var codes = 0; var cancels = 0
        var lastCode: WifiTypedCodeFields? = null
        private fun finish(progress: (PairingProgress) -> Unit): WifiPairedHost {
            progress(PairingProgress(PairingStage.WAITING_FOR_APPROVAL, "AB".repeat(32)))
            gate?.await(5, TimeUnit.SECONDS)
            failure?.let { throw it }
            identity.create(pki.hostId)
            val paired = WifiPairedHost(pki.hostId, pki.root.encoded, "192.168.15.4", 40002, 1_780_000_000_000L)
            identity.writeMetadata(pki.hostId, paired.encode())
            return paired
        }
        override fun pairWithOffer(offer: WifiPairingOffer, label: String, progress: (PairingProgress) -> Unit): WifiPairedHost { offers++; return finish(progress) }
        override fun pairWithCode(fields: WifiTypedCodeFields, label: String, progress: (PairingProgress) -> Unit): WifiPairedHost { codes++; lastCode = fields; return finish(progress) }
        override fun cancel() { cancels++ }
    }

    private val pairer = FakePairer(identity, pki)

    private fun restore(c: WifiController) = kotlinx.coroutines.runBlocking { c.restore() }

    private fun controller(api: Int = 28, granted: Boolean = true, unconfined: Boolean = true) = WifiController(
        scope = CoroutineScope(Dispatchers.Unconfined), identity = identity, modeStore = mode, pairer = pairer, sessionHost = host,
        apiLevel = api, localNetworkGranted = { granted }, deviceLabel = { "tablet de teste" },
        io = if (unconfined) Dispatchers.Unconfined else Dispatchers.Default,
    )

    private fun validQr(): ByteArray {
        // A QR document the strict offer decoder accepts: the shared certificates fixture supplies a real public root.
        val fixture = WifiFixtures.json("certificates-v1.json")
        val b64 = java.util.Base64.getUrlEncoder().withoutPadding()
        val dec = java.util.Base64.getDecoder()
        val body = org.json.JSONObject().apply {
            put("type", "PanelOSPair/1"); put("version", 1); put("hostId", b64.encodeToString(dec.decode(fixture.getString("hostId"))))
            put("rootCa", b64.encodeToString(dec.decode(fixture.getString("root")))); put("ip", "192.168.15.4"); put("pairPort", 40001); put("sessionPort", 40002)
            put("pairId", b64.encodeToString(ByteArray(16))); put("token", b64.encodeToString(ByteArray(32))); put("expiresAt", 1_780_272_120L)
        }
        return body.toString().toByteArray()
    }

    @Test fun belowApi28WifiIsUnsupportedAndNothingElseHappens() {
        val c = controller(api = 27)
        assertFalse(restore(c))
        assertEquals(WifiConnectionState.UNSUPPORTED, c.state.value.state); assertEquals(WifiFailure.UNSUPPORTED_PLATFORM, c.state.value.failure)
        c.setEnabled(true); c.pairFromCode(WifiFixtures.json("typed-code-v1.json").getString("code"))
        assertEquals(0, pairer.codes); assertTrue(host.starts.isEmpty())
        assertTrue(c.state.value.pairing is WifiPairingUi.Failed)
    }

    @Test fun aFreshInstallLeavesTheCableAloneAndWifiOff() {
        val c = controller()
        assertFalse("the cable path runs exactly as before", restore(c))
        assertEquals(WifiConnectionState.NOT_PAIRED, c.state.value.state); assertFalse(c.state.value.enabled)
        assertTrue(host.starts.isEmpty()); assertEquals(0, host.stops)
        c.setEnabled(true)
        assertFalse("cannot turn Wi-Fi on without a paired PC", c.state.value.enabled); assertTrue(host.starts.isEmpty())
    }

    @Test fun pairingByQrRemembersThePcTurnsWifiOnAndConnectsImmediately() {
        val c = controller()
        c.pairFromQr(validQr())
        val state = c.state.value
        assertEquals(WifiPairingUi.Done, state.pairing); assertTrue(state.enabled); assertTrue(mode.enabled)
        assertEquals(64, state.paired!!.hostIdHex.length)
        assertEquals(1, host.starts.size)
        assertEquals(WifiConnectionState.CONNECTING, state.state)
        c.transportListener.onConnected()
        assertTrue(c.state.value.usingWifi); assertNull(c.state.value.failure)
    }

    @Test fun malformedQrAndMalformedCodeNeverReachTheNetwork() {
        val c = controller()
        c.pairFromQr("not a qr".toByteArray()); assertEquals("a poster is a notice, not an error screen", WifiPairingUi.ForeignQr, c.state.value.pairing)
        c.pairFromCode("AAAAA-BBBBB"); assertEquals(WifiPairingUi.InvalidInput, c.state.value.pairing)
        assertEquals(0, pairer.offers + pairer.codes)
    }

    @Test fun pairingByTypedCodePassesTheDecodedFieldsAndStartsWifi() {
        val c = controller()
        c.pairFromCode(WifiFixtures.json("typed-code-v1.json").getString("code").lowercase().replace("-", " "))
        assertEquals("004217", pairer.lastCode!!.pin)
        assertEquals(WifiPairingUi.Done, c.state.value.pairing); assertEquals(1, host.starts.size)
    }

    @Test fun aFailedPairingChangesNothingAndSaysWhyInWords() {
        pairer.failure = WifiPairingException(PairingFailureReason.REFUSED)
        val c = controller()
        c.pairFromQr(validQr())
        assertEquals(WifiPairingUi.Failed(PairingFailureReason.REFUSED), c.state.value.pairing)
        assertFalse(c.state.value.enabled); assertFalse(mode.enabled); assertNull(c.state.value.paired)
        assertTrue(host.starts.isEmpty()); assertEquals(WifiConnectionState.NOT_PAIRED, c.state.value.state)
        c.dismissPairingResult(); assertEquals(WifiPairingUi.Idle, c.state.value.pairing)
    }

    @Test fun cancellingIsNotAFailureAndGoesBackToTheStart() {
        pairer.failure = WifiPairingException(PairingFailureReason.CANCELLED)
        val c = controller()
        c.pairFromQr(validQr())
        assertEquals(WifiPairingUi.Idle, c.state.value.pairing)
        assertFalse(mode.enabled); assertTrue(host.starts.isEmpty())
    }

    @Test fun aQrFromAnotherSystemNeverInterruptsAPairingThatIsRunning() {
        pairer.gate = CountDownLatch(1)
        val c = controller(unconfined = false)
        c.pairFromQr(validQr())
        assertTrue(c.state.value.pairing is WifiPairingUi.Working)
        c.pairFromQr("a poster".toByteArray())
        assertTrue("still working", c.state.value.pairing is WifiPairingUi.Working)
        pairer.gate!!.countDown()
        val deadline = System.currentTimeMillis() + 5_000
        while (c.state.value.pairing !is WifiPairingUi.Done && System.currentTimeMillis() < deadline) Thread.sleep(10)
    }

    @Test fun aCarrierChoiceThatCannotBeWrittenNeverBreaksTheSessionOrThePairing() {
        val broken = object : WifiModeStore { override var enabled: Boolean
            get() = false
            set(_) = throw java.io.IOException("disk full") }
        val c = WifiController(CoroutineScope(Dispatchers.Unconfined), identity, broken, pairer, host, 30, { true }, { "x" }, Dispatchers.Unconfined)
        c.pairFromQr(validQr())
        assertEquals("pairing still completes", WifiPairingUi.Done, c.state.value.pairing)
        c.setEnabled(false)
        assertFalse(c.state.value.enabled)
        c.forget()
        assertNull(c.state.value.paired)
    }

    @Test fun anIdentityThatThrowsWhileRestoringMeansNotPairedAndTheCable() {
        val first = controller(); first.pairFromQr(validQr()); host.starts.clear()
        val broken = object : com.panelos.panelhome.data.wifi.WifiIdentityPort by identity {
            override fun hostIds(): List<ByteArray> = throw IllegalStateException("Keystore unavailable")
        }
        val c = WifiController(CoroutineScope(Dispatchers.Unconfined), broken, mode, pairer, host, 30, { true }, { "x" }, Dispatchers.Unconfined)
        assertFalse(restore(c))
        assertNull(c.state.value.paired); assertTrue(host.starts.isEmpty())
    }

    @Test fun theSwitchTurnsWifiOffGivingTheCarrierBackAndOnAgain() {
        val c = controller(); c.pairFromQr(validQr()); host.starts.clear()
        c.setEnabled(false)
        assertFalse(c.state.value.enabled); assertFalse(mode.enabled); assertEquals(WifiConnectionState.OFF, c.state.value.state); assertEquals(1, host.stops)
        assertEquals("turning Wi-Fi off gives the carrier back to the cable", 1, host.cableRestores)
        c.setEnabled(true)
        assertTrue(c.state.value.enabled); assertEquals(1, host.starts.size)
    }

    @Test fun forgettingThePcWipesTheKeyTheRecordAndTheChoice() {
        val c = controller(); c.pairFromQr(validQr()); val stops = host.stops
        c.forget()
        assertTrue("key and metadata are gone", identity.hostIds().isEmpty())
        assertFalse(mode.enabled); assertNull(c.state.value.paired); assertEquals(WifiConnectionState.NOT_PAIRED, c.state.value.state)
        assertEquals(stops + 1, host.stops)
        assertTrue("forgetting is local: it never claims the PC revoked anything", c.state.value.failure == null)
    }

    @Test fun restartRestoresAPairedPcOnlyWhenTheUserHadChosenWifi() {
        val first = controller(); first.pairFromQr(validQr()); host.starts.clear()
        val chosen = controller()
        assertTrue(restore(chosen)); assertEquals(1, host.starts.size)
        mode.enabled = false; host.starts.clear()
        val notChosen = controller()
        assertFalse(restore(notChosen)); assertTrue(host.starts.isEmpty()); assertEquals(WifiConnectionState.OFF, notChosen.state.value.state)
    }

    @Test fun aMissingOrCorruptRecordIsNotPairedAndNeverRebuildsTrust() {
        val first = controller(); first.pairFromQr(validQr())
        identity.writeMetadata(pki.hostId, "garbage".toByteArray()); host.starts.clear()
        val c = controller()
        assertFalse(restore(c))
        assertNull(c.state.value.paired); assertFalse("the stale choice is cleared", mode.enabled); assertTrue(host.starts.isEmpty())
    }

    @Test fun withoutTheLocalNetworkPermissionWifiPausesAndRetryAfterGrantingConnects() {
        val first = controller(); first.pairFromQr(validQr()); host.starts.clear()
        var granted = false
        val c = WifiController(CoroutineScope(Dispatchers.Unconfined), identity, mode, pairer, host, 37, { granted }, { "x" }, Dispatchers.Unconfined)
        assertTrue(restore(c))
        assertEquals(WifiConnectionState.PAUSED, c.state.value.state); assertEquals(WifiFailure.PERMISSION_DENIED, c.state.value.failure)
        assertTrue(host.starts.isEmpty())
        granted = true; c.retry()
        assertEquals(1, host.starts.size); assertEquals(WifiConnectionState.CONNECTING, c.state.value.state)
    }

    @Test fun transportReportsDriveTheStateAndAPauseNeedsAnExplicitRetry() {
        val c = controller(); c.pairFromQr(validQr()); host.starts.clear()
        c.transportListener.onFailed(WifiFailure.UNREACHABLE, paused = false)
        assertEquals(WifiConnectionState.RETRYING, c.state.value.state); assertEquals(WifiFailure.UNREACHABLE, c.state.value.failure)
        c.transportListener.onFailed(WifiFailure.REFUSED_BY_PC, paused = true)
        assertEquals(WifiConnectionState.PAUSED, c.state.value.state); assertEquals(WifiFailure.REFUSED_BY_PC, c.state.value.failure)
        val stops = host.stops; val restores = host.cableRestores; c.retry()
        assertEquals(stops + 1, host.stops); assertEquals("a retry does not flash the cable carrier", restores, host.cableRestores)
        assertEquals(1, host.starts.size); assertNull(c.state.value.failure)
        c.transportListener.onConnected(); c.transportListener.onSessionLost()
        assertEquals(WifiConnectionState.RETRYING, c.state.value.state)
    }

    @Test fun transportEventsAreIgnoredWhileWifiIsOff() {
        val c = controller()
        c.transportListener.onConnected(); c.transportListener.onFailed(WifiFailure.UNREACHABLE, false)
        assertEquals(WifiConnectionState.NOT_PAIRED, c.state.value.state); assertNull(c.state.value.failure)
    }

    @Test fun aSecondPairingWhileOneIsRunningIsIgnoredAndCancelReachesThePairer() {
        pairer.gate = CountDownLatch(1)
        val c = controller(unconfined = false)
        c.pairFromQr(validQr())
        assertTrue(c.state.value.pairing is WifiPairingUi.Working)
        c.pairFromQr(validQr())
        c.cancelPairing(); assertEquals(1, pairer.cancels)
        pairer.gate!!.countDown()
        val deadline = System.currentTimeMillis() + 5_000
        while (c.state.value.pairing !is WifiPairingUi.Done && System.currentTimeMillis() < deadline) Thread.sleep(10)
        assertEquals(1, pairer.offers)
        assertNotNull(c.state.value.paired)
    }
}