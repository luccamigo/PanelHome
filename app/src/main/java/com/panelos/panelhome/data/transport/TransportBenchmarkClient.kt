package com.panelos.panelhome.data.transport

import android.os.Debug
import android.os.Process
import android.os.SystemClock
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

/** Responds to the opt-in host benchmark without changing Panel Protocol. */
class TransportBenchmarkClient(
    private val transport: PanelTransport,
    private val log: (String) -> Unit,
) {
    private var uploadPayload = EMPTY

    suspend fun run() = withContext(Dispatchers.IO) {
        while (true) {
            currentCoroutineContext().ensureActive()
            try {
                transport.connect(LogicalChannel.ASSETS).use(::runSession)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                log("[TransportBenchmark] disconnected: ${error::class.java.simpleName}: ${error.message}")
            }
            delay(RECONNECT_DELAY_MILLIS)
        }
    }

    private fun runSession(connection: PanelByteConnection) {
        log("[TransportBenchmark] responder connected over ${transport.name}")
        val reader = FrameReader()
        while (!connection.isClosed) {
            val frame = reader.read(connection.input)
            when (frame.command) {
                Command.ECHO -> writeFrame(connection.output, Command.ECHO_RESPONSE, frame.payload, frame.length)
                Command.DOWNLOAD -> writeFrame(connection.output, Command.ACKNOWLEDGEMENT, EMPTY, 0)
                Command.UPLOAD_REQUEST -> {
                    if (frame.length != Int.SIZE_BYTES) error("Invalid upload request")
                    val size = ByteBuffer.wrap(frame.payload, 0, frame.length)
                        .order(ByteOrder.LITTLE_ENDIAN).int
                    require(size in 0..MAXIMUM_PAYLOAD)
                    writeFrame(connection.output, Command.UPLOAD_DATA, deterministicPayload(size), size)
                }
                Command.STATS_REQUEST -> stats().let { writeFrame(connection.output, Command.STATS_RESPONSE, it, it.size) }
                Command.FINISH -> return
                else -> error("Unexpected benchmark command ${frame.command}")
            }
        }
    }

    /**
     * Transport statistics plus the physical I/O counters. The harness needs the
     * counters to tell transport CPU apart from application CPU; without them
     * the only Android number available is the whole process, which also
     * contains Compose, telemetry and the decoder.
     */
    private fun stats(): ByteArray {
        val runtime = Runtime.getRuntime()
        val io = transport.metrics.io
        return ByteBuffer.allocate(STATS_BYTES).order(ByteOrder.LITTLE_ENDIAN).apply {
            putLong(SystemClock.elapsedRealtime())
            putLong(Process.getElapsedCpuTime())
            putLong(Debug.getPss().toLong())
            putLong(runtime.totalMemory() - runtime.freeMemory())
            putLong(io.readCalls)
            putLong(io.readBytes)
            putLong(io.writeCalls)
            putLong(io.writeBytes)
            putLong(io.framesTx)
            putLong(io.framesRx)
            putLong(io.shortReads)
            putLong(io.payloadAllocations)
            putLong(io.readerCpuMillis)
            putLong(io.writerCpuMillis)
            putLong(io.coalescedFrames)
            putLong(io.splitFrames)
        }.array()
    }

    /**
     * The upload payload is deterministic and depends only on its size, so it is
     * generated once and reused. Regenerating 32 MiB byte by byte on every
     * repetition charged the tablet's CPU budget to the transport under test.
     */
    private fun deterministicPayload(size: Int): ByteArray {
        if (uploadPayload.size < size) {
            uploadPayload = ByteArray(size) { index -> (index * 31 + 17).toByte() }
        }
        return uploadPayload
    }

    /** Reuses one growing buffer instead of allocating per benchmark frame. */
    private class FrameReader {
        private val header = ByteArray(HEADER_SIZE)
        private var payload = ByteArray(INITIAL_PAYLOAD_BYTES)

        fun read(input: InputStream): Frame {
            readExactly(input, header, HEADER_SIZE)
            val values = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN)
            if (values.int != MAGIC) error("Invalid benchmark magic")
            if (values.get().toInt() and 0xff != VERSION) error("Unsupported benchmark version")
            val command = Command.fromId(values.get().toInt() and 0xff)
            values.short // reserved
            val length = values.int
            require(length in 0..MAXIMUM_PAYLOAD)
            if (payload.size < length) payload = ByteArray(length)
            readExactly(input, payload, length)
            return Frame(command, payload, length)
        }

        private fun readExactly(input: InputStream, target: ByteArray, count: Int) {
            var offset = 0
            while (offset < count) {
                val read = input.read(target, offset, count - offset)
                if (read < 0) throw EOFException("Benchmark transport ended at $offset/$count")
                offset += read
            }
        }
    }

    /**
     * Header and payload go out as one logical write so the carrier sees one
     * frame per benchmark message instead of two. Bulk payloads keep the header
     * separate: staging tens of megabytes only to save one small write would
     * double the responder's peak memory on a low-end tablet.
     */
    private fun writeFrame(output: OutputStream, command: Command, payload: ByteArray, length: Int) {
        ByteBuffer.wrap(frameBuffer, 0, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(MAGIC)
            put(VERSION.toByte())
            put(command.id.toByte())
            putShort(0)
            putInt(length)
        }
        if (length <= COALESCE_LIMIT) {
            payload.copyInto(frameBuffer, HEADER_SIZE, 0, length)
            output.write(frameBuffer, 0, HEADER_SIZE + length)
        } else {
            output.write(frameBuffer, 0, HEADER_SIZE)
            output.write(payload, 0, length)
        }
        output.flush()
    }

    private val frameBuffer = ByteArray(HEADER_SIZE + COALESCE_LIMIT)

    private class Frame(val command: Command, val payload: ByteArray, val length: Int)

    private enum class Command(val id: Int) {
        ECHO(1),
        ECHO_RESPONSE(2),
        DOWNLOAD(3),
        ACKNOWLEDGEMENT(4),
        UPLOAD_REQUEST(5),
        UPLOAD_DATA(6),
        STATS_REQUEST(7),
        STATS_RESPONSE(8),
        FINISH(9);

        companion object {
            private val byId = arrayOfNulls<Command>(FINISH.id + 1).also { table ->
                entries.forEach { table[it.id] = it }
            }

            fun fromId(id: Int) = byId.getOrNull(id) ?: error("Unknown benchmark command $id")
        }
    }

    private companion object {
        const val MAGIC = 0x4B4D4250
        const val VERSION = 1
        const val HEADER_SIZE = 12
        const val STATS_BYTES = 128
        const val COALESCE_LIMIT = 64 * 1024
        const val INITIAL_PAYLOAD_BYTES = 64 * 1024
        const val MAXIMUM_PAYLOAD = 64 * 1024 * 1024
        const val RECONNECT_DELAY_MILLIS = 1_000L
        val EMPTY = ByteArray(0)
    }
}
