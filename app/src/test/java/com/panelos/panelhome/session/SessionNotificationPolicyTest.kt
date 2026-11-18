package com.panelos.panelhome.session

import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionPhase
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

class SessionNotificationPolicyTest {
    private fun wifi(enabled: Boolean, state: WifiConnectionState) = WifiUiState(true, enabled, null, state, null, WifiPairingUi.Idle)

    @Test fun theCableKeepsItsOwnTextsAndNamesTheCableAsThePath() {
        for (phase in ConnectionPhase.entries) {
            val content = SessionNotificationPolicy.content(phase, wifi(false, WifiConnectionState.OFF), everConnected = false)
            assertEquals(R.string.session_notification_carrier_cable, content.subtext)
        }
        assertEquals(R.string.session_notification_connected_cable, SessionNotificationPolicy.content(ConnectionPhase.CONNECTED, null, false).text)
        assertEquals(R.string.session_notification_waiting_usb, SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, null, false).text)
        assertEquals(R.string.session_notification_reconnecting, SessionNotificationPolicy.content(ConnectionPhase.RECOVERING, null, true).text)
        assertEquals(R.string.session_notification_waiting, SessionNotificationPolicy.content(ConnectionPhase.DISCONNECTED, null, false).text)
    }

    @Test fun whenWifiOwnsTheCarrierTheTextTalksAboutWifiNotUsb() {
        val connected = SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, wifi(true, WifiConnectionState.CONNECTED), false)
        assertEquals(R.string.session_notification_connected_wifi, connected.text)
        assertEquals(R.string.session_notification_carrier_wifi, connected.subtext)
        for (state in listOf(WifiConnectionState.CONNECTING, WifiConnectionState.RETRYING))
            assertEquals(R.string.session_notification_wifi_searching, SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, wifi(true, state), false).text)
        assertEquals(R.string.session_notification_wifi_paused, SessionNotificationPolicy.content(ConnectionPhase.DISCONNECTED, wifi(true, WifiConnectionState.PAUSED), false).text)
    }

    @Test fun theSamePhaseOnAnotherPathIsAnotherNotification() {
        // The bug this guards: the phase did not change when Wi-Fi was switched on or off, so nothing was redrawn.
        val cable = SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, wifi(false, WifiConnectionState.OFF), false)
        val onWifi = SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, wifi(true, WifiConnectionState.CONNECTING), false)
        assertNotEquals(cable, onWifi)
    }
}