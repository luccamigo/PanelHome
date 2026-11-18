package com.panelos.panelhome.presentation

import kotlin.coroutines.resume
import kotlinx.coroutines.CancellableContinuation
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.suspendCancellableCoroutine
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

class VolumeCommandThrottlerTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)

    @After
    fun tearDown() {
        scope.cancel()
    }

    @Test
    fun dragUpdatesAreCoalescedAndFinalValueIsAlwaysSent() {
        val waitGate = ManualWaitGate()
        val sentValues = mutableListOf<Int>()
        val throttler = VolumeCommandThrottler(
            scope = scope,
            wait = waitGate::wait,
            onSend = sentValues::add,
        )

        throttler.submit(10)
        (11..72).forEach(throttler::submit)

        assertEquals(listOf(10), sentValues)
        assertEquals(listOf(VOLUME_COMMAND_THROTTLE_MILLIS), waitGate.requestedDelays)

        waitGate.release()

        assertEquals(listOf(10, 72), sentValues)

        throttler.finish(81)

        assertEquals(listOf(10, 72, 81), sentValues)
    }

    @Test
    fun volumeValuesAreClampedBeforeSending() {
        val waitGate = ManualWaitGate()
        val sentValues = mutableListOf<Int>()
        val throttler = VolumeCommandThrottler(
            scope = scope,
            wait = waitGate::wait,
            onSend = sentValues::add,
        )

        throttler.submit(-1)
        throttler.finish(101)

        assertEquals(listOf(0, 100), sentValues)
    }

    @Test
    fun cancelDropsValuePendingAfterTheThrottleInterval() {
        val waitGate = ManualWaitGate()
        val sentValues = mutableListOf<Int>()
        val throttler = VolumeCommandThrottler(
            scope = scope,
            wait = waitGate::wait,
            onSend = sentValues::add,
        )

        throttler.submit(35)
        throttler.submit(72)
        throttler.cancel()

        assertEquals(listOf(35), sentValues)
        assertEquals(listOf(VOLUME_COMMAND_THROTTLE_MILLIS), waitGate.requestedDelays)
        assertEquals(false, waitGate.hasPendingWait)
    }
}

private class ManualWaitGate {
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
