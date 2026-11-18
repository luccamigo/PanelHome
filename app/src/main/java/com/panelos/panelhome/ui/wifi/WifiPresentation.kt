package com.panelos.panelhome.ui.wifi

import androidx.annotation.StringRes
import com.panelos.panelhome.R
import com.panelos.panelhome.data.transport.WifiFailure
import com.panelos.panelhome.data.wifi.PairingFailureReason
import com.panelos.panelhome.data.wifi.PairingStage
import com.panelos.panelhome.session.WifiConnectionState
import com.panelos.panelhome.session.WifiUiState

/** What the screens say about Wi-Fi. A pure mapping from state to words so every state and reason is checked by a test. */
internal object WifiPresentation {
    @StringRes fun stateText(state: WifiConnectionState): Int = when (state) {
        WifiConnectionState.OFF -> R.string.wifi_state_off
        WifiConnectionState.NOT_PAIRED -> R.string.wifi_state_not_paired
        WifiConnectionState.UNSUPPORTED -> R.string.wifi_state_unsupported
        WifiConnectionState.CONNECTING -> R.string.wifi_state_connecting
        WifiConnectionState.CONNECTED -> R.string.wifi_state_connected
        WifiConnectionState.RETRYING -> R.string.wifi_state_retrying
        WifiConnectionState.PAUSED -> R.string.wifi_state_paused
    }

    @StringRes fun failureText(failure: WifiFailure): Int = when (failure) {
        WifiFailure.UNSUPPORTED_PLATFORM -> R.string.wifi_failure_unsupported
        WifiFailure.PERMISSION_DENIED -> R.string.wifi_failure_permission
        WifiFailure.NOT_PAIRED -> R.string.wifi_failure_not_paired
        WifiFailure.UNREACHABLE -> R.string.wifi_failure_unreachable
        WifiFailure.TIMEOUT -> R.string.wifi_failure_timeout
        WifiFailure.IDENTITY_MISMATCH -> R.string.wifi_failure_identity
        WifiFailure.REFUSED_BY_PC -> R.string.wifi_failure_refused
        WifiFailure.PROTOCOL -> R.string.wifi_failure_protocol
    }

    @StringRes fun stageText(stage: PairingStage): Int = when (stage) {
        PairingStage.CONNECTING -> R.string.wifi_pair_stage_connecting
        PairingStage.VERIFYING_PC -> R.string.wifi_pair_stage_verifying
        PairingStage.REQUESTING -> R.string.wifi_pair_stage_requesting
        PairingStage.WAITING_FOR_APPROVAL -> R.string.wifi_pair_stage_waiting
        PairingStage.INSTALLING -> R.string.wifi_pair_stage_installing
    }

    /** A failure screen: what happened (title), what to do (body), and whether trying again from the start makes sense. */
    data class PairFailureContent(@StringRes val title: Int, @StringRes val body: Int, val canRetry: Boolean)

    /** Null for CANCELLED: the user asked for it, so it is not a failure and no failure screen is shown. */
    fun pairingFailure(reason: PairingFailureReason): PairFailureContent? = when (reason) {
        PairingFailureReason.UNSUPPORTED_PLATFORM -> PairFailureContent(R.string.wifi_pair_failed_title_unsupported, R.string.wifi_pair_failed_unsupported, false)
        PairingFailureReason.UNREACHABLE -> PairFailureContent(R.string.wifi_pair_failed_title_unreachable, R.string.wifi_pair_failed_unreachable, true)
        PairingFailureReason.IDENTITY_MISMATCH -> PairFailureContent(R.string.wifi_pair_failed_title_identity, R.string.wifi_pair_failed_identity, true)
        PairingFailureReason.REFUSED -> PairFailureContent(R.string.wifi_pair_failed_title_refused, R.string.wifi_pair_failed_refused, true)
        PairingFailureReason.TIMED_OUT -> PairFailureContent(R.string.wifi_pair_failed_title_timeout, R.string.wifi_pair_failed_timeout, true)
        PairingFailureReason.PROTOCOL -> PairFailureContent(R.string.wifi_pair_failed_title_protocol, R.string.wifi_pair_failed_protocol, true)
        PairingFailureReason.OFFER_EXPIRED -> PairFailureContent(R.string.wifi_pair_failed_title_expired, R.string.wifi_pair_failed_expired, true)
        PairingFailureReason.CANCELLED -> null
    }

    /** The four visible steps: 0 Conectar, 1 Conferir o PC, 2 Aprovar no PC, 3 Concluir. Requesting still belongs to checking the PC. */
    val stepLabels: List<Int> = listOf(
        R.string.wifi_pair_step_connect, R.string.wifi_pair_step_check, R.string.wifi_pair_step_approve, R.string.wifi_pair_step_finish,
    )
    fun stepIndex(stage: PairingStage): Int = when (stage) {
        PairingStage.CONNECTING -> 0
        PairingStage.VERIFYING_PC, PairingStage.REQUESTING -> 1
        PairingStage.WAITING_FOR_APPROVAL -> 2
        PairingStage.INSTALLING -> 3
    }

    /** m:ss, for the approval countdown. */
    fun countdown(seconds: Int): String = "%d:%02d".format(maxOf(0, seconds) / 60, maxOf(0, seconds) % 60)

    /** The fingerprint as two rows of four blocks of eight, uppercase, so both screens break the lines in the same place. */
    fun fingerprintRows(hex: String): List<List<String>> = hex.uppercase().chunked(8).chunked(4)

    /** Which carrier the panel is on right now, for the "Caminho em uso" row. */
    @StringRes fun carrierText(state: WifiUiState): Int =
        if (state.usingWifi) R.string.wifi_carrier_wifi else R.string.wifi_carrier_cable

    /** The whole 256-bit fingerprint in eight groups of eight, never shortened. */
    fun groupFingerprint(hex: String): String = hex.uppercase().chunked(8).joinToString(" ")

    /** Seconds the PC keeps a request waiting for its operator; the tablet only estimates the countdown. */
    const val APPROVAL_SECONDS = 60
}