package com.panelos.panelhome.data.transport

import java.io.IOException

/**
 * Why a native USB session stopped.
 *
 * Mapping every `IOException` to "the cable was removed" is what previously made
 * the lifecycle guess: a host that restarted, a descriptor this app closed on
 * purpose and an actual detach all arrived as the same failure, and the only
 * response left was to reopen the accessory and hope. The recovery state machine
 * branches on this instead, so a decision is never inferred from a message.
 *
 * Physical detach is **not** in this list on purpose: USB has no end of stream,
 * so a session cannot observe it. `ACTION_USB_ACCESSORY_DETACHED` is the only
 * authority for that, and it arrives through the lifecycle, not through a read.
 */
enum class UsbFailureKind {
    /** A newer host greeting replaced the session in force. Not a failure. */
    SESSION_SUPERSEDED,

    /** Framing or sequence violation: the stream is no longer trustworthy. */
    TRANSPORT_PROTOCOL_FAILURE,

    /** This app closed the descriptor; the blocked read unwound because of it. */
    DESCRIPTOR_CLOSED,

    /** The peer never completed PLMX within the handshake deadline. */
    HANDSHAKE_TIMEOUT,

    /** `UsbManager.openAccessory` refused, or no permitted accessory existed. */
    OPEN_ACCESSORY_FAILURE,

    /** An I/O error whose cause the app cannot attribute. */
    UNKNOWN_TRANSPORT_FAILURE,
}

/**
 * Physical-session callbacks from [NativeUsbTransport] to the recovery state
 * machine. Every callback carries the generation it belongs to so a late report
 * from a dead session cannot act on its successor.
 */
internal interface UsbTransportListener {
    fun onTransportHandshakeStarted(generation: Long) = Unit
    fun onTransportHandshakeSucceeded(generation: Long, sessionPrefix: String) = Unit
    fun onTransportHandshakeFailed(generation: Long, kind: UsbFailureKind, detail: String) = Unit
    fun onSessionSuperseded(generation: Long, sessionPrefix: String) = Unit
    fun onPhysicalConnectionLost(generation: Long, kind: UsbFailureKind, detail: String) = Unit
    fun onDescriptorClosed(generation: Long) = Unit
}

internal object UsbFailureClassifier {
    /**
     * [descriptorClosedByUs] is the only reliable disambiguator available: the
     * errno a blocked accessory read unwinds with is the same whether the fd was
     * closed deliberately or the gadget went away, so the app has to remember
     * which one it did.
     */
    fun classify(error: Throwable?, descriptorClosedByUs: Boolean): UsbFailureKind {
        if (error == null) return UsbFailureKind.DESCRIPTOR_CLOSED
        val chain = generateSequence(error, Throwable::cause).toList()
        if (chain.any { it is TransportProtocolException }) {
            return UsbFailureKind.TRANSPORT_PROTOCOL_FAILURE
        }
        val text = chain.joinToString(" ") { "${it::class.java.simpleName} ${it.message.orEmpty()}" }
            .lowercase()
        return when {
            HANDSHAKE_MARKERS.any { it in text } -> UsbFailureKind.HANDSHAKE_TIMEOUT
            OPEN_MARKERS.any { it in text } -> UsbFailureKind.OPEN_ACCESSORY_FAILURE
            descriptorClosedByUs || CLOSED_MARKERS.any { it in text } ->
                UsbFailureKind.DESCRIPTOR_CLOSED
            chain.any { it is IOException } -> UsbFailureKind.UNKNOWN_TRANSPORT_FAILURE
            else -> UsbFailureKind.UNKNOWN_TRANSPORT_FAILURE
        }
    }

    private val HANDSHAKE_MARKERS = listOf("handshake", "resync")
    private val OPEN_MARKERS = listOf(
        "refused to open",
        "no android open accessory",
        "permission has not been granted",
    )
    private val CLOSED_MARKERS = listOf(
        "socket closed",
        "bad file descriptor",
        "stream closed",
        "file descriptor closed",
        "ebadf",
    )
}
