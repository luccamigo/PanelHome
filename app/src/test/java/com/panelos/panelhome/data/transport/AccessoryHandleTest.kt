package com.panelos.panelhome.data.transport

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * A double close is not a tidiness problem: the second one can land on a
 * descriptor number the runtime has already recycled, and the accessory then
 * refuses to open for the life of the process. The three things that race to
 * close - the detach teardown, the handshake watchdog and the session's own
 * failure path - are all modelled here.
 */
class AccessoryHandleTest {
    @Test
    fun `the descriptor is closed exactly once however many owners ask`() {
        val closes = AtomicInteger()
        val handle = handle(closes)

        handle.close()
        handle.close()
        handle.close()

        assertEquals(1, closes.get())
        assertTrue(handle.isClosed)
    }

    @Test
    fun `a detach, a watchdog and a session teardown racing still close once`() {
        repeat(50) {
            val closes = AtomicInteger()
            val handle = handle(closes)
            val start = CountDownLatch(1)
            val done = CountDownLatch(3)
            repeat(3) {
                Thread {
                    start.await()
                    handle.close()
                    done.countDown()
                }.apply { isDaemon = true }.start()
            }

            start.countDown()
            assertTrue(done.await(5, TimeUnit.SECONDS))
            assertEquals(1, closes.get())
        }
    }

    @Test
    fun `a descriptor that throws on close is still marked released`() {
        val closes = AtomicInteger()
        val handle = AccessoryHandle(
            input = ByteArrayInputStream(ByteArray(0)),
            output = ByteArrayOutputStream(),
            closeDescriptor = {
                closes.incrementAndGet()
                throw java.io.IOException("the gadget is already gone")
            },
        )

        handle.close()
        handle.close()

        // The whole point of the teardown is that the next attach can open. A
        // close that throws has still given the fd back to the kernel.
        assertEquals(1, closes.get())
        assertTrue(handle.isClosed)
    }

    private fun handle(closes: AtomicInteger) = AccessoryHandle(
        input = ByteArrayInputStream(ByteArray(0)),
        output = ByteArrayOutputStream(),
        closeDescriptor = { closes.incrementAndGet() },
    )
}
