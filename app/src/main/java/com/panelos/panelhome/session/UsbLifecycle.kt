package com.panelos.panelhome.session

import com.panelos.panelhome.data.transport.UsbFailureKind
import com.panelos.panelhome.domain.model.ConnectionPhase

/**
 * Explicit states of the native USB lifecycle.
 *
 * The lifecycle used to be several booleans spread across the Service, the
 * runtime and the transport - `activeTransport is NativeUsbTransport`,
 * `connectedInCurrentTransport`, `recoveryJob?.isActive`. Nothing named the one
 * distinction that decides everything after a cable pull: whether an accessory
 * exists at all. Without it a detach and a failed session were the same
 * condition, and the only response was to reopen an accessory that was not
 * there.
 */
internal enum class UsbRecoveryState {
    /** The session runs on ADB/TCP. The machine owns nothing. */
    DISABLED,

    /**
     * No descriptor, no transport, no PLMX session - and no timer.
     *
     * This is the resting state after a physical detach. Reaching it is what
     * stops `openAccessory` from being called against an accessory that does
     * not exist, which is what a detach means.
     */
    WAITING_FOR_ACCESSORY,

    /** An accessory is attached but this app may not open it yet. */
    WAITING_FOR_PERMISSION,

    /** Single-flight: exactly one `openAccessory` call may be outstanding. */
    OPENING_ACCESSORY,

    /** Descriptor open, PLMX v2 HELLO/HELLO_ACK in progress. */
    TRANSPORT_HANDSHAKING,

    /** PLMX established, Panel Protocol v1 hello/hello_ack in progress. */
    APPLICATION_HANDSHAKING,

    /** Both handshakes completed; the panel is usable. */
    CONNECTED,

    /**
     * The cable still looks present, but the session has to be rebuilt. Reached
     * from a transport failure that is *not* a detach.
     */
    RECOVERING_SESSION,

    /** Tearing the current generation down. Nothing may open until it ends. */
    CLOSING_ACCESSORY,

    /** Bounded wait before another open attempt while an accessory is present. */
    BACKOFF,

    /** The Service is going away. Terminal. */
    STOPPED,
}

/**
 * Enough of a `UsbAccessory` to answer one question: does this DETACHED belong
 * to the accessory we are using?
 *
 * Object identity cannot answer it - Android hands out a fresh `UsbAccessory`
 * per broadcast - and the serial must not become an identifier that reaches a
 * log, so it is reduced to a short non-reversible digest that is only ever
 * compared, never printed in full.
 */
internal data class AccessoryIdentity(
    val manufacturer: String,
    val model: String,
    val version: String,
    val serialDigest: Int,
) {
    /** Safe for logs: no serial, no URI, no device path. */
    override fun toString(): String = manufacturer + "/" + model + "@" + version + "#" + digestPrefix()

    private fun digestPrefix(): String =
        Integer.toHexString(serialDigest).padStart(8, '0').take(4).uppercase()

    companion object {
        fun of(
            manufacturer: String?,
            model: String?,
            version: String?,
            serial: String?,
        ): AccessoryIdentity = AccessoryIdentity(
            manufacturer = manufacturer.orEmpty(),
            model = model.orEmpty(),
            version = version.orEmpty(),
            serialDigest = serial?.hashCode() ?: 0,
        )
    }
}

/** Everything that can move the lifecycle. Nothing else may change state. */
internal sealed interface UsbLifecycleEvent {
    /** The Service came up. Carries whatever the startup scan already saw. */
    data class ServiceStarted(
        val accessory: AccessoryIdentity?,
        val permitted: Boolean,
    ) : UsbLifecycleEvent

    /** A permitted accessory was found without a broadcast (app start, restart). */
    data class AccessoryObserved(
        val accessory: AccessoryIdentity,
        val permitted: Boolean,
    ) : UsbLifecycleEvent

    data class AccessoryAttached(
        val accessory: AccessoryIdentity,
        val permitted: Boolean,
    ) : UsbLifecycleEvent

    /** `null` when the broadcast carried no accessory: still authoritative. */
    data class AccessoryDetached(val accessory: AccessoryIdentity?) : UsbLifecycleEvent

    data class PermissionResult(
        val accessory: AccessoryIdentity,
        val granted: Boolean,
    ) : UsbLifecycleEvent

    data class OpenSucceeded(val generation: Long) : UsbLifecycleEvent

    data class OpenFailed(
        val generation: Long,
        val kind: UsbFailureKind,
        val detail: String,
    ) : UsbLifecycleEvent

    data class TransportHandshakeSucceeded(
        val generation: Long,
        val sessionPrefix: String,
    ) : UsbLifecycleEvent

    data class TransportHandshakeFailed(
        val generation: Long,
        val kind: UsbFailureKind,
        val detail: String,
    ) : UsbLifecycleEvent

    data class ApplicationHandshakeSucceeded(val generation: Long) : UsbLifecycleEvent

    data class ApplicationHandshakeFailed(
        val generation: Long,
        val detail: String,
    ) : UsbLifecycleEvent

    data class PhysicalConnectionLost(
        val generation: Long,
        val kind: UsbFailureKind,
        val detail: String,
    ) : UsbLifecycleEvent

    /** A newer host greeting took the session over. The descriptor stays open. */
    data class HostSessionSuperseded(
        val generation: Long,
        val sessionPrefix: String,
    ) : UsbLifecycleEvent

    /** The teardown the machine asked for has finished. */
    data class SessionClosed(val generation: Long) : UsbLifecycleEvent

    data class RetryTimerElapsed(val generation: Long, val attempt: Int) : UsbLifecycleEvent

    /** Neither handshake completed inside its deadline. */
    data class HandshakeDeadlineElapsed(val generation: Long) : UsbLifecycleEvent

    /** The operator selected ADB/TCP. The USB lifecycle stands down. */
    data object CarrierDisabled : UsbLifecycleEvent

    data object ServiceStopping : UsbLifecycleEvent
}

/** Side effects. The reducer never performs one; it only asks for it. */
internal sealed interface UsbLifecycleAction {
    data class RequestPermission(val accessory: AccessoryIdentity) : UsbLifecycleAction

    data class OpenAccessory(
        val generation: Long,
        val accessory: AccessoryIdentity,
    ) : UsbLifecycleAction

    /**
     * Close-before-open, always. The successor is not created until
     * [UsbLifecycleEvent.SessionClosed] reports this finished.
     */
    data class CloseSession(
        val generation: Long,
        val reason: String,
    ) : UsbLifecycleAction

    data class ScheduleRetry(
        val generation: Long,
        val attempt: Int,
        val delayMillis: Long,
    ) : UsbLifecycleAction

    data object CancelTimers : UsbLifecycleAction

    data class ArmHandshakeDeadline(
        val generation: Long,
        val timeoutMillis: Long,
    ) : UsbLifecycleAction

    data class PublishPhase(val phase: ConnectionPhase) : UsbLifecycleAction
}

/**
 * Every deadline the lifecycle depends on, in one place.
 *
 * The values are not arbitrary: each one is bounded by something physical on the
 * other side of the cable, and the comment says which, because a timeout tuned
 * against nothing is how a slow handshake becomes a false negative.
 */
internal object UsbLifecycleTimings {
    /**
     * `UsbManager.openAccessory` is a binder call that either returns a
     * descriptor or `null`. It does not block on the wire, so anything past a
     * few seconds is the framework being wedged, not the host being slow.
     */
    const val OPEN_ACCESSORY_TIMEOUT_MILLIS = 3_000L

    /**
     * Backstop for the PLMX resync bound (10 s), which cannot be evaluated while
     * a blocking accessory read never returns. Slightly longer so a handshake
     * that is merely slow still fails through the protocol's own path.
     */
    const val TRANSPORT_HANDSHAKE_TIMEOUT_MILLIS = 12_000L

    /**
     * PLMX is up, so the carrier works; only PanelHost's JSONL hello is missing.
     * Covers the host's own start-up, which includes opening the display broker.
     */
    const val APPLICATION_HANDSHAKE_TIMEOUT_MILLIS = 15_000L

    /**
     * Bound on one physical write. The writer thread is single, so an unbounded
     * wait here is what let a teardown on the main thread hang forever after a
     * cable pull.
     */
    const val PHYSICAL_WRITE_TIMEOUT_MILLIS = 5_000L

    /** Exponential, bounded, and only used while an accessory is believed present. */
    val OPEN_BACKOFF_MILLIS = longArrayOf(250L, 500L, 1_000L, 2_000L, 4_000L)

    /**
     * After this many consecutive failures the machine stops assuming the
     * accessory is real and goes back to waiting for a physical attach. It is
     * the guard that keeps a stale accessory entry from becoming an open loop.
     */
    const val MAX_CONSECUTIVE_OPEN_ATTEMPTS = 6

    /**
     * How long the panel waits before probing an accessory that is attached but
     * refuses to open.
     *
     * Measured on the test tablet on 2026-08-27: after the descriptor is closed, a
     * read that was blocked on it can stay blocked for over a minute, and
     * `openAccessory` refuses for as long as it does. Giving up entirely would
     * strand a panel whose accessory becomes openable a minute later, so the
     * bounded burst is followed by this - one attempt, rarely, forever.
     */
    const val IDLE_REPROBE_MILLIS = 30_000L

    /**
     * Session recycles that reopen the descriptor immediately before pacing
     * starts. A replug has to reconnect at once; a panel sitting next to a host
     * that is simply not running does not, and every needless reopen is another
     * cycle on the descriptor that is the fragile part of this lifecycle.
     */
    const val IMMEDIATE_SESSION_RECYCLES = 3

    /**
     * How long the panel waits, after a session dies mid-frame, before deciding
     * that the accessory is still there.
     *
     * Measured on the test tablet on 2026-08-27: a cable pull kills the PLMX session
     * at once, but `ACTION_USB_ACCESSORY_DETACHED` arrives **990 ms later**. In
     * that window `UsbManager.accessoryList` still lists the accessory and
     * `openAccessory` still succeeds - on a gadget the kernel is in the middle of
     * tearing down. The panel was therefore opening `/dev/usb_accessory` during a
     * disconnect it had not been told about yet, which is the most plausible
     * trigger for the `UsbDeviceManager` ending up with an empty function list.
     *
     * Only a mid-frame loss waits: a handshake deadline is not evidence that a
     * cable moved, so it still recycles immediately.
     */
    const val DETACH_GRACE_MILLIS = 2_000L

    fun backoffFor(attempt: Int): Long =
        OPEN_BACKOFF_MILLIS[(attempt - 1).coerceIn(0, OPEN_BACKOFF_MILLIS.lastIndex)]
}

/** Cumulative counters. Read for diagnostics; never used to decide anything. */
internal data class UsbRecoveryMetrics(
    val usbPhysicalDetaches: Long = 0,
    val usbPhysicalAttaches: Long = 0,
    val usbAccessoryOpenAttempts: Long = 0,
    val usbAccessoryOpenSuccesses: Long = 0,
    val usbAccessoryOpenFailures: Long = 0,
    val usbDescriptorCloses: Long = 0,
    val usbDescriptorCloseFailures: Long = 0,
    val usbRecoveryAttempts: Long = 0,
    val usbRecoverySuccesses: Long = 0,
    val usbRecoveryFailures: Long = 0,
    val usbHandshakeTimeouts: Long = 0,
    val usbSessionSupersedes: Long = 0,
    val usbStaleGenerationEventsIgnored: Long = 0,
    val usbPermissionRequests: Long = 0,
    val usbPermissionDenials: Long = 0,
    /** Attach to Panel Protocol connected. Not cable insertion: see the docs. */
    val attachToConnectedMillis: Long? = null,
    val lastRecoveryDurationMillis: Long? = null,
) {
    fun summary(): String =
        "detach=" + usbPhysicalDetaches + " attach=" + usbPhysicalAttaches +
            " open=" + usbAccessoryOpenAttempts + "/" + usbAccessoryOpenSuccesses +
            " openFail=" + usbAccessoryOpenFailures + " close=" + usbDescriptorCloses +
            " recovery=" + usbRecoveryAttempts + "/" + usbRecoverySuccesses +
            " handshakeTimeout=" + usbHandshakeTimeouts +
            " supersede=" + usbSessionSupersedes +
            " stale=" + usbStaleGenerationEventsIgnored +
            " attachToConnectedMs=" + (attachToConnectedMillis ?: "-")
}
