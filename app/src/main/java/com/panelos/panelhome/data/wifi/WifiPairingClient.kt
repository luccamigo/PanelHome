package com.panelos.panelhome.data.wifi

import android.os.Build
import androidx.annotation.RequiresApi
import java.io.EOFException
import java.io.IOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.NoRouteToHostException
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.text.Normalizer
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket

internal enum class PairingFailureReason {
    UNSUPPORTED_PLATFORM, UNREACHABLE, IDENTITY_MISMATCH, REFUSED, TIMED_OUT, PROTOCOL, CANCELLED,
    /** The QR was already past its expiry when read (by this tablet's clock, with some tolerance). */
    OFFER_EXPIRED,
}

/** The reason is all the UI is told. Never the host's text and never anything from the QR. */
internal class WifiPairingException(val reason: PairingFailureReason, cause: Throwable? = null) : IOException("Pairing failed: $reason", cause)

internal enum class PairingStage { CONNECTING, VERIFYING_PC, REQUESTING, WAITING_FOR_APPROVAL, INSTALLING }

/** [fingerprint] is the full SHA-256 of this tablet's new public key (64 hex), to compare with the PC's screen. */
internal class PairingProgress(val stage: PairingStage, val fingerprint: String? = null)

/**
 * The device side of PanelOSPair/1: pinned TLS 1.3 to the pairing port, challenge, proof of possession with a key made
 * in the Keystore for this PC, the wait for the operator's local approval, and the certificate. Everything it learns
 * from the network is validated before it is used and none of it is logged. The new key is staged next to any current
 * pairing and replaces it only on success; a failure discards the staged key and leaves the previous pairing as it was.
 */
internal class WifiPairingClient(
    private val identity: WifiIdentityPort,
    private val apiLevel: Int = Build.VERSION.SDK_INT,
    private val connectTimeoutMillis: Int = 3_000,
    private val approvalWaitMillis: Int = 70_000,
    private val now: () -> Long = System::currentTimeMillis,
    /** Test seam only: the cross-language harness points the offer's private address at loopback. */
    private val endpoint: (String, Int) -> InetSocketAddress = { ip, port -> InetSocketAddress(ip, port) },
) {
    @Volatile private var active: Socket? = null
    @Volatile private var cancelled = false

    /** Closes the live connection from another thread; the blocked call then fails as CANCELLED. */
    fun cancel() {
        cancelled = true
        runCatching { active?.close() }
    }

    @RequiresApi(28)
    fun pairWithOffer(offer: WifiPairingOffer, label: String, progress: (PairingProgress) -> Unit = {}): WifiPairedHost =
        pair(Target(offer.hostId, offer.rootDer, offer.pairId, offer.token.copyOf(), offer.ip, offer.pairPort, offer.sessionPort, pin = false,
            expiresAtSeconds = offer.expiresAt), label, progress)

    @RequiresApi(28)
    fun pairWithCode(fields: WifiTypedCodeFields, label: String, progress: (PairingProgress) -> Unit = {}): WifiPairedHost =
        pair(Target(fields.hostId, null, fields.pairId, WifiCrypto.hash(fields.pin.toByteArray(Charsets.US_ASCII)), fields.ip,
            fields.pairPort, fields.sessionPort, pin = true), label, progress)

    private class Target(val hostId: ByteArray, val rootDer: ByteArray?, val pairId: ByteArray, val token: ByteArray,
        val ip: String, val pairPort: Int, val sessionPort: Int, val pin: Boolean, val expiresAtSeconds: Long? = null)

    @RequiresApi(28)
    private fun pair(target: Target, label: String, progress: (PairingProgress) -> Unit): WifiPairedHost {
        if (apiLevel < 28) throw WifiPairingException(PairingFailureReason.UNSUPPORTED_PLATFORM)
        cancelled = false
        var staged = false
        var stage = PairingStage.CONNECTING
        val plain = Socket()
        active = plain
        try {
            // The PC enforces the real expiry; this only spares the user a doomed attempt and says why. The tolerance keeps a
            // tablet clock that is a little off from rejecting a good QR (and a too-generous one is simply refused by the PC).
            target.expiresAtSeconds?.let { expires ->
                if (now() / 1000 > expires + EXPIRY_TOLERANCE_SECONDS) throw WifiPairingException(PairingFailureReason.OFFER_EXPIRED)
            }
            progress(PairingProgress(PairingStage.CONNECTING))
            plain.connect(endpoint(target.ip, target.pairPort), connectTimeoutMillis)
            var captured: Array<X509Certificate>? = null
            val context = if (target.pin) {
                WifiTls.deferredPairingContext({ captured = it }, apiLevel)
            } else {
                val root = certificate(requireNotNull(target.rootDer))
                WifiTls.context(target.hostId, root, null, apiLevel)
            }
            val socket = context.socketFactory.createSocket(plain, target.ip, target.pairPort, true) as SSLSocket
            active = socket
            WifiTls.configure(socket, target.hostId, pairing = true, apiLevel)
            socket.startHandshake()
            WifiTls.checkNegotiated(socket, pairing = true, apiLevel)

            stage = PairingStage.VERIFYING_PC
            progress(PairingProgress(PairingStage.VERIFYING_PC))
            val wire = WifiPairingWire(socket.inputStream, socket.outputStream)
            val rootDer: ByteArray
            if (target.pin) {
                val hostRoot = wire.read(setOf("type", "rootCa"))
                if (hostRoot.getValue("type") != "host_root") throw WifiPairingException(PairingFailureReason.PROTOCOL)
                rootDer = WifiBase64.decode(hostRoot.getValue("rootCa"))
                val root = certificate(rootDer)
                // The typed host id is the only anchor: the root must hash to it, and the certificate that just ended
                // the TLS handshake must chain to that root with the right name. Nothing has been sent yet.
                try {
                    WifiTls.validateRoot(root, target.hostId)
                    WifiTls.validateServer(requireNotNull(captured), target.hostId, root)
                } catch (error: Exception) {
                    throw WifiPairingException(PairingFailureReason.IDENTITY_MISMATCH, error)
                }
            } else {
                rootDer = requireNotNull(target.rootDer)
            }
            val challengeFields = wire.read(setOf("type", "challenge"))
            if (challengeFields.getValue("type") != "challenge") throw WifiPairingException(PairingFailureReason.PROTOCOL)
            val challenge = decode(challengeFields.getValue("challenge"), 32)

            stage = PairingStage.REQUESTING
            progress(PairingProgress(PairingStage.REQUESTING))
            // A new key beside the current pairing: the old one is replaced only when the PC has approved and everything installed.
            val spki = identity.stageNewKey(target.hostId)
            staged = true
            val fingerprint = WifiCrypto.hash(spki).joinToString("") { "%02X".format(it.toInt() and 255) }
            val signature = identity.signWithStagedKey(target.hostId, WifiCrypto.message(challenge, target.hostId, spki, target.pairId, target.token))
            wire.write(
                "{\"type\":\"pair_request\",\"pairId\":${b64(target.pairId)},\"token\":${b64(target.token)}," +
                    "\"spki\":${b64(spki)},\"label\":${WifiFlatJson.quote(normalizeLabel(label))},\"signature\":${b64(signature)}}",
            )

            stage = PairingStage.WAITING_FOR_APPROVAL
            progress(PairingProgress(PairingStage.WAITING_FOR_APPROVAL, fingerprint))
            val waitStarted = now()
            socket.soTimeout = approvalWaitMillis
            val result = wire.read(setOf("type", "status"), optional = setOf("certificate", "rootCa"))
            if (result.getValue("type") != "pair_result") throw WifiPairingException(PairingFailureReason.PROTOCOL)
            if (result.getValue("status") != "approved") {
                // The PC answers the same way for a refusal and for the end of its 60 s; a refusal that comes after about a
                // minute of waiting is the clock running out, which deserves its own message.
                throw WifiPairingException(if (now() - waitStarted >= APPROVAL_TIMEOUT_MILLIS) PairingFailureReason.TIMED_OUT else PairingFailureReason.REFUSED)
            }
            if (result["certificate"] == null || result["rootCa"] == null) throw WifiPairingException(PairingFailureReason.PROTOCOL)
            if (!decodeAny(result.getValue("rootCa")).contentEquals(rootDer)) throw WifiPairingException(PairingFailureReason.IDENTITY_MISMATCH)

            stage = PairingStage.INSTALLING
            progress(PairingProgress(PairingStage.INSTALLING, fingerprint))
            val leaf = certificate(decodeAny(result.getValue("certificate")))
            val host = WifiPairedHost(target.hostId, rootDer, target.ip, target.sessionPort, now())
            identity.commitStaged(target.hostId, leaf, certificate(rootDer), host.encode())
            staged = false
            return host
        } catch (error: Throwable) {
            if (staged) runCatching { identity.discardStaged(target.hostId) } // the previous pairing, if any, is untouched
            throw if (cancelled) WifiPairingException(PairingFailureReason.CANCELLED, error) else classify(error, stage)
        } finally {
            target.token.fill(0)
            runCatching { active?.close() }
            runCatching { plain.close() }
            active = null
        }
    }

    private fun certificate(der: ByteArray): X509Certificate =
        CertificateFactory.getInstance("X.509").generateCertificate(der.inputStream()) as X509Certificate

    private fun b64(bytes: ByteArray) = "\"" + WifiBase64.encode(bytes) + "\""
    private fun decodeAny(text: String): ByteArray {
        require(text.length in 1..1400 && text.matches(Regex("[A-Za-z0-9_-]+")))
        return WifiBase64.decode(text)
    }
    private fun decode(text: String, size: Int): ByteArray = decodeAny(text).also { require(it.size == size) }

    companion object {
        /** A tablet clock this far off still passes the QR on to the PC, which has the final say. */
        internal const val EXPIRY_TOLERANCE_SECONDS = 120L
        private const val APPROVAL_TIMEOUT_MILLIS = 55_000L

        /** Display name: no control, format or separator characters (so no right-to-left override), 1..64 characters. */
        fun normalizeLabel(label: String): String {
            val clean = Normalizer.normalize(label, Normalizer.Form.NFC).filter { character ->
                when (Character.getType(character).toByte()) {
                    Character.CONTROL, Character.FORMAT, Character.LINE_SEPARATOR, Character.PARAGRAPH_SEPARATOR,
                    Character.PRIVATE_USE, Character.SURROGATE, Character.UNASSIGNED -> false
                    else -> true
                }
            }.trim()
            return clean.take(64).trim().ifEmpty { "Tablet" }
        }

        internal fun classify(error: Throwable, stage: PairingStage): WifiPairingException {
            if (error is WifiPairingException) return error
            val causes = generateSequence(error) { it.cause }.take(8).toList()
            return WifiPairingException(
                when {
                    causes.any { it is CertificateException } -> PairingFailureReason.IDENTITY_MISMATCH
                    causes.any { it is SocketTimeoutException } ->
                        if (stage == PairingStage.WAITING_FOR_APPROVAL) PairingFailureReason.TIMED_OUT else PairingFailureReason.UNREACHABLE
                    causes.any { it is ConnectException || it is NoRouteToHostException || it is java.net.UnknownHostException } -> PairingFailureReason.UNREACHABLE
                    // The host closed (invitation gone, wrong token, refused) or dropped: nothing more specific is told on purpose.
                    causes.any { it is EOFException || it is SSLException || it is java.net.SocketException } ->
                        if (stage == PairingStage.CONNECTING) PairingFailureReason.UNREACHABLE else PairingFailureReason.REFUSED
                    else -> PairingFailureReason.PROTOCOL
                },
                error,
            )
        }
    }
}