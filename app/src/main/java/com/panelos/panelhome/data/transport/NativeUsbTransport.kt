package com.panelos.panelhome.data.transport

import android.util.Log
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Android Open Accessory carrier for one physical session, and one only.
 *
 * The descriptor is handed in already open. That is the whole design change: as
 * long as this class could open an accessory by itself, the reconnect loop of
 * the control connection could ask it to - once a second, forever, including
 * after a cable pull when there was no accessory to open. Deciding *whether*
 * there is something to open is a lifecycle question, so it belongs to the
 * recovery state machine, and this class is left with the part it can actually
 * answer: whether the session on this descriptor still works.
 *
 * The tablet is the PLMX accessory role: the host generates the session nonce
 * and this side adopts it. Every physical connection therefore gets a fresh
 * identity without this side having to coordinate anything, and a host that
 * restarts over a live descriptor is absorbed by "newest HELLO wins" rather than
 * by reopening the accessory, which is the fragile part of the Android
 * lifecycle.
 */
internal class NativeUsbTransport(
    private val handle: AccessoryHandle,
    private val generation: Long,
    private val listener: UsbTransportListener = NoUsbTransportListener,
) : PanelTransport {
    private val sessionLock = Any()
    private val closed = AtomicBoolean()
    private val lossReported = AtomicBoolean()

    /**
     * Whether this side closed the descriptor. A blocked accessory read unwinds
     * with the same errno whether the fd went away because we closed it or
     * because the gadget did, so the only way to tell them apart is to remember.
     */
    @Volatile
    private var descriptorClosedByUs = false

    @Volatile
    private var session: MultiplexedTransportSession? = null

    override val name = "usb-native"
    override val metrics = TransportMetrics()

    val physicalGeneration: Long get() = generation

    override suspend fun connect(channel: LogicalChannel): PanelByteConnection {
        require(
            channel == LogicalChannel.CONTROL ||
                channel == LogicalChannel.VIDEO ||
                channel == LogicalChannel.ASSETS,
        )
        val active = synchronized(sessionLock) {
            if (closed.get()) throw TransportDisconnectedException("Native USB transport is closed")
            val existing = session
            when {
                existing != null && existing.isHealthy -> existing
                existing != null -> throw TransportDisconnectedException(
                    "The physical USB session ended (state=" + existing.sessionState.name +
                        "); the recovery lifecycle owns the descriptor from here",
                )
                else -> adoptSession(openSession())
            }
        }
        return try {
            active.open(channel)
        } catch (error: Exception) {
            Log.w(LOG_TAG, "[Transport] Logical channel " + channel + " failed", error)
            throw TransportDisconnectedException("Native USB logical channel failed", error)
        }
    }

    /**
     * Publishes a session only if [close] did not run while the handshake was
     * blocked. Both closes are idempotent, so the double check needs no lock -
     * which matters, because [close] deliberately takes none.
     */
    private fun adoptSession(opened: MultiplexedTransportSession): MultiplexedTransportSession {
        if (closed.get()) {
            opened.close()
            throw TransportDisconnectedException(
                "The transport was closed while its handshake was still blocked",
            )
        }
        session = opened
        if (closed.get()) {
            session = null
            opened.close()
            throw TransportDisconnectedException(
                "The transport was closed while its handshake was still blocked",
            )
        }
        return opened
    }

    private fun openSession(): MultiplexedTransportSession {
        listener.onTransportHandshakeStarted(generation)
        // See HandshakeWatchdog: without this a handshake against an absent host
        // blocks forever and holds the single-open accessory descriptor for the
        // life of the process, so no later attach can reconnect.
        val watchdog = HandshakeWatchdog(HANDSHAKE_TIMEOUT_MILLIS) {
            Log.w(
                LOG_TAG,
                "[Transport] Handshake exceeded " + HANDSHAKE_TIMEOUT_MILLIS +
                    "ms; releasing the accessory so the next attach can open it",
            )
            descriptorClosedByUs = true
            handle.close()
        }.start()
        val opened = MultiplexedTransportSession(
            physicalInput = handle.input,
            physicalOutput = handle.output,
            closePhysical = {
                descriptorClosedByUs = true
                handle.close()
                listener.onDescriptorClosed(generation)
            },
            metrics = metrics,
            log = { message -> Log.i(LOG_TAG, message) },
            onSuperseded = { prefix -> listener.onSessionSuperseded(generation, prefix) },
            onClosed = ::reportSessionClosed,
        )
        try {
            opened.start()
        } catch (error: Throwable) {
            val timedOut = watchdog.cancel()
            descriptorClosedByUs = true
            handle.close()
            val kind = if (timedOut) {
                UsbFailureKind.HANDSHAKE_TIMEOUT
            } else {
                UsbFailureClassifier.classify(error, descriptorClosedByUs)
            }
            listener.onTransportHandshakeFailed(generation, kind, describe(error))
            throw error
        }
        if (watchdog.cancel()) {
            // The watchdog closed the descriptor while start() was still inside
            // a read. Whatever the handshake concluded was concluded on an fd
            // that no longer exists, so it is not a session.
            opened.close()
            listener.onTransportHandshakeFailed(
                generation,
                UsbFailureKind.HANDSHAKE_TIMEOUT,
                "PLMX handshake exceeded " + HANDSHAKE_TIMEOUT_MILLIS + "ms",
            )
            throw TransportDisconnectedException("PLMX handshake exceeded its deadline")
        }
        listener.onTransportHandshakeSucceeded(generation, opened.sessionId.prefix)
        return opened
    }

    /**
     * Reported once per physical session. A dead session is not reopened here:
     * doing that is exactly what turned a host restart and a cable pull into the
     * same unbounded reopen loop.
     */
    private fun reportSessionClosed(error: Throwable?) {
        if (!lossReported.compareAndSet(false, true)) return
        if (closed.get() && error == null) return
        val kind = UsbFailureClassifier.classify(error, descriptorClosedByUs)
        listener.onPhysicalConnectionLost(generation, kind, describe(error))
    }

    /**
     * Releases the descriptor **without taking [sessionLock]**, and before
     * anything else.
     *
     * A handshake in flight holds that lock for as long as its read blocks, and
     * a read on `/dev/usb_accessory` is a blocking read on a character device.
     * Measured on the test tablet on 2026-08-27: after the watchdog closed the
     * descriptor the read stayed blocked for a further 63 seconds, and a
     * teardown that waited for the lock waited with it - a minute during which
     * the panel could not have answered a replug. The descriptor close is the
     * one action that has a chance of unblocking that read, so it must never
     * queue behind it.
     *
     * Nothing here flushes: after a cable pull a write to the gadget does not
     * return, so the descriptor close is the whole teardown boundary.
     */
    override fun close() {
        if (!closed.compareAndSet(false, true)) return
        descriptorClosedByUs = true
        handle.close()
        val active = session
        session = null
        active?.close()
    }

    private fun describe(error: Throwable?): String = when (error) {
        null -> "closed"
        else -> error::class.java.simpleName + ": " + (error.message ?: "no message")
    }

    private companion object {
        const val LOG_TAG = "PanelOS"

        /**
         * Backstop for the PLMX resync bound (10 s), which cannot fire while a
         * blocking accessory read never returns. Slightly longer so a handshake
         * that is merely slow still fails through the protocol's own path.
         */
        const val HANDSHAKE_TIMEOUT_MILLIS = 12_000L
    }
}

internal object NoUsbTransportListener : UsbTransportListener
