package com.panelos.panelhome.data.transport

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransportSelectionTest {
    @Test
    fun `accessory attach selects the native carrier`() {
        assertTrue(
            TransportSelection.selectsNativeUsb(
                TransportSelection.USB_ACCESSORY_ATTACHED_ACTION,
                null,
            ),
        )
    }

    @Test
    fun `explicit usb extra selects the native carrier`() {
        assertTrue(TransportSelection.selectsNativeUsb("android.intent.action.MAIN", "usb"))
        assertTrue(TransportSelection.selectsNativeUsb("android.intent.action.MAIN", "USB"))
    }

    @Test
    fun `launcher and home intents stay on adb`() {
        assertFalse(TransportSelection.selectsNativeUsb("android.intent.action.MAIN", null))
        assertFalse(TransportSelection.selectsNativeUsb(null, null))
        assertFalse(TransportSelection.selectsNativeUsb("android.intent.action.MAIN", "adb"))
    }

    /**
     * The case that stranded the panel on ADB/TCP in the 2026-08-19 physical
     * run: the app was reinstalled while the accessory was already attached, so
     * it relaunched on a plain MAIN intent from the launcher and Android never
     * re-sent USB_ACCESSORY_ATTACHED. Same shape after a crash, after an app
     * update, or whenever the host is started before PanelHome.
     */
    @Test
    fun `already attached accessory selects the native carrier without an attach intent`() {
        assertTrue(
            TransportSelection.shouldUseNativeUsb(
                "android.intent.action.MAIN",
                null,
                accessoryAlreadyAttached = true,
            ),
        )
        assertTrue(
            TransportSelection.shouldUseNativeUsb(null, null, accessoryAlreadyAttached = true),
        )
    }

    @Test
    fun `no accessory keeps the session on adb`() {
        assertFalse(
            TransportSelection.shouldUseNativeUsb(
                "android.intent.action.MAIN",
                null,
                accessoryAlreadyAttached = false,
            ),
        )
    }

    /**
     * The ADB baseline run launches with `--es transport adb` while the tablet
     * is still physically attached. Without this the comparison would silently
     * become a USB measurement and the two transports could not be compared.
     */
    @Test
    fun `explicit adb extra wins over an attached accessory`() {
        assertFalse(
            TransportSelection.shouldUseNativeUsb(
                "android.intent.action.MAIN",
                "adb",
                accessoryAlreadyAttached = true,
            ),
        )
        assertFalse(
            TransportSelection.shouldUseNativeUsb(
                TransportSelection.USB_ACCESSORY_ATTACHED_ACTION,
                "adb",
                accessoryAlreadyAttached = true,
            ),
        )
    }

    /**
     * `onNewIntent` must keep asking the narrower question. If an attached
     * accessory made every intent look native, a HOME press would clear the
     * ViewModel store and recreate the Activity on a live session.
     */
    @Test
    fun `intent level selection ignores accessory presence`() {
        assertFalse(TransportSelection.selectsNativeUsb("android.intent.action.MAIN", null))
    }

    /**
     * The manifest filter and the constant have to name the same action; if they
     * drift, the accessory attach is delivered but never switches the carrier.
     */
    @Test
    fun `constant matches the manifest action`() {
        assertEquals(
            "android.hardware.usb.action.USB_ACCESSORY_ATTACHED",
            TransportSelection.USB_ACCESSORY_ATTACHED_ACTION,
        )
    }
}
