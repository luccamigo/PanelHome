package com.panelos.panelhome.data.transport

import java.io.EOFException
import java.io.InputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.SecureRandom

/**
 * PLMX transport protocol version 2.
 *
 * v1 had no session identity, so a byte stream reopened over a device still in
 * accessory mode could deliver the previous session's frames to the new one.
 * Those frames decoded correctly - they were valid v1 frames - and the new
 * session then failed on "data arrived for unopened channel". v2 fixes that at
 * the protocol level: every frame carries a tag derived from a 128-bit session
 * nonce, and a session refuses to interpret anything until it has completed an
 * explicit HELLO/HELLO_ACK exchange for that exact nonce.
 *
 * This is a separation mechanism, not a security mechanism. A random session id
 * distinguishes sessions and rejects stale or accidentally replayed frames; it
 * does not authenticate the peer.
 */
object PlmxVersions {
    /**
     * Native USB speaks v2 only. v1 remains implemented and tested for the
     * shared fixtures, but a session never negotiates down to it: silently
     * accepting v1 would reintroduce exactly the identity-free framing that
     * stale bytes exploit.
     */
    const val MINIMUM = 2
    const val MAXIMUM = 2

    fun isSupported(version: Int) = version in MINIMUM..MAXIMUM
}

enum class PlmxFrameType(val value: Int) {
    DATA(0x0000),
    HELLO(0x0001),
    OPEN(0x0002),
    FIN(0x0004),
    HELLO_ACK(0x0008);

    companion object {
        fun fromValue(value: Int): PlmxFrameType? = entries.firstOrNull { it.value == value }
    }
}

/**
 * 128 bits of cryptographic randomness identifying one physical session.
 * Deliberately not derived from a clock, a process id, a counter or a device
 * serial: those repeat across reconnects, which is the case that has to be
 * distinguishable.
 */
@JvmInline
value class PlmxSessionId(val bytes: ByteArray) {
    init {
        require(bytes.size == SIZE) { "A session id needs $SIZE bytes" }
    }

    /**
     * The first four bytes, carried in every frame header. 32 bits is enough to
     * separate consecutive sessions while keeping per-frame overhead at four
     * bytes.
     */
    val tag: Int
        get() = (bytes[0].toInt() and 0xff) or
            ((bytes[1].toInt() and 0xff) shl 8) or
            ((bytes[2].toInt() and 0xff) shl 16) or
            ((bytes[3].toInt() and 0xff) shl 24)

    val isEmpty: Boolean get() = bytes.all { it.toInt() == 0 }

    /** Truncated form for logs. The full id is never logged. */
    val prefix: String
        get() = if (isEmpty) "<none>" else buildString {
            for (index in 0 until 3) append("%02X".format(bytes[index]))
            append("...")
        }

    fun contentEquals(other: PlmxSessionId) = bytes.contentEquals(other.bytes)

    companion object {
        const val SIZE = 16
        private val random = SecureRandom()

        val EMPTY = PlmxSessionId(ByteArray(SIZE))

        fun create(): PlmxSessionId = PlmxSessionId(ByteArray(SIZE).also(random::nextBytes))

        fun read(source: ByteArray, offset: Int): PlmxSessionId =
            PlmxSessionId(source.copyOfRange(offset, offset + SIZE))
    }
}

data class PlmxFrameHeader(
    val channel: LogicalChannel,
    val type: PlmxFrameType,
    val payloadLength: Int,
    val sequence: Int,
    val sessionTag: Int,
)

data class PlmxHello(
    val transportVersion: Int,
    val capabilities: Int,
    val sessionId: PlmxSessionId,
)

object PlmxFrame {
    const val MAGIC = 0x584D4C50
    const val VERSION = 2

    /**
     * 24 bytes: the v1 header plus a session tag and four reserved bytes. The
     * reserved field must be zero, which both keeps room for a future
     * authentication tag selector and adds 32 more bits of structure that a
     * random or stale byte run has to match before it can be mistaken for a
     * frame boundary.
     */
    const val HEADER_SIZE = 24
    /**
     * 32 bytes: "PNTR", the transport version, the capability flags, the
     * 128-bit nonce, and four reserved bytes that must be zero. The reserved
     * tail is where a future authenticated handshake can put a key selector
     * without changing the frame size.
     */
    const val HELLO_PAYLOAD_SIZE = 32
    const val MAGIC_SIZE = 4

    private const val RESERVED_OFFSET = 20
    private const val HELLO_RESERVED_OFFSET = 28
    private val HELLO_MAGIC = byteArrayOf('P'.code.toByte(), 'N'.code.toByte(), 'T'.code.toByte(), 'R'.code.toByte())

    fun encodeHeader(
        target: ByteArray,
        channel: LogicalChannel,
        type: PlmxFrameType,
        payloadLength: Int,
        sequence: Int,
        sessionTag: Int,
    ) {
        require(target.size >= HEADER_SIZE) { "Header buffer is too small" }
        validateLength(channel, payloadLength)
        ByteBuffer.wrap(target, 0, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(MAGIC)
            put(VERSION.toByte())
            put(channel.id.toByte())
            putShort(type.value.toShort())
            putInt(payloadLength)
            putInt(sequence)
            putInt(sessionTag)
            putInt(0)
        }
    }

    fun encodeHeader(
        channel: LogicalChannel,
        type: PlmxFrameType,
        payloadLength: Int,
        sequence: Int,
        sessionTag: Int,
    ): ByteArray = ByteArray(HEADER_SIZE).also {
        encodeHeader(it, channel, type, payloadLength, sequence, sessionTag)
    }

    /**
     * Non-throwing decode used by the resync scanner, which must be able to
     * reject a candidate cheaply and move on rather than fail a session.
     */
    fun tryDecodeHeader(source: ByteArray, offset: Int): PlmxFrameHeader? {
        if (source.size - offset < HEADER_SIZE) return null
        val buffer = ByteBuffer.wrap(source, offset, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN)
        if (buffer.int != MAGIC) return null
        val version = buffer.get().toInt() and 0xff
        if (!PlmxVersions.isSupported(version)) return null
        // The channel byte is read once and then matched: reading it inside the
        // predicate would advance the buffer on every candidate entry.
        val channelId = buffer.get().toInt() and 0xff
        val channel = LogicalChannel.entries.firstOrNull { it.id == channelId } ?: return null
        val type = PlmxFrameType.fromValue(buffer.short.toInt() and 0xffff) ?: return null
        val payloadLength = buffer.int
        if (payloadLength < 0 || payloadLength > maximumPayload(channel)) return null
        if (!isPayloadLengthValidFor(type, payloadLength)) return null
        val sequence = buffer.int
        val sessionTag = buffer.int
        val reserved = ByteBuffer.wrap(source, offset + RESERVED_OFFSET, 4).order(ByteOrder.LITTLE_ENDIAN).int
        if (reserved != 0) return null
        return PlmxFrameHeader(channel, type, payloadLength, sequence, sessionTag)
    }

    fun decodeHeader(source: ByteArray, offset: Int = 0): PlmxFrameHeader =
        tryDecodeHeader(source, offset) ?: throw TransportProtocolException("Malformed PLMX v2 header")

    /**
     * Frame types with a fixed payload shape are validated here so the scanner
     * can reject a false boundary before it consumes a bogus payload length.
     */
    fun isPayloadLengthValidFor(type: PlmxFrameType, payloadLength: Int) = when (type) {
        PlmxFrameType.HELLO, PlmxFrameType.HELLO_ACK -> payloadLength == HELLO_PAYLOAD_SIZE
        PlmxFrameType.OPEN, PlmxFrameType.FIN -> payloadLength == 0
        PlmxFrameType.DATA -> payloadLength >= 0
    }

    fun encodeHello(target: ByteArray, capabilities: Int, sessionId: PlmxSessionId) {
        require(target.size >= HELLO_PAYLOAD_SIZE) { "HELLO payload needs $HELLO_PAYLOAD_SIZE bytes" }
        ByteBuffer.wrap(target, 0, HELLO_PAYLOAD_SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
            put(HELLO_MAGIC)
            putInt(VERSION)
            putInt(capabilities)
            put(sessionId.bytes)
            putInt(0)
        }
    }

    fun encodeHello(capabilities: Int, sessionId: PlmxSessionId): ByteArray =
        ByteArray(HELLO_PAYLOAD_SIZE).also { encodeHello(it, capabilities, sessionId) }

    fun tryDecodeHello(payload: ByteArray, offset: Int, length: Int): PlmxHello? {
        if (length != HELLO_PAYLOAD_SIZE) return null
        for (index in HELLO_MAGIC.indices) {
            if (payload[offset + index] != HELLO_MAGIC[index]) return null
        }
        val buffer = ByteBuffer.wrap(payload, offset + 4, 8).order(ByteOrder.LITTLE_ENDIAN)
        val version = buffer.int
        if (version != VERSION) return null
        val capabilities = buffer.int
        val reserved = ByteBuffer.wrap(payload, offset + HELLO_RESERVED_OFFSET, 4)
            .order(ByteOrder.LITTLE_ENDIAN).int
        if (reserved != 0) return null
        val sessionId = PlmxSessionId.read(payload, offset + 12)
        // An all-zero id means the peer did not generate one, which would defeat
        // the whole mechanism.
        if (sessionId.isEmpty) return null
        return PlmxHello(version, capabilities, sessionId)
    }

    /**
     * A candidate is only a real handshake when the header and the payload
     * agree. Matching the four magic bytes alone is not enough: a previous
     * session's payload can contain them.
     */
    fun readHandshake(
        header: PlmxFrameHeader,
        buffer: ByteArray,
        payloadOffset: Int,
        expected: PlmxFrameType,
    ): PlmxHello? {
        if (header.type != expected) return null
        if (header.channel != LogicalChannel.TRANSPORT) return null
        if (header.payloadLength != HELLO_PAYLOAD_SIZE) return null
        val hello = tryDecodeHello(buffer, payloadOffset, header.payloadLength) ?: return null
        if (hello.sessionId.tag != header.sessionTag) return null
        return hello
    }

    fun validateLength(channel: LogicalChannel, length: Int) {
        val maximum = maximumPayload(channel)
        if (length !in 0..maximum) {
            throw TransportProtocolException("Payload length $length exceeds $channel limit $maximum")
        }
    }

    // The Hardening Wave 1 payload ceilings are unchanged. Session identity and
    // resync must never become a way around them.
    fun maximumPayload(channel: LogicalChannel): Int = MultiplexFrame.maximumPayload(channel)
}

/** Explicit carrier lifecycle, replacing a set of independent flags. */
enum class PlmxSessionState {
    DISCONNECTED,
    OPENING_PHYSICAL_TRANSPORT,
    SEARCHING_FOR_HANDSHAKE,
    HANDSHAKING,
    ESTABLISHED,
    CLOSING,
    RECOVERING,
}

/**
 * Bounds on how much rubbish a resync may absorb before the carrier gives up and
 * reopens. Without them a peer that never speaks PLMX would keep a reader
 * spinning against an endless stream.
 */
data class PlmxResyncLimits(
    val maximumBytes: Int = 1024 * 1024,
    val maximumDurationMillis: Long = 10_000,
)

class PlmxResyncException(message: String, val discardedBytes: Long) : java.io.IOException(message)

/**
 * The single reader in front of the physical stream. It owns frame-aligned reads
 * and the resynchronization search, so no other code has to reason about where a
 * frame starts.
 *
 * Refills always request the whole staging buffer, which is sized to the
 * accessory gadget's bulk buffer, so the posted USB request stays a whole number
 * of max packets and the host is free to coalesce a header with its payload.
 */
internal class PlmxStreamReader(
    private val input: InputStream,
    private val profile: TransportIoProfile,
    stagingBytes: Int = DEFAULT_STAGING_BYTES,
    private val limits: PlmxResyncLimits = PlmxResyncLimits(),
) {
    private val staging = ByteArray(
        maxOf(stagingBytes, (PlmxFrame.HEADER_SIZE + PlmxFrame.HELLO_PAYLOAD_SIZE) * 2),
    )
    private var offset = 0
    private var available = 0

    var discardedBytes = 0L
        private set

    fun readFully(target: ByteArray, targetOffset: Int, count: Int) {
        var written = 0
        while (written < count) {
            if (available == 0) {
                val remaining = count - written
                if (remaining >= staging.size) {
                    // A tail this large is read straight into the destination.
                    // The request is at least one staging buffer, so the kernel
                    // still posts a whole number of max packets.
                    val read = input.read(target, targetOffset + written, remaining)
                    profile.physicalRead(read, staged = false)
                    if (read < 0) throw EOFException("Transport ended after $written of $count bytes")
                    written += read
                    continue
                }
                fill(written, count)
            }
            val chunk = minOf(count - written, available)
            staging.copyInto(target, targetOffset + written, offset, offset + chunk)
            offset += chunk
            available -= chunk
            written += chunk
            profile.stagedCopy(chunk)
        }
    }

    /**
     * Reads the next frame header.
     *
     * A header that does not decode is deliberately left in the buffer. If the
     * bytes were consumed, a resync starting from the next position could have
     * eaten the head of the very handshake it needs to find - which is exactly
     * the case where the previous session ended mid-frame.
     */
    fun readHeader(): PlmxFrameHeader {
        while (available < PlmxFrame.HEADER_SIZE) compactAndFill()
        val header = PlmxFrame.tryDecodeHeader(staging, offset)
            ?: throw TransportProtocolException("Malformed PLMX v2 header")
        offset += PlmxFrame.HEADER_SIZE
        available -= PlmxFrame.HEADER_SIZE
        return header
    }

    /**
     * Scans forward until a frame of [expected] decodes completely and [accept]
     * agrees, discarding everything before it. Everything after the match stays
     * buffered, so the caller continues reading normal frames with no gap.
     *
     * This is what makes reconnect deterministic: the session does not need to
     * know how many bytes the previous session left behind.
     */
    fun searchHandshake(
        expected: PlmxFrameType,
        accept: (PlmxHello) -> Boolean,
    ): PlmxHello {
        val startedAt = System.nanoTime()
        discardedBytes = 0
        val candidateSize = PlmxFrame.HEADER_SIZE + PlmxFrame.HELLO_PAYLOAD_SIZE
        while (true) {
            if (discardedBytes > limits.maximumBytes) {
                throw PlmxResyncException(
                    "Resync discarded $discardedBytes bytes without finding $expected",
                    discardedBytes,
                )
            }
            if ((System.nanoTime() - startedAt) / 1_000_000 > limits.maximumDurationMillis) {
                throw PlmxResyncException(
                    "Resync exceeded ${limits.maximumDurationMillis} ms without finding $expected",
                    discardedBytes,
                )
            }

            if (available < candidateSize) {
                val partial = indexOfMagic()
                if (partial > 0) discard(partial)
                compactAndFill()
                continue
            }

            val found = indexOfMagic()
            if (found < 0) {
                // Keep the last three bytes: the magic may straddle the boundary.
                discard(available - minOf(PlmxFrame.MAGIC_SIZE - 1, available))
                compactAndFill()
                continue
            }
            if (found > 0) {
                discard(found)
                continue
            }
            if (available < candidateSize) {
                compactAndFill()
                continue
            }

            val header = PlmxFrame.tryDecodeHeader(staging, offset)
            val hello = header?.let {
                PlmxFrame.readHandshake(it, staging, offset + PlmxFrame.HEADER_SIZE, expected)
            }
            if (hello != null && accept(hello)) {
                offset += candidateSize
                available -= candidateSize
                return hello
            }

            // The magic matched but the frame did not. That is exactly the
            // "magic inside a stale payload" case, so step over this byte and
            // keep looking instead of trusting it.
            discard(1)
        }
    }

    private fun indexOfMagic(): Int {
        var index = offset
        val end = offset + available - (PlmxFrame.MAGIC_SIZE - 1)
        while (index < end) {
            if (staging[index] == MAGIC_0 &&
                staging[index + 1] == MAGIC_1 &&
                staging[index + 2] == MAGIC_2 &&
                staging[index + 3] == MAGIC_3
            ) {
                return index - offset
            }
            index++
        }
        return -1
    }

    private fun discard(count: Int) {
        if (count <= 0) return
        offset += count
        available -= count
        discardedBytes += count
    }

    private fun compactAndFill() {
        if (offset > 0) {
            if (available > 0) staging.copyInto(staging, 0, offset, offset + available)
            offset = 0
        }
        if (available == staging.size) throw TransportProtocolException("PLMX resync buffer is full")
        val read = input.read(staging, available, staging.size - available)
        profile.physicalRead(read, staged = true)
        if (read < 0) throw EOFException("Transport ended during resync")
        available += read
    }

    private fun fill(written: Int, count: Int) {
        offset = 0
        while (available == 0) {
            val read = input.read(staging, 0, staging.size)
            profile.physicalRead(read, staged = true)
            if (read < 0) throw EOFException("Transport ended after $written of $count bytes")
            available = read
        }
    }

    private companion object {
        /**
         * Matches BULK_BUFFER_SIZE in the kernel's f_accessory gadget.
         */
        const val DEFAULT_STAGING_BYTES = 16 * 1024
        const val MAGIC_0 = 'P'.code.toByte()
        const val MAGIC_1 = 'L'.code.toByte()
        const val MAGIC_2 = 'M'.code.toByte()
        const val MAGIC_3 = 'X'.code.toByte()
    }
}
