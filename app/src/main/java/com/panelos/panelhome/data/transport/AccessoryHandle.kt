package com.panelos.panelhome.data.transport

import android.os.ParcelFileDescriptor
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.util.concurrent.atomic.AtomicBoolean

/**
 * The single owner of one accessory descriptor, for exactly one physical session.
 *
 * `/dev/usb_accessory` is single-open, so every descriptor that is not released
 * is a reconnect that cannot happen. Ownership is therefore explicit and
 * unshared: this object closes the descriptor, nothing else does, and it does so
 * exactly once.
 *
 * The streams are built from the descriptor's [java.io.FileDescriptor], which
 * does not transfer ownership. They deliberately have **no** independent
 * lifecycle: closing either of them would close the same fd a second time, and a
 * double close can land on a descriptor number the runtime has already recycled
 * - which is how a later `openAccessory` ends up refusing forever.
 *
 * Nothing here flushes. After a cable pull a write to the gadget can block below
 * the USB stack and never return, so the teardown boundary is the descriptor
 * close and only the descriptor close.
 */
internal class AccessoryHandle(
    val input: InputStream,
    val output: OutputStream,
    private val closeDescriptor: () -> Unit,
) {
    private val closed = AtomicBoolean()

    val isClosed: Boolean get() = closed.get()

    /** Idempotent. Only the first caller reaches [closeDescriptor]. */
    fun close() {
        if (!closed.compareAndSet(false, true)) return
        runCatching(closeDescriptor)
    }

    companion object {
        fun of(descriptor: ParcelFileDescriptor): AccessoryHandle = AccessoryHandle(
            input = FileInputStream(descriptor.fileDescriptor),
            output = FileOutputStream(descriptor.fileDescriptor),
            closeDescriptor = { descriptor.close() },
        )
    }
}
