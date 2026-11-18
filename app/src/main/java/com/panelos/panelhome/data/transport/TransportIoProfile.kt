package com.panelos.panelhome.data.transport

import java.io.File
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Physical-layer counters for the accessory carrier. They exist so transport CPU
 * can be attributed to concrete I/O behaviour - syscall rate, transfer size and
 * framing - instead of being inferred from a process-wide percentage that also
 * contains UI, decoder and benchmark work.
 *
 * Every counter is an atomic add on the reader or writer thread, so the hot path
 * cost is a few nanoseconds against a syscall that costs microseconds. Thread CPU
 * is not sampled in the loop at all: only the thread ids are recorded, and
 * `/proc` is read on demand when a snapshot is requested.
 */
class TransportIoProfile {
    private val readCallsValue = AtomicLong()
    private val readBytesValue = AtomicLong()
    private val readShortValue = AtomicLong()
    private val stagedCopyBytesValue = AtomicLong()
    private val directReadsValue = AtomicLong()
    private val writeCallsValue = AtomicLong()
    private val writeBytesValue = AtomicLong()
    private val framesTxValue = AtomicLong()
    private val framesRxValue = AtomicLong()
    private val coalescedFramesValue = AtomicLong()
    private val splitFramesValue = AtomicLong()
    private val fragmentsTxValue = AtomicLong()
    private val payloadRentalsValue = AtomicLong()
    private val payloadReturnsValue = AtomicLong()
    private val payloadAllocationsValue = AtomicLong()
    private val readerThreadId = AtomicInteger()
    private val writerThreadId = AtomicInteger()

    val readCalls get() = readCallsValue.get()
    val readBytes get() = readBytesValue.get()
    val shortReads get() = readShortValue.get()
    val stagedCopyBytes get() = stagedCopyBytesValue.get()
    val directReads get() = directReadsValue.get()
    val writeCalls get() = writeCallsValue.get()
    val writeBytes get() = writeBytesValue.get()
    val framesTx get() = framesTxValue.get()
    val framesRx get() = framesRxValue.get()
    val coalescedFrames get() = coalescedFramesValue.get()
    val splitFrames get() = splitFramesValue.get()
    val fragmentsTx get() = fragmentsTxValue.get()
    val payloadRentals get() = payloadRentalsValue.get()
    val payloadReturns get() = payloadReturnsValue.get()
    val payloadAllocations get() = payloadAllocationsValue.get()

    internal fun physicalRead(bytes: Int, staged: Boolean) {
        readCallsValue.incrementAndGet()
        if (bytes > 0) readBytesValue.addAndGet(bytes.toLong())
        if (bytes in 1 until MIN_FULL_READ) readShortValue.incrementAndGet()
        if (!staged) directReadsValue.incrementAndGet()
    }

    internal fun stagedCopy(bytes: Int) = stagedCopyBytesValue.addAndGet(bytes.toLong())

    internal fun physicalWrite(bytes: Int) {
        writeCallsValue.incrementAndGet()
        writeBytesValue.addAndGet(bytes.toLong())
    }

    internal fun frameSent(coalesced: Boolean) {
        framesTxValue.incrementAndGet()
        if (coalesced) coalescedFramesValue.incrementAndGet() else splitFramesValue.incrementAndGet()
    }

    internal fun frameReceived() = framesRxValue.incrementAndGet()
    internal fun fragmentSent() = fragmentsTxValue.incrementAndGet()
    internal fun payloadRented() = payloadRentalsValue.incrementAndGet()
    internal fun payloadReturned() = payloadReturnsValue.incrementAndGet()
    internal fun payloadAllocated() = payloadAllocationsValue.incrementAndGet()
    internal fun bindReaderThread(tid: Int) = readerThreadId.set(tid)
    internal fun bindWriterThread(tid: Int) = writerThreadId.set(tid)

    /** Reader thread CPU milliseconds, or -1 when `/proc` is unreadable. */
    val readerCpuMillis get() = threadCpuMillis(readerThreadId.get())

    /** Writer thread CPU milliseconds, or -1 when `/proc` is unreadable. */
    val writerCpuMillis get() = threadCpuMillis(writerThreadId.get())

    private companion object {
        /** Reads below one bulk max packet indicate a header-sized syscall. */
        const val MIN_FULL_READ = 512

        /**
         * utime and stime are fields 14 and 15 of `/proc/<pid>/task/<tid>/stat`,
         * expressed in clock ticks. Android's USER_HZ is 100 on every supported
         * ABI, so a tick is 10 ms.
         */
        const val MILLIS_PER_CLOCK_TICK = 10L

        fun threadCpuMillis(tid: Int): Long {
            if (tid <= 0) return -1
            return runCatching {
                val stat = File("/proc/self/task/$tid/stat").readText()
                // The second field is the thread name and may itself contain
                // spaces, so field counting has to start after its closing ')'.
                val fields = stat.substring(stat.lastIndexOf(')') + 2).split(' ')
                (fields[11].toLong() + fields[12].toLong()) * MILLIS_PER_CLOCK_TICK
            }.getOrDefault(-1)
        }
    }
}

/**
 * Fixed-size buffer pool for received frame payloads.
 *
 * Ownership is explicit and single-writer: the reader loop rents, and either the
 * consuming logical channel or the reader itself recycles exactly once. A buffer
 * is never recycled while a consumer can still observe it, and a payload larger
 * than the pooled size simply falls back to a plain allocation rather than
 * growing the pool without bound.
 */
internal class PayloadBufferPool(
    private val bufferBytes: Int,
    private val maximumBuffers: Int,
    private val profile: TransportIoProfile,
) {
    private val free = ArrayDeque<ByteArray>(maximumBuffers)

    fun rent(size: Int): PooledPayload {
        // Handshake, OPEN and FIN frames carry no payload and must not consume
        // a pooled buffer.
        if (size == 0) return PooledPayload(EMPTY, 0, pool = null)
        if (size > bufferBytes) {
            profile.payloadAllocated()
            return PooledPayload(ByteArray(size), size, pool = null)
        }
        val buffer = synchronized(free) { free.removeLastOrNull() }
        if (buffer == null) profile.payloadAllocated() else profile.payloadRented()
        return PooledPayload(buffer ?: ByteArray(bufferBytes), size, this)
    }

    fun recycle(buffer: ByteArray) {
        if (buffer.size != bufferBytes) return
        val accepted = synchronized(free) {
            if (free.size >= maximumBuffers) false else { free.addLast(buffer); true }
        }
        if (accepted) profile.payloadReturned()
    }

    fun clear() = synchronized(free) { free.clear() }

    private companion object {
        val EMPTY = ByteArray(0)
    }
}

/**
 * A received payload with exactly one owner. [length] rather than the array size
 * is the payload extent, because a pooled array is deliberately larger.
 */
internal class PooledPayload(
    @JvmField val bytes: ByteArray,
    @JvmField val length: Int,
    private val pool: PayloadBufferPool?,
    private val reservation: AutoCloseable? = null,
) {
    private val released = java.util.concurrent.atomic.AtomicBoolean()

    fun release() {
        if (!released.compareAndSet(false, true)) return
        reservation?.close()
        pool?.recycle(bytes)
    }
}
