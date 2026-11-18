package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import kotlin.coroutines.resume
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.suspendCancellableCoroutine
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TouchControllerTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)

    @After
    fun tearDown() {
        scope.cancel()
    }

    @Test
    fun downIsImmediateMovesAreLatestWinsAndUpIsNeverDiscarded()
    {
        val gate = TouchManualWaitGate()
        val sent = mutableListOf<TouchCommand>()
        val throttler = throttler(gate, sent)

        throttler.submit(event(TouchCommandAction.DOWN, x = 0.1, y = 0.2))
        throttler.submit(event(TouchCommandAction.MOVE, x = 0.2, y = 0.3))
        throttler.submit(event(TouchCommandAction.MOVE, x = 0.4, y = 0.5))
        throttler.submit(event(TouchCommandAction.MOVE, x = 0.8, y = 0.9))

        assertEquals(listOf(TouchCommandAction.DOWN), sent.map(TouchCommand::action))
        assertEquals(listOf(DESKTOP_TOUCH_MOVE_INTERVAL_MILLIS), gate.requestedDelays)

        gate.release()

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.MOVE),
            sent.map(TouchCommand::action),
        )
        assertEquals(0.8, sent.last().x)
        assertEquals(0.9, sent.last().y)

        throttler.submit(event(TouchCommandAction.UP, x = 0.9, y = 1.0))

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.MOVE, TouchCommandAction.UP),
            sent.map(TouchCommand::action),
        )
        assertFalse(gate.hasPendingWait)
        assertEquals(DesktopTouchMetrics(touchQueued = 3, moveCoalesced = 2), throttler.metrics())
    }

    @Test
    fun upDropsPendingMoveButCarriesItsFinalCoordinate()
    {
        val gate = TouchManualWaitGate()
        val sent = mutableListOf<TouchCommand>()
        val throttler = throttler(gate, sent)
        throttler.submit(event(TouchCommandAction.DOWN, x = 0.1, y = 0.1))
        throttler.submit(event(TouchCommandAction.MOVE, x = 0.3, y = 0.3))

        throttler.submit(event(TouchCommandAction.UP, x = 0.7, y = 0.8))

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.UP),
            sent.map(TouchCommand::action),
        )
        assertEquals(0.7, sent.last().x)
        assertEquals(0.8, sent.last().y)
        assertFalse(gate.hasPendingWait)
    }

    @Test
    fun cancelIsImmediateAndStaleGenerationCannotChangeState()
    {
        val gate = TouchManualWaitGate()
        val sent = mutableListOf<TouchCommand>()
        val throttler = throttler(gate, sent)
        throttler.submit(event(TouchCommandAction.DOWN, generation = 3))
        throttler.submit(event(TouchCommandAction.MOVE, generation = 4))
        throttler.submit(event(TouchCommandAction.UP, generation = 4))

        throttler.cancelActive()

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.CANCEL),
            sent.map(TouchCommand::action),
        )
        assertTrue(sent.all { it.generation == 3L })
    }

    @Test
    fun duplicateDownAndOrphanMoveOrUpAreIgnored()
    {
        val gate = TouchManualWaitGate()
        val sent = mutableListOf<TouchCommand>()
        val throttler = throttler(gate, sent)

        throttler.submit(event(TouchCommandAction.MOVE))
        throttler.submit(event(TouchCommandAction.UP))
        throttler.submit(event(TouchCommandAction.DOWN, x = 0.1, y = 0.1))
        throttler.submit(event(TouchCommandAction.DOWN, x = 0.2, y = 0.2))

        assertEquals(listOf(TouchCommandAction.DOWN), sent.map(TouchCommand::action))
        assertEquals(0.1, sent.single().x)
    }

    private fun throttler(
        gate: TouchManualWaitGate,
        sent: MutableList<TouchCommand>,
    ) = DesktopTouchCommandThrottler(
        scope = scope,
        wait = gate::wait,
        onSend = sent::add,
    )

    private fun event(
        action: TouchCommandAction,
        generation: Long = 3,
        x: Double? = if (action == TouchCommandAction.CANCEL) null else 0.5,
        y: Double? = if (action == TouchCommandAction.CANCEL) null else 0.5,
    ) = DesktopTouchEvent(
        action = action,
        x = x,
        y = y,
        generation = generation,
    )
}

private class TouchManualWaitGate {
    val requestedDelays = mutableListOf<Long>()
    private var continuation: CancellableContinuation<Unit>? = null
    val hasPendingWait: Boolean
        get() = continuation != null

    suspend fun wait(delayMillis: Long) {
        requestedDelays += delayMillis
        suspendCancellableCoroutine { current ->
            check(continuation == null)
            continuation = current
            current.invokeOnCancellation {
                if (continuation === current) {
                    continuation = null
                }
            }
        }
    }

    fun release() {
        val current = requireNotNull(continuation)
        continuation = null
        current.resume(Unit)
    }
}
