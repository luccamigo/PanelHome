package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoSurfaceLifecyclePolicy
import com.panelos.panelhome.domain.video.VideoSurfaceTransportAction

import java.nio.ByteBuffer
import java.nio.ByteOrder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class H264VideoProtocolTest {
    @Test
    fun `handshake accepts H264 selection`() {
        val bytes = handshake(VideoProtocol.CODEC_H264, 1280, 800, 30)

        val accepted = VideoProtocol.parseHandshake(bytes) as VideoHandshakeResponse.Accepted

        assertEquals(VideoProtocol.CODEC_H264, accepted.codec)
        assertEquals(1280, accepted.maxWidth)
        assertEquals(800, accepted.maxHeight)
        assertEquals(30, accepted.maxFps)
    }

    @Test
    fun `packet parser preserves config keyframe sequence and timing`() {
        val flags = VideoProtocol.H264_FLAG_CONFIG or
            VideoProtocol.H264_FLAG_KEYFRAME or
            VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT
        val bytes = packetHeader(
            sequence = 77,
            timestamp = 123_456,
            flags = flags,
            payloadBytes = 4096,
            encodeMicros = 2_500,
        )

        val header = VideoProtocol.parseH264PacketHeader(bytes)

        assertEquals(77, header.sequence)
        assertEquals(123_456, header.captureTimestampMicros)
        assertEquals(4096, header.payloadBytes)
        assertEquals(2_500, header.encodeDurationMicros)
        assertTrue(header.isConfig)
        assertTrue(header.isKeyFrame)
        assertTrue(header.isEndOfAccessUnit)
    }

    @Test(expected = VideoProtocolException::class)
    fun `packet parser rejects oversized payload`() {
        VideoProtocol.parseH264PacketHeader(
            packetHeader(
                sequence = 1,
                timestamp = 1,
                flags = VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
                payloadBytes = VideoProtocol.MAX_H264_PAYLOAD_BYTES + 1,
                encodeMicros = 1,
            ),
        )
    }

    @Test
    fun `decoder gate queues config before first keyframe and then accepts P frames`() {
        val gate = H264DecoderPacketGate()
        val config = packet(1, VideoProtocol.H264_FLAG_CONFIG)
        val pFrame = packet(1, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT)
        val keyFrame = packet(
            2,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )

        assertTrue(gate.accept(config).isEmpty())
        gate.onDecoderReady()
        assertTrue(gate.accept(pFrame).isEmpty())
        assertEquals(listOf(config, keyFrame), gate.accept(keyFrame))
        val acceptedPFrame = gate.accept(
            packet(3, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT),
        )
        assertEquals(1, acceptedPFrame.size)
        assertEquals(3, acceptedPFrame.single().header.sequence)
    }

    @Test
    fun `surface recreation waits for another keyframe without losing config`() {
        val gate = H264DecoderPacketGate()
        val config = packet(1, VideoProtocol.H264_FLAG_CONFIG)
        val firstKey = packet(
            1,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )
        gate.accept(config)
        assertTrue(gate.onDecoderReady().isEmpty())
        assertEquals(2, gate.accept(firstKey).size)

        gate.onDecoderGone()
        assertEquals(listOf(config, firstKey), gate.onDecoderReady())
        assertTrue(gate.accept(packet(2, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT)).isEmpty())
        val nextKey = packet(
            3,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )
        assertEquals(listOf(config, nextKey), gate.accept(nextKey))
    }

    @Test
    fun `keyframe received before Surface is replayed when decoder becomes ready`() {
        val gate = H264DecoderPacketGate()
        val config = packet(8, VideoProtocol.H264_FLAG_CONFIG)
        val keyFrame = packet(
            8,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )

        gate.accept(config)
        assertTrue(gate.accept(keyFrame).isEmpty())

        assertEquals(listOf(config, keyFrame), gate.onDecoderReady())
    }

    @Test
    fun `decoder input drop waits for a new keyframe without closing decoder`() {
        val gate = H264DecoderPacketGate()
        val config = packet(1, VideoProtocol.H264_FLAG_CONFIG)
        val firstKey = packet(
            1,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )
        gate.accept(config)
        gate.onDecoderReady()
        assertEquals(2, gate.accept(firstKey).size)

        gate.onAccessUnitDropped()

        assertTrue(gate.accept(packet(2, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT)).isEmpty())
        val recoveryKey = packet(
            3,
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )
        assertEquals(listOf(config, recoveryKey), gate.accept(recoveryKey))
    }

    @Test
    fun `codec unavailable advertises only zlib fallback`() {
        val hello = VideoHelloNegotiator.create(null)

        assertEquals(VideoProtocol.CODEC_MASK_ZLIB_BGRA8888, hello.supportedCodecMask)
        assertEquals(640, hello.maxWidth)
        assertEquals(400, hello.maxHeight)
        assertEquals(10, hello.maxFps)
    }

    @Test
    fun `codec available advertises H264 first pixels fallback and full profile`() {
        val capability = H264DecoderCapability("decoder", true, true)
        val hello = VideoHelloNegotiator.create(capability)

        assertTrue(hello.supportedCodecMask and VideoProtocol.CODEC_MASK_H264 != 0)
        assertTrue(hello.supportedCodecMask and VideoProtocol.CODEC_MASK_ZLIB_BGRA8888 != 0)
        assertEquals(1280, hello.maxWidth)
        assertEquals(800, hello.maxHeight)
        assertEquals(60, hello.maxFps)
    }

    @Test
    fun `dropped IDR also requires a later IDR before P frames resume`() {
        val gate = readyGate()

        gate.onAccessUnitDropped() // Models an IDR removed by queue overflow.

        assertTrue(gate.accept(packet(4, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT)).isEmpty())
        assertTrue(
            gate.accept(
                packet(
                    5,
                    VideoProtocol.H264_FLAG_KEYFRAME or
                        VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
                ),
            ).last().header.isKeyFrame,
        )
    }

    @Test
    fun `executor rejection invalidates dependencies until recovery IDR`() {
        val gate = readyGate()

        gate.onAccessUnitDropped() // Same transition used by executor rejection.

        assertTrue(gate.accept(packet(6, VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT)).isEmpty())
        val recovery = gate.accept(
            packet(
                7,
                VideoProtocol.H264_FLAG_KEYFRAME or
                    VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
            ),
        )
        assertTrue(recovery.last().header.isKeyFrame)
    }

    @Test
    fun `H264 retention budget never exceeds access unit or byte limits`() {
        val budget = H264RetentionBudget(maximumUnits = 2, maximumBytes = 10)

        budget.reserve(6)
        assertFalse(budget.canReserve(5))
        assertTrue(budget.canReserve(4))
        budget.reserve(4)
        assertFalse(budget.canReserve(1))
        assertEquals(2, budget.units())
        assertEquals(10, budget.bytes())
        assertEquals(10, budget.highWatermarkBytes())

        budget.release(6)
        assertEquals(1, budget.units())
        assertEquals(4, budget.bytes())
    }

    @Test
    fun `decoder maximum remains a capability separate from preferred FPS`() {
        listOf(60, 45, 30).forEach { clientMaxFps ->
            val capability = H264DecoderCapability(
                codecName = "decoder-$clientMaxFps",
                isHardwareAccelerated = true,
                supportsLowLatency = true,
                clientMaxFps = clientMaxFps,
            )

            val hello = VideoHelloNegotiator.create(capability)

            assertEquals(60, VideoClientCapabilities.preferredSessionFps)
            assertEquals(clientMaxFps, hello.maxFps)
        }
    }

    @Test
    fun `low latency option is gated by API 30 and decoder capability`() {
        val supported = H264DecoderCapability("decoder", true, true)
        val unsupported = supported.copy(supportsLowLatency = false)

        assertFalse(VideoHelloNegotiator.shouldEnableLowLatency(29, supported))
        assertTrue(VideoHelloNegotiator.shouldEnableLowLatency(30, supported))
        assertFalse(VideoHelloNegotiator.shouldEnableLowLatency(37, unsupported))
    }

    @Test
    fun `surface destruction keeps transport session alive`() {
        assertEquals(
            VideoSurfaceTransportAction.KEEP_SESSION,
            VideoSurfaceLifecyclePolicy.onSurfaceDestroyed(desktopSessionActive = true),
        )
    }

    @Test
    fun `recoverable and transient codec errors recover while fatal errors reconnect`() {
        assertEquals(
            H264DecoderErrorDisposition.RECOVER,
            classifyH264DecoderError(isRecoverable = true, isTransient = false),
        )
        assertEquals(
            H264DecoderErrorDisposition.RECOVER,
            classifyH264DecoderError(isRecoverable = false, isTransient = true),
        )
        assertEquals(
            H264DecoderErrorDisposition.FATAL,
            classifyH264DecoderError(isRecoverable = false, isTransient = false),
        )
    }

    private fun handshake(codec: Int, width: Int, height: Int, fps: Int): ByteArray =
        ByteBuffer.allocate(VideoProtocol.ACK_SIZE)
            .order(ByteOrder.LITTLE_ENDIAN)
            .put("PNLV".toByteArray())
            .putShort(VideoProtocol.VERSION.toShort())
            .putShort(VideoProtocol.TYPE_ACK.toShort())
            .putInt(codec)
            .putInt(width)
            .putInt(height)
            .putInt(fps)
            .array()

    private fun packetHeader(
        sequence: Long,
        timestamp: Long,
        flags: Int,
        payloadBytes: Int,
        encodeMicros: Long,
    ): ByteArray = ByteBuffer.allocate(VideoProtocol.H264_PACKET_HEADER_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put("PNLV".toByteArray())
        .putShort(VideoProtocol.VERSION.toShort())
        .putShort(VideoProtocol.TYPE_H264_PACKET.toShort())
        .putLong(sequence)
        .putLong(timestamp)
        .putInt(flags)
        .putInt(payloadBytes)
        .putLong(encodeMicros)
        .array()

    private fun packet(sequence: Long, flags: Int) = H264VideoPacket(
        header = H264PacketHeader(
            sequence = sequence,
            captureTimestampMicros = sequence * 100,
            flags = flags,
            payloadBytes = 5,
            encodeDurationMicros = 100,
        ),
        payload = byteArrayOf(0, 0, 0, 1, 0x65),
        receivedAtNanos = sequence * 1_000,
    )

    private fun readyGate(): H264DecoderPacketGate {
        val gate = H264DecoderPacketGate()
        gate.accept(packet(1, VideoProtocol.H264_FLAG_CONFIG))
        gate.onDecoderReady()
        gate.accept(
            packet(
                2,
                VideoProtocol.H264_FLAG_KEYFRAME or
                    VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
            ),
        )
        return gate
    }
}
