package com.panelos.panelhome.data.video

/**
 * Maintains the dependency boundary required by H.264. After decoder creation/recreation, P-frames
 * are discarded until a keyframe arrives, and the latest CONFIG is queued immediately before it.
 */
internal class H264DecoderPacketGate {
    private var decoderReady = false
    private var awaitingKeyFrame = true
    private var latestConfiguration: H264VideoPacket? = null
    private var latestKeyFrame: H264VideoPacket? = null

    fun reset() {
        decoderReady = false
        awaitingKeyFrame = true
        latestConfiguration = null
        latestKeyFrame = null
    }

    fun onDecoderReady(): List<H264VideoPacket> {
        decoderReady = true
        awaitingKeyFrame = true
        val keyFrame = latestKeyFrame ?: return emptyList()
        return buildList(2) {
            latestConfiguration?.let(::add)
            add(keyFrame)
        }
    }

    fun onDecoderGone() {
        decoderReady = false
        awaitingKeyFrame = true
    }

    /** A dropped access unit invalidates inter-frame dependencies but not the decoder instance. */
    fun onAccessUnitDropped() {
        awaitingKeyFrame = true
    }

    fun hasConfiguration(): Boolean = latestConfiguration != null

    fun accept(packet: H264VideoPacket): List<H264VideoPacket> {
        if (packet.header.isConfig) {
            latestConfiguration = packet
            return emptyList()
        }
        if (packet.header.isKeyFrame) {
            latestKeyFrame = packet
        }
        if (!decoderReady) return emptyList()
        if (!awaitingKeyFrame) return listOf(packet)
        if (!packet.header.isKeyFrame) return emptyList()

        awaitingKeyFrame = false
        return buildList(2) {
            latestConfiguration?.let(::add)
            add(packet)
        }
    }
}

internal object VideoHelloNegotiator {
    fun create(capability: H264DecoderCapability?): VideoHello =
        if (capability == null) {
            VideoClientCapabilities.ZlibFallback.toHello()
        } else {
            VideoClientCapabilities
                .h264ForClientMaximum(capability.clientMaxFps)
                .toHello()
        }

    fun shouldEnableLowLatency(sdkInt: Int, capability: H264DecoderCapability): Boolean =
        sdkInt >= 30 && capability.supportsLowLatency
}
