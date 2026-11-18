package com.panelos.panelhome.data.transport

import android.hardware.usb.UsbManager

/**
 * Decides which carrier an Activity intent asks for.
 *
 * The decision is kept away from the Activity so it can be tested without a
 * device: the accessory attach is the only thing that puts normal use on the
 * native carrier, and getting it wrong silently leaves the panel on ADB/TCP
 * while the host writes into a bulk endpoint nobody drains.
 */
object TransportSelection {
    /** Mirrors [UsbManager.ACTION_USB_ACCESSORY_ATTACHED] and the manifest filter. */
    const val USB_ACCESSORY_ATTACHED_ACTION = UsbManager.ACTION_USB_ACCESSORY_ATTACHED

    fun selectsNativeUsb(action: String?, transportExtra: String?): Boolean =
        transportExtra.equals("usb", ignoreCase = true) ||
            action == USB_ACCESSORY_ATTACHED_ACTION

    /**
     * Whether this *session* should run on the native carrier.
     *
     * [selectsNativeUsb] answers a narrower question - does this intent ask for
     * the native carrier - and that is all `onNewIntent` may act on. It is not
     * enough to decide the carrier at startup: Android delivers
     * `USB_ACCESSORY_ATTACHED` only on a fresh attach, so a process that starts
     * while the accessory is *already* attached never sees the action at all.
     * That happens on an app update, after a crash, and whenever the host is
     * started before PanelHome - and the panel then sits on ADB/TCP forever
     * while the host writes into a bulk endpoint nobody drains.
     *
     * An explicit `transport=adb` still wins, so the ADB baseline run stays an
     * ADB measurement even with an accessory attached.
     */
    fun shouldUseNativeUsb(
        action: String?,
        transportExtra: String?,
        accessoryAlreadyAttached: Boolean,
    ): Boolean {
        if (transportExtra.equals("adb", ignoreCase = true)) return false
        return selectsNativeUsb(action, transportExtra) || accessoryAlreadyAttached
    }

    /**
     * True when any accessory is attached, permitted or not.
     *
     * Permission used to be part of this question, on the grounds that the
     * native transport could only fail without it. It no longer is: the recovery
     * lifecycle requests permission and then opens, so requiring approval up
     * front would send a panel whose accessory is merely unapproved onto
     * ADB/TCP - where it would stay even after the operator approved it.
     */
    fun hasAccessory(usbManager: UsbManager?): Boolean =
        usbManager?.accessoryList?.isNotEmpty() == true
}
