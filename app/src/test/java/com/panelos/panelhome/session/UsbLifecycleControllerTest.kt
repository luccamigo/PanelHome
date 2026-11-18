package com.panelos.panelhome.session

import com.panelos.panelhome.domain.model.ConnectionPhase
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The reducer is pure, so what is left to prove here is the thing that made the
 * old lifecycle unsafe: that four independent sources of USB events - a
 * broadcast receiver, an intent, a handshake watchdog and a transport reader -
 * cannot interleave into two sessions or into an open that overtakes a close.
 */
class UsbLifecycleControllerTest {
    @Test
    fun `a detach that arrives mid-open is handled after the open, never during it`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val port = RecordingPort()
        val controller = UsbLifecycleController(scope, port)
        controller.start()

        port.blockNextOpen()
        controller.submit(UsbLifecycleEvent.ServiceStarted(ACCESSORY, permitted = true))
        port.awaitOpenStarted()
        // The cable comes out while `openAccessory` is still inside the loop.
        controller.submit(UsbLifecycleEvent.AccessoryDetached(ACCESSORY))
        port.releaseOpen()

        port.awaitCalls("open", "close")
        assertEquals(1, port.opens)
        assertEquals(1, port.closes)
        assertEquals(0, port.maxConcurrent - 1)
        awaitState(controller, UsbRecoveryState.WAITING_FOR_ACCESSORY)
        scope.cancel()
    }

    @Test
    fun `an attach storm produces exactly one session`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val port = RecordingPort()
        val controller = UsbLifecycleController(scope, port)
        controller.start()

        repeat(20) {
            controller.submit(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = true))
        }

        awaitState(controller, UsbRecoveryState.TRANSPORT_HANDSHAKING) {
            controller.submit(UsbLifecycleEvent.OpenSucceeded(controller.generation))
        }
        assertEquals(1, port.opens)
        assertEquals(0, port.closes)
        scope.cancel()
    }

    @Test
    fun `shutdown releases the live descriptor`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val port = RecordingPort()
        val controller = UsbLifecycleController(scope, port)
        controller.start()
        controller.submit(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = true))
        // The controller reports its own open result, so OPENING_ACCESSORY is
        // gone again within microseconds; the handshake is the first state that
        // waits for someone else.
        awaitState(controller, UsbRecoveryState.TRANSPORT_HANDSHAKING)

        controller.shutdown()

        port.awaitCalls("open", "close")
        assertEquals(1, port.closes)
        scope.cancel()
    }

    @Test
    fun `an unpermitted accessory produces one permission request`() = runBlocking {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val port = RecordingPort()
        val controller = UsbLifecycleController(scope, port)
        controller.start()

        controller.submit(UsbLifecycleEvent.AccessoryAttached(ACCESSORY, permitted = false))
        // The reducer records the state before its actions run, so waiting on
        // the state would let the assertions overtake the side effect.
        awaitPort(port) { it.permissionRequests == 1 }

        assertEquals(UsbRecoveryState.WAITING_FOR_PERMISSION, controller.state)
        assertEquals(0, port.opens)
        assertEquals(1, port.permissionRequests)
        assertTrue(ConnectionPhase.WAITING_FOR_USB in port.phases)
        scope.cancel()
    }

    private suspend fun awaitPort(port: RecordingPort, until: (RecordingPort) -> Boolean) {
        withTimeout(WAIT_MILLIS) {
            while (!until(port)) delay(5)
        }
    }

    private suspend fun awaitState(
        controller: UsbLifecycleController,
        expected: UsbRecoveryState,
        nudge: () -> Unit = {},
    ) {
        withTimeout(WAIT_MILLIS) {
            while (controller.state != expected) {
                nudge()
                delay(5)
            }
        }
    }

    private class RecordingPort : UsbLifecyclePort {
        private val concurrent = AtomicInteger()
        private val calls = Collections.synchronizedList(mutableListOf<String>())
        private var openGate: CountDownLatch? = null
        private var openStarted: CountDownLatch? = null

        val phases = Collections.synchronizedList(mutableListOf<ConnectionPhase>())

        @Volatile
        var maxConcurrent = 0
            private set
        val opens: Int get() = calls.count { it == "open" }
        val closes: Int get() = calls.count { it == "close" }

        @Volatile
        var permissionRequests = 0
            private set

        fun blockNextOpen() {
            openGate = CountDownLatch(1)
            openStarted = CountDownLatch(1)
        }

        fun awaitOpenStarted() {
            assertTrue(openStarted!!.await(WAIT_MILLIS, TimeUnit.MILLISECONDS))
        }

        fun releaseOpen() {
            openGate?.countDown()
        }

        fun awaitCalls(vararg expected: String) {
            val deadline = System.nanoTime() + WAIT_MILLIS * 1_000_000
            while (System.nanoTime() < deadline) {
                if (calls.toList() == expected.toList()) return
                Thread.sleep(5)
            }
            assertEquals(expected.toList(), calls.toList())
        }

        override suspend fun openAccessory(
            generation: Long,
            accessory: AccessoryIdentity,
        ): UsbOpenResult {
            enter("open")
            openStarted?.countDown()
            openGate?.await(WAIT_MILLIS, TimeUnit.MILLISECONDS)
            leave()
            return UsbOpenResult.Opened
        }

        override suspend fun closeSession(generation: Long, reason: String) {
            enter("close")
            leave()
        }

        override fun requestPermission(accessory: AccessoryIdentity) {
            permissionRequests++
        }

        override fun publishPhase(phase: ConnectionPhase) {
            phases += phase
        }

        override fun log(message: String) = Unit

        private fun enter(name: String) {
            calls += name
            maxConcurrent = maxOf(maxConcurrent, concurrent.incrementAndGet())
        }

        private fun leave() {
            concurrent.decrementAndGet()
        }
    }

    private companion object {
        const val WAIT_MILLIS = 5_000L
        val ACCESSORY = AccessoryIdentity.of("PanelOS", "PanelHost", "1", "serial")
    }
}
