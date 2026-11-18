package com.panelos.panelhome.ui.wifi

import com.panelos.panelhome.data.transport.WifiFailure
import com.panelos.panelhome.data.wifi.PairingFailureReason
import com.panelos.panelhome.data.wifi.PairingStage
import com.panelos.panelhome.session.WifiConnectionState
import com.panelos.panelhome.session.WifiPairingUi
import com.panelos.panelhome.session.WifiUiState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class WifiPresentationTest {
    private fun state(wifiOn: Boolean) = WifiUiState(true, wifiOn, null,
        if (wifiOn) WifiConnectionState.CONNECTED else WifiConnectionState.OFF, null, WifiPairingUi.Idle)

    @Test fun everyStateFailureStageAndPairingReasonHasItsOwnText() {
        for (values in listOf(
            WifiConnectionState.entries.map(WifiPresentation::stateText),
            WifiFailure.entries.map(WifiPresentation::failureText),
            PairingStage.entries.map(WifiPresentation::stageText),
            PairingFailureReason.entries.mapNotNull(WifiPresentation::pairingFailure).map { it.title * 1000003 + it.body },
        )) {
            assertEquals("each case says something different", values.size, values.toSet().size)
            assert(values.all { it != 0 })
        }
    }

    @Test fun theCarrierRowNamesWifiOnlyWhileTheWifiLinkIsUp() {
        assertNotEquals(WifiPresentation.carrierText(state(true)), WifiPresentation.carrierText(state(false)))
        assertEquals(com.panelos.panelhome.R.string.wifi_carrier_wifi, WifiPresentation.carrierText(state(true)))
        assertEquals(com.panelos.panelhome.R.string.wifi_carrier_cable, WifiPresentation.carrierText(state(false)))
    }

    @Test fun theFingerprintIsShownWholeInEightGroupsOfEight() {
        val hex = "0123456789abcdef".repeat(4)
        val grouped = WifiPresentation.groupFingerprint(hex)
        assertEquals(8, grouped.split(' ').size)
        assertEquals(hex.uppercase(), grouped.replace(" ", ""))
    }

    @Test fun everyRealFailureTellsWhatHappenedAndWhatToDoAndCancellingIsNotAFailure() {
        assertNull("cancelling goes back to the start with no failure screen", WifiPresentation.pairingFailure(PairingFailureReason.CANCELLED))
        for (reason in PairingFailureReason.entries - PairingFailureReason.CANCELLED) {
            val content = WifiPresentation.pairingFailure(reason)!!
            assertNotEquals(content.title, content.body)
        }
        assertEquals(com.panelos.panelhome.R.string.wifi_pair_failed_title_expired, WifiPresentation.pairingFailure(PairingFailureReason.OFFER_EXPIRED)!!.title)
        assertFalse("nothing to retry on an Android that cannot do it", WifiPresentation.pairingFailure(PairingFailureReason.UNSUPPORTED_PLATFORM)!!.canRetry)
        assertTrue(WifiPresentation.pairingFailure(PairingFailureReason.TIMED_OUT)!!.canRetry)
    }

    @Test fun theFingerprintIsTwoRowsOfFourUppercaseBlocksOfEight() {
        val rows = WifiPresentation.fingerprintRows("a1b2c3d4e5f607189abcdef012345678" + "87654321" + "0fedcba9" + "8170f6e5" + "d4c3b2a1")
        assertEquals(listOf(4, 4), rows.map { it.size })
        assertEquals(listOf("A1B2C3D4", "E5F60718", "9ABCDEF0", "12345678"), rows[0])
        assertEquals("D4C3B2A1", rows[1][3])
        assertTrue(rows.flatten().all { it.length == 8 && it == it.uppercase() })
    }

    @Test fun theFourStepsFollowThePairingStagesAndWaitingIsTheHumanOne() {
        assertEquals(4, WifiPresentation.stepLabels.size)
        assertEquals(listOf(0, 1, 1, 2, 3), PairingStage.entries.map(WifiPresentation::stepIndex))
        assertEquals("0:47", WifiPresentation.countdown(47)); assertEquals("1:00", WifiPresentation.countdown(60)); assertEquals("0:00", WifiPresentation.countdown(-3))
    }
}
