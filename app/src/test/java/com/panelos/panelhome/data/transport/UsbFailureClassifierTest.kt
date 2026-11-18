package com.panelos.panelhome.data.transport

import java.io.EOFException
import java.io.IOException
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The lifecycle branches on these answers, so a wrong one is a wrong physical
 * decision: reopening a descriptor after a host restart, or waiting for an
 * attach after a framing error that a reopen would have fixed.
 */
class UsbFailureClassifierTest {
    @Test
    fun `a framing violation is a protocol failure, not a cable event`() {
        val failure = TransportDisconnectedException(
            "Physical USB disconnected mid-frame",
            TransportProtocolException("Data arrived for unopened CONTROL"),
        )

        assertEquals(
            UsbFailureKind.TRANSPORT_PROTOCOL_FAILURE,
            UsbFailureClassifier.classify(failure, descriptorClosedByUs = false),
        )
    }

    @Test
    fun `a handshake that ran out of time is named as such`() {
        assertEquals(
            UsbFailureKind.HANDSHAKE_TIMEOUT,
            UsbFailureClassifier.classify(
                TransportDisconnectedException("PLMX handshake failed", EOFException()),
                descriptorClosedByUs = true,
            ),
        )
    }

    @Test
    fun `a refused open is not confused with a broken session`() {
        assertEquals(
            UsbFailureKind.OPEN_ACCESSORY_FAILURE,
            UsbFailureClassifier.classify(
                TransportDisconnectedException(
                    "Android refused to open the USB accessory (already open, or a stale attach)",
                ),
                descriptorClosedByUs = false,
            ),
        )
    }

    @Test
    fun `a read that unwound because this app closed the fd is attributed to us`() {
        assertEquals(
            UsbFailureKind.DESCRIPTOR_CLOSED,
            UsbFailureClassifier.classify(IOException("read failed"), descriptorClosedByUs = true),
        )
    }

    @Test
    fun `a clean close carries no failure at all`() {
        assertEquals(
            UsbFailureKind.DESCRIPTOR_CLOSED,
            UsbFailureClassifier.classify(null, descriptorClosedByUs = false),
        )
    }

    @Test
    fun `an unattributable IO error stays unattributed`() {
        assertEquals(
            UsbFailureKind.UNKNOWN_TRANSPORT_FAILURE,
            UsbFailureClassifier.classify(
                IOException("I/O error"),
                descriptorClosedByUs = false,
            ),
        )
    }
}
