package com.panelos.panelhome.domain.model

/**
 * How far the panel has got towards a usable session, as the UI needs it.
 *
 * [ConnectionStatus] answers only "is the application protocol up", which is all
 * the dashboard needs to draw. It cannot distinguish an unplugged cable from a
 * host that has not answered yet, and those need different words on a screen the
 * operator is looking at while holding the cable.
 *
 * This is a projection of the USB recovery state machine, not the machine
 * itself: several internal states collapse into one phase on purpose.
 */
enum class ConnectionPhase {
    /** The session runs on ADB/TCP, so there is no USB lifecycle to report. */
    DISABLED,

    /** No accessory is attached. Nothing is open and nothing is being retried. */
    WAITING_FOR_USB,

    /** An accessory is attached and the descriptor is being opened. */
    OPENING_USB,

    /** The descriptor is open; PLMX v2 is exchanging HELLO/HELLO_ACK. */
    TRANSPORT_HANDSHAKE,

    /** PLMX is established; Panel Protocol v1 is exchanging hello/hello_ack. */
    APPLICATION_HANDSHAKE,

    /** Both handshakes completed. */
    CONNECTED,

    /** The cable looks present but the session is being recycled. */
    RECOVERING,

    /** Carrier down for a reason the USB lifecycle does not own. */
    DISCONNECTED,
}
