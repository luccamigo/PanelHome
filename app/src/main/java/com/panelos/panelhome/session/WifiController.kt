package com.panelos.panelhome.session

import android.annotation.SuppressLint
import android.os.Build
import com.panelos.panelhome.data.transport.WifiFailure
import com.panelos.panelhome.data.transport.WifiTransportListener
import com.panelos.panelhome.data.wifi.InvalidPairingCode
import com.panelos.panelhome.data.wifi.InvalidPairingQr
import com.panelos.panelhome.data.wifi.PairingFailureReason
import com.panelos.panelhome.data.wifi.PairingProgress
import com.panelos.panelhome.data.wifi.PairingStage
import com.panelos.panelhome.data.wifi.WifiIdentityPort
import com.panelos.panelhome.data.wifi.WifiPairedHost
import com.panelos.panelhome.data.wifi.WifiPairingClient
import com.panelos.panelhome.data.wifi.WifiPairingException
import com.panelos.panelhome.data.wifi.WifiPairingOffer
import com.panelos.panelhome.data.wifi.WifiTypedCode
import com.panelos.panelhome.data.wifi.WifiTypedCodeFields
import java.io.File
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

internal enum class WifiConnectionState { OFF, NOT_PAIRED, UNSUPPORTED, CONNECTING, CONNECTED, RETRYING, PAUSED }

internal data class WifiPairedSummary(val hostIdHex: String, val pairedAtMillis: Long)

/** What the pairing screen shows. The fingerprint is the tablet key's full SHA-256 for comparing with the PC's screen. */
internal sealed interface WifiPairingUi {
    data object Idle : WifiPairingUi
    data class Working(val stage: PairingStage, val fingerprint: String?) : WifiPairingUi
    data class Failed(val reason: PairingFailureReason) : WifiPairingUi
    /** A typed code that does not parse: shown under the field. */
    data object InvalidInput : WifiPairingUi
    /** A QR that is not a PanelOS pairing QR (a poster, a menu): a short notice over the camera, which keeps reading. */
    data object ForeignQr : WifiPairingUi
    data object Done : WifiPairingUi
}

/**
 * Everything the settings and pairing screens need to say about Wi-Fi. [enabled] is the user's choice of carrier and it
 * is off until they pair; the cable is untouched while it is off.
 */
internal data class WifiUiState(
    val supported: Boolean,
    val enabled: Boolean,
    val paired: WifiPairedSummary?,
    val state: WifiConnectionState,
    val failure: WifiFailure?,
    val pairing: WifiPairingUi,
) {
    /** The carrier the panel is using right now. */
    val usingWifi: Boolean get() = state == WifiConnectionState.CONNECTED
}

internal interface WifiPairer {
    fun pairWithOffer(offer: WifiPairingOffer, label: String, progress: (PairingProgress) -> Unit): WifiPairedHost
    fun pairWithCode(fields: WifiTypedCodeFields, label: String, progress: (PairingProgress) -> Unit): WifiPairedHost
    fun cancel()
}

/** Starts and stops the Wi-Fi session in the Service, and puts the cable carrier back when Wi-Fi stops. */
internal interface WifiSessionHost {
    fun startWifi(host: WifiPairedHost)
    /** [restoreCable] is false when Wi-Fi is about to be started again right away (retry, replaced pairing). */
    fun stopWifi(restoreCable: Boolean = true)
}

/** One bit that survives a restart: the user chose Wi-Fi. Kept in a no-backup directory. */
internal interface WifiModeStore {
    var enabled: Boolean
}

internal class FileWifiModeStore(private val file: File) : WifiModeStore {
    override var enabled: Boolean
        get() = runCatching { file.readText().trim() == "1" }.getOrDefault(false)
        set(value) { file.parentFile?.mkdirs(); file.writeText(if (value) "1" else "0") }
}

@SuppressLint("NewApi") // every call is behind the apiLevel >= 28 check inside WifiPairingClient
internal class AndroidWifiPairer(identity: WifiIdentityPort, apiLevel: Int = Build.VERSION.SDK_INT) : WifiPairer {
    private val client = WifiPairingClient(identity, apiLevel)
    override fun pairWithOffer(offer: WifiPairingOffer, label: String, progress: (PairingProgress) -> Unit) =
        client.pairWithOffer(offer, label, progress)
    override fun pairWithCode(fields: WifiTypedCodeFields, label: String, progress: (PairingProgress) -> Unit) =
        client.pairWithCode(fields, label, progress)
    override fun cancel() = client.cancel()
}

/**
 * Owner of the Wi-Fi decision. It pairs (QR or typed code), remembers the single paired PC, starts and stops the Wi-Fi
 * session through the Service, follows what the transport reports, and forgets the PC on request. It never connects to
 * anything it was not paired with, and it never turns Wi-Fi on by itself: only a successful pairing or the user's switch.
 */
internal class WifiController(
    private val scope: CoroutineScope,
    private val identity: WifiIdentityPort,
    private val modeStore: WifiModeStore,
    private val pairer: WifiPairer,
    private val sessionHost: WifiSessionHost,
    private val apiLevel: Int = Build.VERSION.SDK_INT,
    private val localNetworkGranted: () -> Boolean = { true },
    private val deviceLabel: () -> String = { Build.MODEL ?: "Tablet" },
    private val io: CoroutineDispatcher = Dispatchers.IO,
    private val log: (String) -> Unit = {},
) {
    private var host: WifiPairedHost? = null
    private var pairingJob: Job? = null
    private var forgetJob: Job? = null
    private val _state = MutableStateFlow(initialState())
    val state: StateFlow<WifiUiState> = _state.asStateFlow()

    val transportListener: WifiTransportListener = object : WifiTransportListener {
        override fun onConnecting() = _state.update { if (it.enabled) it.copy(state = WifiConnectionState.CONNECTING, failure = null) else it }
        override fun onConnected() = _state.update { if (it.enabled) it.copy(state = WifiConnectionState.CONNECTED, failure = null) else it }
        override fun onFailed(failure: WifiFailure, paused: Boolean) = _state.update {
            if (!it.enabled) it else it.copy(state = if (paused) WifiConnectionState.PAUSED else WifiConnectionState.RETRYING, failure = failure)
        }
        override fun onSessionLost() = _state.update {
            if (it.enabled && it.state == WifiConnectionState.CONNECTED) it.copy(state = WifiConnectionState.RETRYING) else it
        }
    }

    private fun initialState() = WifiUiState(
        supported = apiLevel >= 28, enabled = false, paired = null,
        state = if (apiLevel >= 28) WifiConnectionState.NOT_PAIRED else WifiConnectionState.UNSUPPORTED,
        failure = if (apiLevel >= 28) null else WifiFailure.UNSUPPORTED_PLATFORM, pairing = WifiPairingUi.Idle,
    )

    /**
     * Called once by the Service. Loads the paired PC (a missing or corrupt record means "not paired": trust is never
     * rebuilt from half a record) and, only if the user had chosen Wi-Fi, starts it. Returns whether Wi-Fi took over;
     * when it did not, the caller runs the cable path exactly as before.
     */
    suspend fun restore(): Boolean {
        if (apiLevel < 28) return false
        // The Keystore and the metadata file are read off the caller's (main) thread.
        host = withContext(io) { loadHost() }
        val wanted = modeStore.enabled && host != null
        if (modeStore.enabled && host == null) writeMode(false)
        _state.value = _state.value.copy(
            enabled = wanted, paired = host?.summary(), state = if (host == null) WifiConnectionState.NOT_PAIRED else if (wanted) WifiConnectionState.CONNECTING else WifiConnectionState.OFF,
        )
        return if (wanted) startIfAllowed() else false
    }

    /** The user's carrier choice is one small file; if it cannot be written the session still runs and the next start asks again. */
    private fun writeMode(value: Boolean) {
        runCatching { modeStore.enabled = value }.onFailure { log("Could not store the carrier choice: ${it::class.java.simpleName}") }
    }

    private fun loadHost(): WifiPairedHost? = try {
        identity.hostIds().firstNotNullOfOrNull { id -> runCatching { WifiPairedHost.decode(identity.readMetadata(id)) }.getOrNull()?.takeIf { it.hostId.contentEquals(id) } }
    } catch (_: Exception) { null }

    private fun WifiPairedHost.summary() = WifiPairedSummary(hostIdHex, pairedAtMillis)

    /** The user's switch. Turning it on needs a paired PC; turning it off gives the carrier back to the cable. */
    fun setEnabled(enabled: Boolean) {
        val current = host
        if (enabled && (current == null || apiLevel < 28)) return
        writeMode(enabled)
        if (enabled) {
            _state.update { it.copy(enabled = true, state = WifiConnectionState.CONNECTING, failure = null) }
            startIfAllowed()
        } else {
            _state.update { it.copy(enabled = false, state = if (current == null) WifiConnectionState.NOT_PAIRED else WifiConnectionState.OFF, failure = null) }
            sessionHost.stopWifi()
        }
    }

    private fun startIfAllowed(): Boolean {
        val current = host ?: return false
        if (!localNetworkGranted()) {
            _state.update { it.copy(state = WifiConnectionState.PAUSED, failure = WifiFailure.PERMISSION_DENIED) }
            return true // Wi-Fi is the chosen carrier; the cable is not silently substituted while the user fixes the permission
        }
        sessionHost.startWifi(current)
        return true
    }

    /** After a pause (refused by the PC, permission, identity): try again with a fresh transport. */
    fun retry() {
        val current = host ?: return
        if (!_state.value.enabled) return
        _state.update { it.copy(state = WifiConnectionState.CONNECTING, failure = null) }
        sessionHost.stopWifi(restoreCable = false)
        if (localNetworkGranted()) sessionHost.startWifi(current)
        else _state.update { it.copy(state = WifiConnectionState.PAUSED, failure = WifiFailure.PERMISSION_DENIED) }
    }

    // The offer decoder is only reachable after canStartPairing() has refused every API level below 28.
    @SuppressLint("NewApi")
    fun pairFromQr(qr: ByteArray) {
        if (!canStartPairing()) return
        val offer = try { WifiPairingOffer.decodeSupported(qr) } catch (_: InvalidPairingQr) { return foreignQr() }
        runPairing { progress -> pairer.pairWithOffer(offer, deviceLabel(), progress) }
    }

    fun pairFromCode(text: String) {
        if (!canStartPairing()) return
        val fields = try { WifiTypedCode.decode(text) } catch (_: InvalidPairingCode) { return invalidInput() }
        runPairing { progress -> pairer.pairWithCode(fields, deviceLabel(), progress) }
    }

    private fun canStartPairing(): Boolean {
        if (apiLevel < 28) { _state.update { it.copy(pairing = WifiPairingUi.Failed(PairingFailureReason.UNSUPPORTED_PLATFORM)) }; return false }
        if (pairingJob?.isActive == true) return false
        return true
    }

    private fun invalidInput() { _state.update { it.copy(pairing = WifiPairingUi.InvalidInput) } }
    private fun foreignQr() { _state.update { if (it.pairing is WifiPairingUi.Working) it else it.copy(pairing = WifiPairingUi.ForeignQr) } }

    private fun runPairing(block: (progress: (PairingProgress) -> Unit) -> WifiPairedHost) {
        _state.update { it.copy(pairing = WifiPairingUi.Working(PairingStage.CONNECTING, null)) }
        pairingJob = scope.launch(io) {
            try {
                forgetJob?.join()
                // A replaced pairing must not keep a live session to the old identity.
                if (_state.value.enabled) sessionHost.stopWifi(restoreCable = false)
                val paired = block { progress -> _state.update { it.copy(pairing = WifiPairingUi.Working(progress.stage, progress.fingerprint)) } }
                host = paired
                writeMode(true)
                _state.update { it.copy(enabled = true, paired = paired.summary(), state = WifiConnectionState.CONNECTING, failure = null, pairing = WifiPairingUi.Done) }
                // The certificate is valid for the first connection for only 60 s on the PC: connect right now.
                startIfAllowed()
            } catch (error: WifiPairingException) {
                host = loadHost()
                restoreAfterFailedPairing()
                // Cancelling is the user's own choice, not a failure: back to the start of the pairing screen.
                _state.update { it.copy(pairing = if (error.reason == PairingFailureReason.CANCELLED) WifiPairingUi.Idle else WifiPairingUi.Failed(error.reason)) }
            } catch (error: Exception) {
                log("Pairing failed unexpectedly: ${error::class.java.simpleName}")
                host = loadHost()
                restoreAfterFailedPairing()
                _state.update { it.copy(pairing = WifiPairingUi.Failed(PairingFailureReason.PROTOCOL)) }
            }
        }
    }

    /** A failed pairing leaves the previous pairing (if it survived) and the user's carrier choice as they were. */
    private fun restoreAfterFailedPairing() {
        val current = host
        if (current != null && modeStore.enabled && localNetworkGranted()) sessionHost.startWifi(current)
        else if (modeStore.enabled && current == null) writeMode(false)
        _state.update { it.copy(enabled = modeStore.enabled && current != null, paired = current?.summary(),
            state = if (current == null) WifiConnectionState.NOT_PAIRED else if (modeStore.enabled) WifiConnectionState.CONNECTING else WifiConnectionState.OFF) }
    }

    fun cancelPairing() { pairer.cancel() }

    /** Closing the pairing screen mid-wait abandons the request instead of leaving a half-finished pairing. */
    fun cancelPairingIfWorking() { if (_state.value.pairing is WifiPairingUi.Working) pairer.cancel() }

    fun dismissPairingResult() { _state.update { if (it.pairing is WifiPairingUi.Working) it else it.copy(pairing = WifiPairingUi.Idle) } }

    /** "Esquecer este PC": local only. It does not tell the PC; revoking there is a separate act on the Control Center. */
    fun forget() {
        pairer.cancel()
        sessionHost.stopWifi()
        val current = host
        host = null
        writeMode(false)
        _state.value = initialState()
        // The Keystore is touched off the main thread; a new pairing waits for this to finish so it is not wiped by it.
        forgetJob = scope.launch(io) {
            current?.let { runCatching { identity.forget(it.hostId) } }
            runCatching { identity.hostIds().forEach { runCatching { identity.forget(it) } } }
        }
    }
}