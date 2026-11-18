package com.panelos.panelhome.session

import androidx.annotation.StringRes
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionPhase

/** The two lines the foreground notification shows: the status text and the carrier in use ("Wi-Fi" / "Cabo USB"). */
internal data class SessionNotificationContent(@StringRes val text: Int, @StringRes val subtext: Int)

/**
 * What the notification says, from the cable's phase and Wi-Fi's own state. Nothing here is the PC's name, address or
 * fingerprint: the notification can be read on the lock screen. The result is compared as a whole, so it is rebuilt when the
 * phase OR the path (cable/Wi-Fi) changes, even if the phase stays the same.
 */
internal object SessionNotificationPolicy {
    fun content(phase: ConnectionPhase, wifi: WifiUiState?, everConnected: Boolean): SessionNotificationContent {
        if (wifi?.enabled == true) {
            val text = when (wifi.state) {
                WifiConnectionState.CONNECTED -> R.string.session_notification_connected_wifi
                WifiConnectionState.PAUSED -> R.string.session_notification_wifi_paused
                else -> R.string.session_notification_wifi_searching
            }
            return SessionNotificationContent(text, R.string.session_notification_carrier_wifi)
        }
        val text = when (phase) {
            ConnectionPhase.CONNECTED -> R.string.session_notification_connected_cable
            ConnectionPhase.WAITING_FOR_USB -> R.string.session_notification_waiting_usb
            ConnectionPhase.OPENING_USB,
            ConnectionPhase.TRANSPORT_HANDSHAKE,
            ConnectionPhase.APPLICATION_HANDSHAKE,
            ConnectionPhase.RECOVERING,
            -> R.string.session_notification_reconnecting
            ConnectionPhase.DISABLED,
            ConnectionPhase.DISCONNECTED,
            -> if (everConnected) R.string.session_notification_reconnecting else R.string.session_notification_waiting
        }
        return SessionNotificationContent(text, R.string.session_notification_carrier_cable)
    }
}