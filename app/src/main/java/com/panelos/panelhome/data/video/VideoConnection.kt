package com.panelos.panelhome.data.video

interface VideoConnection {
    /** Runs one video socket session and returns when that session ends. */
    suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    )

    /** Codec-aware session API. Legacy fakes remain valid through the ZLIB default. */
    suspend fun receiveVideo(
        onConnected: (VideoSessionInfo) -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
        onH264Packet: (H264VideoPacket) -> Unit,
    ) {
        receiveFrames(
            onConnected = {
                onConnected(
                    VideoSessionInfo(
                        codec = VideoProtocol.CODEC_ZLIB_BGRA8888,
                        width = VideoProtocol.ZLIB_MAX_WIDTH,
                        height = VideoProtocol.ZLIB_MAX_HEIGHT,
                        fps = VideoProtocol.ZLIB_MAX_FPS,
                    ),
                )
            },
            onFrame = onFrame,
        )
    }

    /** Interrupts the current session without permanently disabling future sessions. */
    fun disconnect()

    fun disconnect(reason: VideoClientCloseReason) = disconnect()

    fun updateRuntimeState(state: VideoClientRuntimeState) = Unit

    /** Permanently releases this connection. */
    fun close()
}

data class VideoSessionInfo(
    val codec: Int,
    val width: Int,
    val height: Int,
    val fps: Int,
)

/**
 * Pixels are valid only for the duration of the onFrame callback. Consumers must copy or consume
 * them synchronously; this lets the transport reuse its large decompression buffer.
 */
data class DecodedVideoFrame(
    val sequence: Long,
    val captureTimestampMicros: Long,
    val width: Int,
    val height: Int,
    val pixels: ByteArray,
    val pixelCountBytes: Int,
)
