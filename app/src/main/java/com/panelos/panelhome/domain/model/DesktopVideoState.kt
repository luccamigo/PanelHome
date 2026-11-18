package com.panelos.panelhome.domain.model

enum class DesktopVideoStatus {
    IDLE,
    CONNECTING,
    WAITING_FOR_FIRST_FRAME,
    WAITING_FOR_CODEC_CONFIG,
    WAITING_FOR_KEYFRAME,
    RECOVERING,
    FRAME_RECEIVED,
    DISPLAYING,
    PRESENTATION_ERROR,
}

enum class DesktopVideoCodec {
    ZLIB_BGRA8888,
    H264,
}

data class DesktopVideoState(
    val status: DesktopVideoStatus = DesktopVideoStatus.IDLE,
    val sequence: Long? = null,
    val presentationGeneration: Long? = null,
    val presentedSequence: Long? = null,
    val width: Int? = null,
    val height: Int? = null,
    val codec: DesktopVideoCodec? = null,
)
