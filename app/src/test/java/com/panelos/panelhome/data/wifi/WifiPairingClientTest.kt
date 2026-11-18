package com.panelos.panelhome.data.wifi

import java.net.ServerSocket
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The device side against a real TLS 1.3 listener (JSSE on 127.0.0.1) that speaks the PC's wire and checks the proof the
 * way the PC does. The AndroidKeyStore is replaced by software keys with the same contract; nothing else is faked.
 */
class WifiPairingClientTest {
    private val pki = TestPki.create()
    private val hosts = mutableListOf<FakePairingHost>()
    private val identity = SoftwareWifiIdentity()

    @After fun stop() { hosts.forEach { it.close() } }

    private fun host(pin: String? = null, behavior: FakePairingHost.Behavior = FakePairingHost.Behavior.APPROVE, delay: Long = 0) =
        FakePairingHost(pki, pin, behavior, delay).also { hosts.add(it) }

    /** An invitation that is valid for the next ten minutes, like the PC's. */
    private val validUntil = System.currentTimeMillis() / 1000 + 600

    private fun offer(host: FakePairingHost, rootDer: ByteArray = pki.root.encoded, hostId: ByteArray = pki.hostId, expiresAt: Long = validUntil) =
        WifiPairingOffer(hostId, rootDer, host.offerPairId, host.offerToken.copyOf(), "127.0.0.1", host.port, 40002, expiresAt)

    private fun fields(host: FakePairingHost, pin: String, hostId: ByteArray = pki.hostId) =
        WifiTypedCodeFields(hostId, host.offerPairId, "127.0.0.1", host.port, 40002, pin)

    private fun client(approvalWaitMillis: Int = 10_000) = WifiPairingClient(identity, apiLevel = 28, approvalWaitMillis = approvalWaitMillis)

    private fun failure(block: () -> Unit): PairingFailureReason = assertThrows(WifiPairingException::class.java, block).reason

    @Test fun qrPairingProvesPossessionInstallsTheCertificateAndRemembersThePc() {
        val host = host()
        val stages = mutableListOf<PairingProgress>()
        val paired = client().pairWithOffer(offer(host), "Tablet da sala") { stages.add(it) }

        assertTrue("the PC accepted the proof exactly as it would", host.proofValid == true)
        assertEquals("Tablet da sala", host.requests.single().getValue("label"))
        assertArrayEquals(pki.hostId, paired.hostId)
        assertEquals("127.0.0.1", paired.ip); assertEquals(40002, paired.sessionPort)
        assertTrue(identity.exists(pki.hostId))
        assertNotNull("the certificate is installed with its key", identity.entry(pki.hostId).certificateChain)
        assertArrayEquals("the record is stored (the loopback address is only a test endpoint, the reader refuses it)", paired.encode(), identity.readMetadata(pki.hostId))
        assertEquals(
            listOf(PairingStage.CONNECTING, PairingStage.VERIFYING_PC, PairingStage.REQUESTING, PairingStage.WAITING_FOR_APPROVAL, PairingStage.INSTALLING),
            stages.map { it.stage },
        )
        // The tablet shows the same full fingerprint the PC shows the operator: SHA-256 of the key it just made.
        val spki = java.util.Base64.getUrlDecoder().decode(host.requests.single().getValue("spki"))
        val expected = WifiCrypto.hash(spki).joinToString("") { "%02X".format(it.toInt() and 255) }
        assertEquals(expected, stages.first { it.stage == PairingStage.WAITING_FOR_APPROVAL }.fingerprint)
        assertEquals(64, expected.length)
    }

    @Test fun aRefusalLeavesNoKeyBehindAndNeverReportsWhy() {
        val host = host(behavior = FakePairingHost.Behavior.DENY)
        assertEquals(PairingFailureReason.REFUSED, failure { client().pairWithOffer(offer(host), "x") })
        assertFalse("no orphan key after a refusal", identity.exists(pki.hostId))
    }

    @Test fun aWrongTokenIsRefusedByTheHostAndCleansUp() {
        val host = host()
        val wrong = WifiPairingOffer(pki.hostId, pki.root.encoded, host.offerPairId, ByteArray(32) { 9 }, "127.0.0.1", host.port, 40002, validUntil)
        assertEquals(PairingFailureReason.REFUSED, failure { client().pairWithOffer(wrong, "x") })
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun aPcWhoseRootIsNotTheScannedOneIsRejectedBeforeAnyRequest() {
        val host = host()
        val other = TestPki.create()
        // The QR pinned another PC (its root and host id); the endpoint answers with this PC's certificate.
        val swapped = WifiPairingOffer(other.hostId, other.root.encoded, host.offerPairId, host.offerToken.copyOf(), "127.0.0.1", host.port, 40002, validUntil)
        assertEquals(PairingFailureReason.IDENTITY_MISMATCH, failure { client().pairWithOffer(swapped, "x") })
        assertFalse("not a single request reached the impostor", host.requestSeen.get())
        assertEquals("no key was even created", 0, identity.creates)
    }

    @Test fun theCertificateResultMustMatchTheKeyAndThePinnedRoot() {
        for (behavior in listOf(FakePairingHost.Behavior.WRONG_LEAF_KEY, FakePairingHost.Behavior.WRONG_ROOT_IN_RESULT)) {
            val host = host(behavior = behavior)
            val reason = failure { client().pairWithOffer(offer(host), "x") }
            assertTrue("$behavior -> $reason", reason == PairingFailureReason.IDENTITY_MISMATCH || reason == PairingFailureReason.PROTOCOL)
            assertFalse("$behavior leaves nothing", identity.exists(pki.hostId))
        }
    }

    @Test fun noApprovalInTimeIsATimeoutNotAnotherFailureAndCleansUp() {
        val host = host(delay = 1_500)
        assertEquals(PairingFailureReason.TIMED_OUT, failure { client(approvalWaitMillis = 300).pairWithOffer(offer(host), "x") })
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun anUnreachablePortAndAHostThatClosesEarlyAreDistinguishedFromARefusal() {
        val closed = ServerSocket(0).use { it.localPort }
        val missing = WifiPairingOffer(pki.hostId, pki.root.encoded, ByteArray(16), ByteArray(32), "127.0.0.1", closed, 40002, validUntil)
        assertEquals(PairingFailureReason.UNREACHABLE, failure { client().pairWithOffer(missing, "x") })
        val host = host(behavior = FakePairingHost.Behavior.CLOSE_AFTER_CHALLENGE)
        val reason = failure { client().pairWithOffer(offer(host), "x") }
        assertTrue(reason == PairingFailureReason.REFUSED || reason == PairingFailureReason.PROTOCOL)
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun belowApi28NothingIsAttemptedAndNothingIsCreated() {
        val host = host()
        val reason = failure { WifiPairingClient(identity, apiLevel = 27).pairWithOffer(offer(host), "x") }
        assertEquals(PairingFailureReason.UNSUPPORTED_PLATFORM, reason)
        assertEquals(0, identity.creates); assertFalse(host.requestSeen.get())
    }

    @Test fun scanningANewInvitationForTheSamePcReplacesTheOldPairingOnlyAfterItWasApproved() {
        val first = host()
        val before = client().pairWithOffer(offer(first), "first")
        val certificateBefore = identity.entry(pki.hostId).certificate.encoded
        val second = host()
        val after = client().pairWithOffer(offer(second), "second")
        assertEquals("nothing was forgotten explicitly: the new key replaced the old one at the commit", 0, identity.forgets)
        assertEquals("second", second.requests.single().getValue("label"))
        assertTrue(identity.exists(pki.hostId)); assertEquals(0, identity.stagedCount)
        assertFalse("the identity is the new one", certificateBefore.contentEquals(identity.entry(pki.hostId).certificate.encoded))
        assertArrayEquals(after.encode(), identity.readMetadata(pki.hostId))
        assertFalse(before.encode().contentEquals(after.encode()))
    }

    @Test fun aNewPairingThatFailsLeavesTheOldPairingWorkingUntouched() {
        val first = host()
        val before = client().pairWithOffer(offer(first), "first")
        val certificateBefore = identity.entry(pki.hostId).certificate.encoded
        // Refused by the operator, a dropped connection and a wrong certificate: none of them may cost the working pairing.
        for (behavior in listOf(FakePairingHost.Behavior.DENY, FakePairingHost.Behavior.CLOSE_AFTER_CHALLENGE, FakePairingHost.Behavior.WRONG_LEAF_KEY)) {
            val failing = host(behavior = behavior)
            failure { client().pairWithOffer(offer(failing), "again") }
            assertTrue("$behavior keeps the old pairing", identity.exists(pki.hostId))
            assertArrayEquals(certificateBefore, identity.entry(pki.hostId).certificate.encoded)
            assertArrayEquals(before.encode(), identity.readMetadata(pki.hostId))
            assertEquals("the unfinished key is discarded", 0, identity.stagedCount)
        }
        assertEquals(0, identity.forgets)
    }

    @Test fun anInvitationAlreadyPastItsExpiryIsReportedAsExpiredBeforeAnythingHappens() {
        val host = host()
        val expired = offer(host, expiresAt = System.currentTimeMillis() / 1000 - 3_600)
        assertEquals(PairingFailureReason.OFFER_EXPIRED, failure { client().pairWithOffer(expired, "x") })
        assertEquals("no key was made", 0, identity.creates)
        assertFalse("the PC was never contacted", host.requestSeen.get())
    }

    @Test fun aTabletClockAMinuteOffDoesNotRejectAFreshInvitation() {
        val host = host()
        val justExpired = offer(host, expiresAt = System.currentTimeMillis() / 1000 - 60)
        assertNotNull(client().pairWithOffer(justExpired, "x"))
    }

    @Test fun aRefusalThatComesAfterAboutAMinuteOfWaitingIsTheTimeout() {
        val host = host(behavior = FakePairingHost.Behavior.DENY)
        var calls = 0
        // Call 1 is the expiry check, 2 the start of the wait, 3 the refusal: one minute later by this clock.
        val fake = WifiPairingClient(identity, apiLevel = 28, now = { 1_000L + 60_000L * maxOf(0, ++calls - 2) })
        assertEquals(PairingFailureReason.TIMED_OUT, failure { fake.pairWithOffer(offer(host, expiresAt = 600), "x") })
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun pinModePairsWithOnlyTheTypedCodeAfterValidatingThePcAgainstTheTypedHostId() {
        val host = host(pin = "004217")
        val stages = mutableListOf<PairingStage>()
        val paired = client().pairWithCode(fields(host, "004217"), "Tablet") { stages.add(it.stage) }
        assertTrue(host.proofValid == true)
        assertArrayEquals(pki.hostId, paired.hostId)
        assertTrue(PairingStage.VERIFYING_PC in stages)
    }

    @Test fun pinModeAbortsBeforeSendingTheTokenWhenTheHostIdIsNotThePcThatAnswers() {
        val host = host(pin = "004217")
        val typedForAnotherPc = fields(host, "004217", hostId = ByteArray(32) { 7 })
        assertEquals(PairingFailureReason.IDENTITY_MISMATCH, failure { client().pairWithCode(typedForAnotherPc, "x") })
        assertFalse("the PIN never left the tablet", host.requestSeen.get())
        assertEquals(0, identity.creates)
    }

    @Test fun pinModeWithAWrongPinIsRefusedByTheHostAndCleansUp() {
        val host = host(pin = "004217")
        assertEquals(PairingFailureReason.REFUSED, failure { client().pairWithCode(fields(host, "004218"), "x") })
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun cancelFromAnotherThreadEndsTheWaitAsCancelled() {
        val host = host(delay = 5_000)
        val pairing = client()
        val outcome = java.util.concurrent.atomic.AtomicReference<PairingFailureReason?>()
        val worker = Thread { outcome.set(runCatching { pairing.pairWithOffer(offer(host), "x") }.exceptionOrNull().let { (it as? WifiPairingException)?.reason }) }
        worker.start()
        val deadline = System.currentTimeMillis() + 5_000
        while (!host.requestSeen.get() && System.currentTimeMillis() < deadline) Thread.sleep(10)
        pairing.cancel()
        worker.join(5_000)
        assertEquals(PairingFailureReason.CANCELLED, outcome.get())
        assertFalse(identity.exists(pki.hostId))
    }

    @Test fun labelsLoseControlAndDirectionCharactersAndAreBounded() {
        assertEquals("Tabletevil", WifiPairingClient.normalizeLabel("Tablet\u202Eevil"))
        assertEquals("ab", WifiPairingClient.normalizeLabel("a\nb\u0007").replace("\n", ""))
        assertEquals(64, WifiPairingClient.normalizeLabel("x".repeat(200)).length)
        assertEquals("Tablet", WifiPairingClient.normalizeLabel("  \u200B \u202E "))
    }

    @Test fun pairedHostRecordRoundTripsAndRefusesBadFields() {
        val host = WifiPairedHost(pki.hostId, pki.root.encoded, "192.168.15.4", 40002, 1780000000000L)
        val back = WifiPairedHost.decode(host.encode())
        assertArrayEquals(host.rootDer, back.rootDer); assertEquals("192.168.15.4", back.ip); assertEquals(1780000000000L, back.pairedAtMillis)
        for (bad in listOf("""{"hostId":"AA"}""", String(host.encode()).replace("192.168.15.4", "8.8.8.8"), String(host.encode()).replace("40002", "8765")))
            assertThrows(bad, IllegalArgumentException::class.java) { WifiPairedHost.decode(bad.toByteArray()) }
    }
}