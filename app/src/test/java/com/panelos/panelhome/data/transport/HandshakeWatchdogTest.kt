package com.panelos.panelhome.data.transport

import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The watchdog exists because a blocking read on `/dev/usb_accessory` never
 * returns for a silent peer and ignores interrupts, so the PLMX resync bound
 * cannot fire. Since the descriptor is single-open, a handshake left blocked
 * holds the accessory for the life of the process and no later attach can
 * reconnect - observed physically after every cable pull on 2026-08-19.
 */
class HandshakeWatchdogTest {
    @Test
    fun `fires when the handshake does not finish in time`() {
        val fired = CountDownLatch(1)
        HandshakeWatchdog(30L) { fired.countDown() }.start()

        assertTrue(
            "the watchdog must release the accessory when the handshake stalls",
            fired.await(5, TimeUnit.SECONDS),
        )
    }

    @Test
    fun `does not fire when the handshake completes first`() {
        val fired = AtomicInteger()
        val watchdog = HandshakeWatchdog(5_000L) { fired.incrementAndGet() }.start()

        assertFalse("cancel() must report that the watchdog had not fired", watchdog.cancel())
        Thread.sleep(100L)
        assertEquals("a completed handshake must keep its descriptor", 0, fired.get())
    }

    /**
     * `cancel()` is what tells `openSession` whether the descriptor it is about
     * to hand out has already been closed underneath it. Getting this backwards
     * would leave a closed handle referenced as if it were live.
     */
    @Test
    fun `cancel reports whether the watchdog already fired`() {
        val fired = CountDownLatch(1)
        val watchdog = HandshakeWatchdog(20L) { fired.countDown() }.start()
        assertTrue(fired.await(5, TimeUnit.SECONDS))

        assertTrue("cancel() must report the timeout that already happened", watchdog.cancel())
    }

    @Test
    fun `timeout action runs at most once`() {
        val fired = AtomicInteger()
        val watchdog = HandshakeWatchdog(20L) { fired.incrementAndGet() }.start()
        Thread.sleep(300L)
        watchdog.cancel()
        watchdog.cancel()

        assertEquals(1, fired.get())
    }
}
