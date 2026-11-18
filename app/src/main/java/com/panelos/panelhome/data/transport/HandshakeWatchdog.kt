package com.panelos.panelhome.data.transport

import java.util.concurrent.atomic.AtomicBoolean

/**
 * Releases the accessory when a handshake stops making progress.
 *
 * The PLMX resync limit bounds handshake duration, but it can only be evaluated
 * between reads, and a read from `/dev/usb_accessory` is a blocking read on a
 * character device: it does not return when the peer is silent and it ignores
 * `Thread.interrupt`. A handshake against a host that is not there therefore
 * blocks forever.
 *
 * That would merely be a stuck session, except `/dev/usb_accessory` is
 * single-open. The blocked thread keeps the descriptor for the life of the
 * process, so the *next* AOA attach cannot open the accessory at all and the
 * panel never reconnects until the app is restarted - which is exactly the
 * failure observed physically on 2026-08-19 after every cable pull.
 *
 * Closing the descriptor from another thread is what unblocks the read, so the
 * watchdog owns that one action and nothing else.
 */
internal class HandshakeWatchdog(
    private val timeoutMillis: Long,
    private val onTimeout: () -> Unit,
) {
    private val finished = AtomicBoolean()
    private var thread: Thread? = null

    fun start(): HandshakeWatchdog {
        val worker = Thread({
            try {
                Thread.sleep(timeoutMillis)
            } catch (_: InterruptedException) {
                return@Thread
            }
            if (finished.compareAndSet(false, true)) onTimeout()
        }, "PanelOS-USB-handshake-watchdog")
        worker.isDaemon = true
        thread = worker
        worker.start()
        return this
    }

    /** True when the watchdog fired; false when the handshake beat it. */
    fun cancel(): Boolean {
        val alreadyFired = !finished.compareAndSet(false, true)
        thread?.interrupt()
        thread = null
        return alreadyFired
    }
}
