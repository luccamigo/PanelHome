package com.panelos.panelhome.domain.protocol

import com.panelos.panelhome.domain.model.DesktopStatus

sealed interface PanelMessage {
    val type: PanelMessageType
}

data class HelloMessage(
    val protocolVersion: Int = PanelProtocol.PROTOCOL_VERSION,
    val deviceName: String = PanelProtocol.DEVICE_NAME,
    val features: List<String> = PanelProtocol.CLIENT_FEATURES,
    val buildIdentity: String? = null,
    val buildNumber: Long? = null,
) : PanelMessage {
    override val type = PanelMessageType.HELLO
}

data class HelloAckMessage(
    val protocolVersion: Int,
    val hostName: String,
    val features: List<String> = emptyList(),
    val buildIdentity: String? = null,
    val buildNumber: Long? = null,
) : PanelMessage {
    override val type = PanelMessageType.HELLO_ACK
}

data class ErrorMessage(
    val code: String,
) : PanelMessage {
    override val type = PanelMessageType.ERROR
}

data class CpuTelemetry(
    val name: String? = null,
    val temperatureC: Double? = null,
    val usagePercent: Double? = null,
    val clockGhz: Double? = null,
    val powerW: Double? = null,
)

data class GpuTelemetry(
    val name: String? = null,
    val temperatureC: Double? = null,
    val usagePercent: Double? = null,
    val clockGhz: Double? = null,
    val memoryUsagePercent: Double? = null,
    val memoryUsedGb: Double? = null,
    val memoryTotalGb: Double? = null,
    val powerW: Double? = null,
)

data class MemoryTelemetry(
    val usedGb: Double? = null,
    val totalGb: Double? = null,
    val usagePercent: Double? = null,
)

data class StorageTelemetry(
    val name: String? = null,
    val temperatureC: Double? = null,
    val usagePercent: Double? = null,
)

data class FpsTelemetry(
    val state: String = "unavailable",
    val presentsPerSecond: Double? = null,
    val appName: String? = null,
)

data class TelemetryMessage(
    val cpu: CpuTelemetry = CpuTelemetry(),
    val gpu: GpuTelemetry = GpuTelemetry(),
    val memory: MemoryTelemetry = MemoryTelemetry(),
    val storage: List<StorageTelemetry> = emptyList(),
    val fps: FpsTelemetry? = null,
) : PanelMessage {
    override val type = PanelMessageType.TELEMETRY
}

data class MediaStateMessage(
    val available: Boolean = true,
    val mediaId: String? = null,
    val sourceApp: String? = null,
    val title: String = "",
    val artist: String = "",
    val album: String? = null,
    val isPlaying: Boolean = false,
    val positionMs: Long = 0,
    val durationMs: Long = 0,
    val canPlayPause: Boolean = true,
    val canPrevious: Boolean = true,
    val canNext: Boolean = true,
) : PanelMessage {
    override val type = PanelMessageType.MEDIA_STATE
}

data class MediaArtworkMessage(
    val mediaId: String,
    val available: Boolean,
    val mimeType: String?,
    val dataBase64: String?,
) : PanelMessage {
    override val type = PanelMessageType.MEDIA_ARTWORK
}

data class VolumeStateMessage(
    val available: Boolean = false,
    val volumePercent: Int = 0,
    val isMuted: Boolean = false,
) : PanelMessage {
    override val type = PanelMessageType.VOLUME_STATE
}

data class DesktopStateMessage(
    val status: DesktopStatus,
    val errorCode: String? = null,
    val generation: Long? = null,
) : PanelMessage {
    override val type = PanelMessageType.DESKTOP_STATE
}

data class CommandMessage(
    val command: PanelCommand,
) : PanelMessage {
    override val type = PanelMessageType.COMMAND
}

/**
 * The layout PanelHost publishes. It carries only what the panel draws plus the
 * ids it sends back; nothing here describes the Windows action behind a button.
 */
data class StreamDeckLayoutMessage(
    val revision: String,
    val profileId: String? = null,
    val profileName: String? = null,
    val pages: List<StreamDeckPageMessage> = emptyList(),
) : PanelMessage {
    override val type = PanelMessageType.STREAM_DECK_LAYOUT
}

data class StreamDeckPageMessage(
    val id: String,
    val name: String,
    val rows: Int,
    val columns: Int,
    val buttons: List<StreamDeckButtonMessage> = emptyList(),
)

data class StreamDeckButtonMessage(
    val id: String,
    val row: Int,
    val column: Int,
    val label: String,
    val iconId: String? = null,
    val glyph: String? = null,
    val enabled: Boolean = true,
)

data class StreamDeckInvokeResultMessage(
    val buttonId: String,
    val success: Boolean,
    val errorCode: String? = null,
    val message: String? = null,
) : PanelMessage {
    override val type = PanelMessageType.STREAM_DECK_INVOKE_RESULT
}

data class StreamDeckIconMessage(
    val iconId: String,
    val available: Boolean,
    val mime: String? = null,
    val dataBase64: String? = null,
) : PanelMessage {
    override val type = PanelMessageType.STREAM_DECK_ICON
}

/**
 * The single media the host publishes for the full-screen page. When
 * [available] is false every other field is null. There is deliberately no
 * name, path or URL: [mediaId] (a SHA-256) is the only identity.
 */
data class LoopMediaStateMessage(
    val available: Boolean,
    val mediaId: String? = null,
    val kind: LoopMediaKind? = null,
    val sizeBytes: Int? = null,
) : PanelMessage {
    override val type = PanelMessageType.LOOP_MEDIA_STATE
}

/** One answer to `loop_media.request`: either data or an [error] code. */
data class LoopMediaChunkMessage(
    val mediaId: String,
    val offset: Int,
    val totalBytes: Int? = null,
    val dataBase64: String? = null,
    val last: Boolean = false,
    val error: String? = null,
) : PanelMessage {
    override val type = PanelMessageType.LOOP_MEDIA_CHUNK
}

data object PingMessage : PanelMessage {
    override val type = PanelMessageType.PING
}

data object PongMessage : PanelMessage {
    override val type = PanelMessageType.PONG
}
