package com.panelos.panelhome.session

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Service start decision, without a device: the cable path with no pairing, and Wi-Fi problems that must not touch it. */
class CarrierStartCoordinatorTest {
    private val events = mutableListOf<String>()
    private var owns = false

    private fun coordinator(restore: (suspend () -> Boolean)?) = CarrierStartCoordinator<String>(
        scope = CoroutineScope(Dispatchers.Unconfined),
        restoreWifi = restore,
        wifiOwnsCarrier = { owns },
        onWifiTookOver = { events += "wifi-took-over" },
        selectCable = { events += "cable:$it" },
        afterStart = { events += "after:$it" },
    )

    @Test fun withoutAPairingTheCablePathRunsExactlyOncePerStart() {
        val c = coordinator { false }
        c.start("start-1")
        assertEquals(listOf("cable:start-1", "after:start-1"), events)
        c.start("start-2")
        assertEquals("later starts reuse the decision", listOf("cable:start-1", "after:start-1", "cable:start-2", "after:start-2"), events)
        assertFalse(c.restoring)
    }

    @Test fun whenWifiIsUnavailableAtAllTheCableStillRuns() {
        coordinator(null).start("s")
        assertEquals(listOf("cable:s", "after:s"), events)
    }

    @Test fun aWifiRestoreThatThrowsFallsBackToTheCable() {
        coordinator { throw IllegalStateException("Keystore unavailable") }.start("s")
        assertEquals(listOf("cable:s", "after:s"), events)
    }

    @Test fun whenWifiTakesOverTheCableIsNotSelectedAndIsToldToStandDown() {
        val c = coordinator { owns = true; true }
        c.start("s")
        assertEquals(listOf("wifi-took-over", "after:s"), events)
    }

    @Test fun startsThatArriveWhileRestoringAreHandledInOrderAfterIt() {
        val gate = CompletableDeferred<Boolean>()
        val c = coordinator { gate.await() }
        c.start("first"); c.start("second")
        assertTrue("a cable attach in this window must not start a session", c.restoring)
        assertTrue(events.isEmpty())
        gate.complete(false)
        assertEquals(listOf("cable:first", "after:first", "cable:second", "after:second"), events)
        assertFalse(c.restoring)
    }
}