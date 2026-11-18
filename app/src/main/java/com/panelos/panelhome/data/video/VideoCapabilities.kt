package com.panelos.panelhome.data.video

/** Explicit client capability advertised by the v1 video handshake. */
data class VideoCapabilities(
    val maxWidth: Int,
    val maxHeight: Int,
    val maxFps: Int,
    val supportedCodecMask: Int,
)

object VideoClientCapabilities {
    const val preferredSessionFps = 60

    /** Ordered rates used when probing the decoder for the Desktop profile. */
    val desktopFpsCandidates = listOf(preferredSessionFps, 45, 30)

    val H264 = VideoCapabilities(
        maxWidth = 1_280,
        maxHeight = 800,
        maxFps = preferredSessionFps,
        supportedCodecMask =
            VideoProtocol.CODEC_MASK_H264 or
                VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
    )

    val ZlibFallback = VideoCapabilities(
        maxWidth = VideoProtocol.ZLIB_MAX_WIDTH,
        maxHeight = VideoProtocol.ZLIB_MAX_HEIGHT,
        maxFps = VideoProtocol.ZLIB_MAX_FPS,
        supportedCodecMask = VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
    )

    fun h264ForClientMaximum(clientMaxFps: Int): VideoCapabilities {
        require(clientMaxFps in 1..240) { "Invalid client maximum FPS" }
        return H264.copy(maxFps = clientMaxFps)
    }
}

internal fun VideoCapabilities.toHello() = VideoHello(
    maxWidth = maxWidth,
    maxHeight = maxHeight,
    maxFps = maxFps,
    supportedCodecMask = supportedCodecMask,
)
