package com.panelos.panelhome.data.wifi

import android.content.Context
import android.os.Build
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.panelos.panelhome.data.transport.TlsWifiSessionOpener
import com.panelos.panelhome.data.transport.TransportMetrics
import java.net.InetSocketAddress
import java.security.Security
import java.security.Signature
import java.security.interfaces.ECPublicKey
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

/** Run only on an explicitly selected API 28 emulator. No PC LAN or physical tablet is involved. */
@RunWith(AndroidJUnit4::class)
class WifiApi28InstrumentedTest {
    @Test fun nonExportableKeystoreKeySignsRawDigestAndPairsThenAuthenticatesWithBundledConscrypt() {
        assertEquals("This compatibility proof must run on Android 9", 28, Build.VERSION.SDK_INT)
        val providers = Security.getProviders().toList()
        val context = ApplicationProvider.getApplicationContext<Context>()
        val identity = AndroidWifiIdentity(context)
        val pki = TestPki.create()
        try {
            // Use the production adapter, including staging, certificate installation and encrypted metadata.
            for (typed in listOf(false, true)) {
                FakeSessionHost(pki).use { session ->
                    FakePairingHost(pki, pin = if (typed) "123456" else null).use { pairing ->
                        val client = WifiPairingClient(identity, endpoint = { _, _ -> InetSocketAddress("127.0.0.1", pairing.port) })
                        val paired = if (typed) {
                            client.pairWithCode(WifiTypedCodeFields(pki.hostId, pairing.offerPairId, "192.168.15.4",
                                pairing.port, session.port, "123456"), "API 28 emulator")
                        } else {
                            WifiPairingOffer(pki.hostId, pki.root.encoded, pairing.offerPairId, pairing.offerToken.copyOf(),
                                "192.168.15.4", pairing.port, session.port, System.currentTimeMillis() / 1000 + 600).use {
                                client.pairWithOffer(it, "API 28 emulator")
                            }
                        }
                        assertEquals(true, pairing.proofValid)
                        val entry = identity.entry(pki.hostId)
                        assertNull("Private key must not be exportable", entry.privateKey.encoded)
                        assertEquals(256, (entry.certificate.publicKey as ECPublicKey).params.order.bitLength())
                        val digest = WifiCrypto.hash("API 28 raw ECDSA proof".toByteArray())
                        val signature = Signature.getInstance("NONEwithECDSA").run {
                            initSign(entry.privateKey); update(digest); sign()
                        }
                        assertTrue(Signature.getInstance("NONEwithECDSA").run {
                            initVerify(entry.certificate.publicKey); update(digest); verify(signature)
                        })
                        assertArrayEquals(paired.encode(), identity.readMetadata(pki.hostId))
                        TlsWifiSessionOpener(identity, endpoint = { _, _ -> InetSocketAddress("127.0.0.1", session.port) })
                            .open(paired, TransportMetrics()) {}.use {
                                assertEquals(1, session.handshakes.get())
                                assertArrayEquals(entry.certificate.encoded, session.clientCertificates.single().encoded)
                            }
                    }
                }
            }
            assertEquals("No global provider installation", providers, Security.getProviders().toList())
        } finally {
            identity.forget(pki.hostId)
            assertFalse(identity.exists(pki.hostId))
        }
    }
}
