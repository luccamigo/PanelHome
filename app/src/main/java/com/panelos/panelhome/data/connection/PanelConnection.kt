package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.protocol.DesktopStateMessage
import com.panelos.panelhome.domain.protocol.LoopMediaChunkMessage
import com.panelos.panelhome.domain.protocol.LoopMediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeResultMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import com.panelos.panelhome.domain.protocol.NetworkRatesMessage
import com.panelos.panelhome.domain.protocol.ProcessesMessage
import com.panelos.panelhome.domain.protocol.ProcessTerminateResultMessage
import com.panelos.panelhome.domain.protocol.PanelMessageType
import com.panelos.panelhome.domain.protocol.LocalNoticesMessage
import com.panelos.panelhome.domain.protocol.ClaudePermissionsMessage
import kotlinx.coroutines.flow.Flow

interface PanelConnection {
    val events: Flow<PanelConnectionEvent>

    suspend fun maintainConnection()

    suspend fun send(command: PanelCommand, expectedConnectionId: Long)

    fun close()
}

sealed interface PanelConnectionEvent {
    data object Connecting : PanelConnectionEvent

    data class Connected(
        val connectionId: Long,
        val hostName: String,
        val features: Set<String> = emptySet(),
        val hostBuildIdentity: String? = null,
        val hostBuildNumber: Long? = null,
    ) : PanelConnectionEvent

    data class TelemetryReceived(
        val telemetry: TelemetryMessage,
    ) : PanelConnectionEvent

    data class NetworkRatesReceived(val rates: NetworkRatesMessage) : PanelConnectionEvent
    data class ProcessesReceived(val processes: ProcessesMessage) : PanelConnectionEvent
    data class ProcessTerminateResultReceived(val message: ProcessTerminateResultMessage) : PanelConnectionEvent
    data class MonitoringDataInvalid(val type: PanelMessageType) : PanelConnectionEvent
    data class LocalNoticesReceived(val message: LocalNoticesMessage) : PanelConnectionEvent
    data class ClaudePermissionsReceived(val message: ClaudePermissionsMessage) : PanelConnectionEvent

    data class MediaStateReceived(
        val mediaState: MediaStateMessage,
    ) : PanelConnectionEvent

    data class MediaArtworkReceived(
        val mediaId: String,
        val artwork: MediaArtwork?,
    ) : PanelConnectionEvent

    data class VolumeStateReceived(
        val volumeState: VolumeStateMessage,
    ) : PanelConnectionEvent

    data class DesktopStateReceived(
        val desktopState: DesktopStateMessage,
    ) : PanelConnectionEvent

    data class StreamDeckLayoutReceived(
        val layout: StreamDeckLayoutMessage,
    ) : PanelConnectionEvent

    data class StreamDeckInvokeResultReceived(
        val result: StreamDeckInvokeResultMessage,
    ) : PanelConnectionEvent

    data class StreamDeckIconReceived(
        val icon: StreamDeckIconMessage,
    ) : PanelConnectionEvent

    data class LoopMediaStateReceived(
        val state: LoopMediaStateMessage,
    ) : PanelConnectionEvent

    data class LoopMediaChunkReceived(
        val chunk: LoopMediaChunkMessage,
    ) : PanelConnectionEvent

    data object Disconnected : PanelConnectionEvent
}
