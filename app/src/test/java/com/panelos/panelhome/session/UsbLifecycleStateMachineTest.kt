package com.panelos.panelhome.session

import com.panelos.panelhome.data.transport.UsbFailureKind
import com.panelos.panelhome.domain.model.ConnectionPhase
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The cable-pull campaign, minus the cable.
 *
 * Every case here is one the panel can only otherwise reach by someone standing
 * at the bench with a USB plug, including the ones that need two physical events
 * to land in the wrong order. The invariant the whole suite exists to protect is
 * checked after every single event: **at most one accessory is open at a time,
 * and a detach never opens one**.
 */
class UsbLifecycleStateMachineTest {
    @Test
    fun `service start without an accessory waits instead of opening one`() {
        val panel = Panel()

        panel.dispatch(UsbLifecycleEvent.ServiceStarted(accessory = null, permitted = false))

        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(0, panel.openCount)
        assertEquals(ConnectionPhase.WAITING_FOR_USB, panel.machine.phase)
    }

    @Test
    fun `an attach opens, handshakes and connects`() {
        val panel = Panel()
        panel.startWithoutAccessory()

        panel.attach()
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
        assertEquals(UsbRecoveryState.TRANSPORT_HANDSHAKING, panel.machine.state)
        panel.dispatch(
            UsbLifecycleEvent.TransportHandshakeSucceeded(panel.generation, "AB12CD34"),
        )
        assertEquals(UsbRecoveryState.APPLICATION_HANDSHAKING, panel.machine.state)
        panel.dispatch(UsbLifecycleEvent.ApplicationHandshakeSucceeded(panel.generation))

        assertEquals(UsbRecoveryState.CONNECTED, panel.machine.state)
        assertEquals(ConnectionPhase.CONNECTED, panel.machine.phase)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a detach closes the current generation exactly once and then waits`() {
        val panel = Panel().connected()
        val connectedGeneration = panel.generation

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))

        assertEquals(UsbRecoveryState.CLOSING_ACCESSORY, panel.machine.state)
        assertEquals(listOf(connectedGeneration), panel.closedGenerations)
        panel.dispatch(UsbLifecycleEvent.SessionClosed(connectedGeneration))
        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(listOf(connectedGeneration), panel.closedGenerations)
        assertEquals(1, panel.machine.metrics.usbDescriptorCloses)
    }

    @Test
    fun `a detach never asks for an accessory to be opened`() {
        val panel = Panel().connected()
        val opensBefore = panel.openCount

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        panel.dispatch(UsbLifecycleEvent.SessionClosed(panel.machine.generation))
        // Whatever a retry timer would have done, it must not happen here.
        panel.dispatch(UsbLifecycleEvent.RetryTimerElapsed(panel.machine.generation, attempt = 1))

        assertEquals(opensBefore, panel.openCount)
        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(ConnectionPhase.WAITING_FOR_USB, panel.machine.phase)
    }

    @Test
    fun `a replug opens a new generation strictly greater than the old one`() {
        val panel = Panel().connected()
        val first = panel.generation

        panel.detachAndSettle()
        panel.clock += 5_000
        panel.attach()

        assertTrue(panel.generation > first)
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        assertEquals(2, panel.openCount)
    }

    @Test
    fun `a host restart supersedes the session without touching the descriptor`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(UsbLifecycleEvent.HostSessionSuperseded(generation, "99FF0011"))

        assertEquals(UsbRecoveryState.APPLICATION_HANDSHAKING, panel.machine.state)
        assertTrue(panel.closedGenerations.isEmpty())
        assertEquals(1, panel.openCount)
        panel.dispatch(UsbLifecycleEvent.ApplicationHandshakeSucceeded(generation))
        assertEquals(UsbRecoveryState.CONNECTED, panel.machine.state)
        assertEquals(generation, panel.generation)
        assertEquals(1, panel.machine.metrics.usbSessionSupersedes)
    }

    @Test
    fun `a late report from a replaced generation cannot touch its successor`() {
        val panel = Panel().connected()
        val dead = panel.generation
        panel.detachAndSettle()
        panel.clock += 5_000
        panel.attach()
        panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
        val live = panel.generation

        panel.dispatch(
            UsbLifecycleEvent.PhysicalConnectionLost(
                generation = dead,
                kind = UsbFailureKind.UNKNOWN_TRANSPORT_FAILURE,
                detail = "read failed after the cable came out",
            ),
        )
        panel.dispatch(UsbLifecycleEvent.TransportHandshakeSucceeded(dead, "DEADBEEF"))

        assertEquals(live, panel.generation)
        assertEquals(UsbRecoveryState.TRANSPORT_HANDSHAKING, panel.machine.state)
        assertTrue(panel.machine.metrics.usbStaleGenerationEventsIgnored >= 2)
    }

    @Test
    fun `open failures back off in a bounded burst and then probe rarely`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.attach()

        val delays = mutableListOf<Long>()
        repeat(UsbLifecycleTimings.MAX_CONSECUTIVE_OPEN_ATTEMPTS) {
            val decision = panel.dispatch(
                UsbLifecycleEvent.OpenFailed(
                    generation = panel.generation,
                    kind = UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                    detail = "Android refused to open the accessory",
                ),
            )
            val retry = decision.actions
                .filterIsInstance<UsbLifecycleAction.ScheduleRetry>()
                .single()
            delays += retry.delayMillis
            if (retry.delayMillis == UsbLifecycleTimings.IDLE_REPROBE_MILLIS) return@repeat
            panel.dispatch(UsbLifecycleEvent.RetryTimerElapsed(panel.generation, retry.attempt))
        }

        // The burst is bounded, and what follows it is one attempt every 30 s -
        // not a give-up, because an accessory that refuses now can become
        // openable a minute later, and not a spin either.
        assertEquals(
            listOf(250L, 500L, 1_000L, 2_000L, 4_000L, UsbLifecycleTimings.IDLE_REPROBE_MILLIS),
            delays,
        )
        assertEquals(UsbRecoveryState.BACKOFF, panel.machine.state)
        assertEquals(
            UsbLifecycleTimings.MAX_CONSECUTIVE_OPEN_ATTEMPTS.toLong(),
            panel.machine.metrics.usbAccessoryOpenAttempts,
        )
    }

    @Test
    fun `a detach cancels the idle probe and leaves nothing scheduled`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.attach()
        panel.dispatch(
            UsbLifecycleEvent.OpenFailed(
                generation = panel.generation,
                kind = UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                detail = "Android refused to open the accessory",
            ),
        )
        assertEquals(UsbRecoveryState.BACKOFF, panel.machine.state)

        val decision = panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))

        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertTrue(UsbLifecycleAction.CancelTimers in decision.actions)
        assertTrue(
            decision.actions.none { it is UsbLifecycleAction.ScheduleRetry },
        )
    }

    @Test
    fun `repeated session recycles reopen at once and only then slow down`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.attach()

        val paced = mutableListOf<Long>()
        repeat(6) {
            panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
            val generation = panel.generation
            val decision = panel.dispatch(
                // A deadline, not a mid-frame loss: nothing here suggests the
                // cable moved, so no detach grace applies.
                UsbLifecycleEvent.HandshakeDeadlineElapsed(generation),
            )
            assertTrue(decision.actions.any { it is UsbLifecycleAction.CloseSession })
            val closed = panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))
            val retry = closed.actions.filterIsInstance<UsbLifecycleAction.ScheduleRetry>()
                .singleOrNull()
            if (retry != null) {
                paced += retry.delayMillis
                panel.dispatch(
                    UsbLifecycleEvent.RetryTimerElapsed(panel.generation, retry.attempt),
                )
            }
        }

        // A replug must reconnect immediately, so the first recycles are free.
        // Past that the panel is next to a host that is simply not running, and
        // every further reopen is another cycle on the descriptor for nothing.
        assertEquals(listOf(250L, 500L, 1_000L), paced)
    }

    @Test
    fun `a session lost mid-frame waits for a possible detach before reopening`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(
            UsbLifecycleEvent.PhysicalConnectionLost(
                generation = generation,
                kind = UsbFailureKind.DESCRIPTOR_CLOSED,
                detail = "Physical USB disconnected mid-frame",
            ),
        )
        val closed = panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))

        // Android confirms a cable pull with DETACHED about a second after the
        // session dies. Reopening inside that window opens an accessory the
        // kernel is tearing down, so the reopen deliberately loses that race.
        val retry = closed.actions.filterIsInstance<UsbLifecycleAction.ScheduleRetry>().single()
        assertEquals(UsbLifecycleTimings.DETACH_GRACE_MILLIS, retry.delayMillis)
        assertEquals(UsbRecoveryState.BACKOFF, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a detach inside the grace window cancels the reopen entirely`() {
        val panel = Panel().connected()
        val generation = panel.generation
        panel.dispatch(
            UsbLifecycleEvent.PhysicalConnectionLost(
                generation = generation,
                kind = UsbFailureKind.DESCRIPTOR_CLOSED,
                detail = "Physical USB disconnected mid-frame",
            ),
        )
        panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))

        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a handshake that never completes releases the descriptor and stays recoverable`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.attach()
        panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
        val stuck = panel.generation

        panel.dispatch(UsbLifecycleEvent.HandshakeDeadlineElapsed(stuck))
        assertEquals(UsbRecoveryState.RECOVERING_SESSION, panel.machine.state)
        assertEquals(listOf(stuck), panel.closedGenerations)

        panel.dispatch(UsbLifecycleEvent.SessionClosed(stuck))
        // The cable is still in, so the correct response is another attempt on a
        // fresh descriptor - not waiting for an attach that will never come.
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        assertEquals(2, panel.openCount)
        assertEquals(1, panel.machine.metrics.usbHandshakeTimeouts)
    }

    @Test
    fun `a lost panel protocol session keeps the carrier and is bounded by the deadline`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(
            UsbLifecycleEvent.ApplicationHandshakeFailed(generation, "PanelHost closed"),
        )

        // PLMX is fine; only the JSONL session ended. Closing here would turn a
        // host restart into a physical recovery nobody can complete.
        assertEquals(UsbRecoveryState.APPLICATION_HANDSHAKING, panel.machine.state)
        assertTrue(panel.closedGenerations.isEmpty())

        panel.dispatch(UsbLifecycleEvent.HandshakeDeadlineElapsed(generation))
        assertEquals(UsbRecoveryState.RECOVERING_SESSION, panel.machine.state)
        assertEquals(listOf(generation), panel.closedGenerations)
    }

    @Test
    fun `a detach during any live state closes exactly one session`() {
        val states = listOf<(Panel) -> Unit>(
            { it.startWithoutAccessory(); it.attach() },
            {
                it.startWithoutAccessory(); it.attach()
                it.dispatch(UsbLifecycleEvent.OpenSucceeded(it.generation))
            },
            {
                it.startWithoutAccessory(); it.attach()
                it.dispatch(UsbLifecycleEvent.OpenSucceeded(it.generation))
                it.dispatch(
                    UsbLifecycleEvent.TransportHandshakeSucceeded(it.generation, "AA00BB11"),
                )
            },
            { it.connected() },
        )
        for (reach in states) {
            val panel = Panel()
            reach(panel)
            val generation = panel.generation

            panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
            panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))

            assertEquals(listOf(generation), panel.closedGenerations)
            assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
            assertEquals(1, panel.openCount)
        }
    }

    @Test
    fun `a duplicate attach does not produce a second session`() {
        val panel = Panel()
        panel.startWithoutAccessory()

        panel.attach()
        panel.attach()
        panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
        panel.attach()

        assertEquals(1, panel.openCount)
        assertEquals(UsbRecoveryState.TRANSPORT_HANDSHAKING, panel.machine.state)
    }

    @Test
    fun `a duplicate detach is idempotent`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))
        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(null))

        assertEquals(listOf(generation), panel.closedGenerations)
        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a rapid replug never opens before the previous close reports back`() {
        val panel = Panel().connected()
        val old = panel.generation

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        panel.clock += 1_000
        // The cable is already back before the descriptor has been released.
        panel.attach()
        assertEquals(1, panel.openCount)

        panel.dispatch(UsbLifecycleEvent.SessionClosed(old))

        assertEquals(2, panel.openCount)
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        assertTrue(panel.generation > old)
    }

    @Test
    fun `a detach that lands during a recovery cancels the reopen`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(
            UsbLifecycleEvent.PhysicalConnectionLost(
                generation = generation,
                kind = UsbFailureKind.UNKNOWN_TRANSPORT_FAILURE,
                detail = "read failed",
            ),
        )
        assertEquals(UsbRecoveryState.RECOVERING_SESSION, panel.machine.state)

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))

        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `an attach after the service is stopping never starts a session`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(UsbLifecycleEvent.ServiceStopping)
        panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))
        assertEquals(UsbRecoveryState.STOPPED, panel.machine.state)

        panel.clock += 5_000
        panel.attach()

        assertEquals(UsbRecoveryState.STOPPED, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a detach for a different accessory is ignored`() {
        val panel = Panel().connected()
        val other = AccessoryIdentity.of("Other", "Thing", "9", "zzz")

        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(other))

        assertEquals(UsbRecoveryState.CONNECTED, panel.machine.state)
        assertTrue(panel.closedGenerations.isEmpty())
    }

    @Test
    fun `an unpermitted accessory asks once and opens after the grant`() {
        val panel = Panel()
        panel.startWithoutAccessory()

        panel.dispatch(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = false))
        panel.clock += 1_000
        panel.dispatch(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = false))

        assertEquals(UsbRecoveryState.WAITING_FOR_PERMISSION, panel.machine.state)
        assertEquals(1, panel.permissionRequests)
        assertEquals(0, panel.openCount)

        panel.dispatch(UsbLifecycleEvent.PermissionResult(ACCESSORY, granted = true))
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        assertEquals(1, panel.openCount)
    }

    @Test
    fun `a denied permission leaves the panel waiting rather than retrying`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.dispatch(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = false))

        panel.dispatch(UsbLifecycleEvent.PermissionResult(ACCESSORY, granted = false))

        assertEquals(UsbRecoveryState.WAITING_FOR_ACCESSORY, panel.machine.state)
        assertEquals(0, panel.openCount)
        assertEquals(1, panel.machine.metrics.usbPermissionDenials)
    }

    @Test
    fun `attach to connected duration is measured and reported`() {
        val panel = Panel()
        panel.startWithoutAccessory()
        panel.attach()
        panel.clock += 1_400
        panel.dispatch(UsbLifecycleEvent.OpenSucceeded(panel.generation))
        panel.dispatch(
            UsbLifecycleEvent.TransportHandshakeSucceeded(panel.generation, "AB12CD34"),
        )
        panel.clock += 600
        panel.dispatch(UsbLifecycleEvent.ApplicationHandshakeSucceeded(panel.generation))

        assertEquals(2_000L, panel.machine.metrics.attachToConnectedMillis)
    }

    @Test
    fun `no log line carries a full session nonce or an accessory serial`() {
        val panel = Panel().connected()
        panel.dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))

        assertFalse(panel.logs.any { SECRET_SERIAL in it })
        assertTrue(panel.logs.all { it.startsWith("[UsbRecovery] generation=") })
    }

    @Test
    fun `the carrier can be handed to ADB and taken back`() {
        val panel = Panel().connected()
        val generation = panel.generation

        panel.dispatch(UsbLifecycleEvent.CarrierDisabled)
        panel.dispatch(UsbLifecycleEvent.SessionClosed(generation))
        assertEquals(UsbRecoveryState.DISABLED, panel.machine.state)
        assertEquals(ConnectionPhase.DISABLED, panel.machine.phase)

        panel.clock += 5_000
        panel.attach()
        assertEquals(UsbRecoveryState.OPENING_ACCESSORY, panel.machine.state)
        assertNotEquals(generation, panel.generation)
    }

    /**
     * Drives the reducer and enforces the two invariants that matter physically,
     * after every event: an open is only ever issued when nothing is open, and a
     * close is only ever issued for the generation that is actually live.
     */
    private class Panel {
        var clock = 1_000L
        val machine = UsbLifecycleStateMachine(clock = { clock })
        val logs = mutableListOf<String>()
        val closedGenerations = mutableListOf<Long>()
        var openCount = 0
            private set
        var permissionRequests = 0
            private set

        private var openGeneration: Long? = null

        val generation: Long get() = machine.generation

        fun dispatch(event: UsbLifecycleEvent): UsbLifecycleDecision {
            // A refused open leaves nothing open. The port only reports Failed
            // when it is holding no descriptor, so the harness models that too.
            if (event is UsbLifecycleEvent.OpenFailed) openGeneration = null
            val decision = machine.dispatch(event)
            logs += decision.logs
            for (action in decision.actions) {
                when (action) {
                    is UsbLifecycleAction.OpenAccessory -> {
                        assertEquals(
                            "an accessory was opened while one was still open",
                            null,
                            openGeneration,
                        )
                        openGeneration = action.generation
                        openCount++
                    }
                    is UsbLifecycleAction.CloseSession -> {
                        assertEquals(
                            "a close was issued for a generation that was not open",
                            openGeneration,
                            action.generation,
                        )
                        openGeneration = null
                        closedGenerations += action.generation
                    }
                    is UsbLifecycleAction.RequestPermission -> permissionRequests++
                    else -> Unit
                }
            }
            return decision
        }

        fun startWithoutAccessory() {
            dispatch(UsbLifecycleEvent.ServiceStarted(accessory = null, permitted = false))
        }

        fun attach() {
            dispatch(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = true))
        }

        fun detachAndSettle() {
            val generation = machine.generation
            dispatch(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
            dispatch(UsbLifecycleEvent.SessionClosed(generation))
        }

        fun connected(): Panel {
            startWithoutAccessory()
            attach()
            dispatch(UsbLifecycleEvent.OpenSucceeded(generation))
            dispatch(UsbLifecycleEvent.TransportHandshakeSucceeded(generation, "AB12CD34"))
            dispatch(UsbLifecycleEvent.ApplicationHandshakeSucceeded(generation))
            check(machine.state == UsbRecoveryState.CONNECTED)
            closedGenerations.clear()
            return this
        }
    }

    private companion object {
        const val SECRET_SERIAL = "PANEL-SERIAL-0001"
        val ACCESSORY = AccessoryIdentity.of("PanelOS", "PanelHost", "1", SECRET_SERIAL)
    }
}
