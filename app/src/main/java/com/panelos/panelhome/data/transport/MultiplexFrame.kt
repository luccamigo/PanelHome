package com.panelos.panelhome.data.transport

import java.nio.ByteBuffer
import java.nio.ByteOrder

internal object MultiplexFrame {
    const val MAGIC = 0x584D4C50
    const val VERSION = 1
    const val HEADER_SIZE = 16
    const val MAX_CONTROL_PAYLOAD = 1024 * 1024
    const val MAX_VIDEO_PAYLOAD = 16 * 1024 * 1024
    const val MAX_OTHER_PAYLOAD = 1024 * 1024

    /**
     * How much of a logical write goes into one frame. This is a scheduling
     * knob, not a wire limit: the peer validates against the maximum payload
     * above, so any fragment size below it stays protocol compatible. Smaller
     * fragments return control to the writer's priority check more often.
     */
    const val DEFAULT_FRAGMENT_BYTES = 64 * 1024

    /** One bulk max packet; fragments stay a whole number of packets. */
    const val MIN_FRAGMENT_BYTES = 512

    fun sanitizeFragmentBytes(value: Int): Int {
        val clamped = value.coerceIn(MIN_FRAGMENT_BYTES, MAX_VIDEO_PAYLOAD)
        return clamped - (clamped % MIN_FRAGMENT_BYTES)
    }

    /** Writes the header into [target] without allocating. */
    fun encodeHeader(
        target: ByteArray,
        channel: LogicalChannel,
        flags: Int,
        payloadLength: Int,
        sequence: Long,
    ) {
        require(target.size >= HEADER_SIZE) { "Header buffer is too small" }
        validateLength(channel, payloadLength)
        ByteBuffer.wrap(target, 0, HEADER_SIZE).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(MAGIC)
            put(VERSION.toByte())
            put(channel.id.toByte())
            putShort(flags.toShort())
            putInt(payloadLength)
            putInt(sequence.toInt())
        }
    }

    fun encodeHeader(
        channel: LogicalChannel,
        flags: Int,
        payloadLength: Int,
        sequence: Long,
    ): ByteArray = ByteArray(HEADER_SIZE).also { encodeHeader(it, channel, flags, payloadLength, sequence) }

    fun decodeHeader(bytes: ByteArray): Header {
        if (bytes.size != HEADER_SIZE) throw TransportProtocolException("Multiplex header must contain 16 bytes")
        val buffer = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        if (buffer.int != MAGIC) throw TransportProtocolException("Corrupted multiplex magic")
        val version = buffer.get().toInt() and 0xff
        if (version != VERSION) throw TransportProtocolException("Unsupported transport version $version")
        val channel = LogicalChannel.fromId(buffer.get().toInt() and 0xff)
        val flags = buffer.short.toInt() and 0xffff
        val payloadLength = buffer.int
        validateLength(channel, payloadLength)
        val sequence = buffer.int.toLong() and 0xffffffffL
        return Header(channel, flags, payloadLength, sequence)
    }

    private fun validateLength(channel: LogicalChannel, length: Int) {
        val maximum = maximumPayload(channel)
        if (length !in 0..maximum) {
            throw TransportProtocolException("Payload length $length exceeds $channel limit $maximum")
        }
    }

    fun maximumPayload(channel: LogicalChannel): Int =
        when (channel) {
            LogicalChannel.CONTROL -> MAX_CONTROL_PAYLOAD
            LogicalChannel.VIDEO -> MAX_VIDEO_PAYLOAD
            else -> MAX_OTHER_PAYLOAD
        }

    data class Header(
        val channel: LogicalChannel,
        val flags: Int,
        val payloadLength: Int,
        val sequence: Long,
    )
}
