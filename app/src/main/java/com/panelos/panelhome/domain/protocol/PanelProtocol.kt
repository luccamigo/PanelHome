package com.panelos.panelhome.domain.protocol

import com.panelos.panelhome.domain.model.DesktopStatus

object PanelProtocol {
    const val PROTOCOL_VERSION = 1
    // Fits a 512 KiB PNG after Base64 plus its JSON envelope.
    const val MAX_CONTROL_MESSAGE_BYTES = 1024 * 1024
    const val DEVICE_NAME = "PanelOS"
    const val FEATURE_MEDIA_ARTWORK = "media_artwork"
    const val FEATURE_DESKTOP = "desktop"
    const val FEATURE_VOLUME = "volume"
    const val FEATURE_TOUCH = "touch"

    /**
     * Additive. A PanelHost that does not announce it simply never sends a
     * layout, and the panel keeps working as a dashboard.
     */
    const val FEATURE_STREAM_DECK = "stream_deck"
    const val FEATURE_COMPUTER_STATS = "computer_stats"
    const val FEATURE_PROCESSES = "processes"
    const val FEATURE_LOCAL_NOTICES = "local_notices"
    const val FEATURE_CLAUDE_PERMISSIONS = "claude_permissions"

    /** Additive: without it on both sides no `loop_media.*` message is exchanged. */
    const val FEATURE_LOOP_MEDIA = "loop_media"
    const val FEATURE_FOREGROUND_PRESENTS = "foreground_presents_v1"

    val CLIENT_FEATURES = listOf(
        FEATURE_MEDIA_ARTWORK,
        FEATURE_DESKTOP,
        FEATURE_VOLUME,
        FEATURE_TOUCH,
        FEATURE_STREAM_DECK,
        FEATURE_COMPUTER_STATS,
        FEATURE_PROCESSES,
        FEATURE_LOCAL_NOTICES,
        FEATURE_CLAUDE_PERMISSIONS,
        FEATURE_LOOP_MEDIA,
        FEATURE_FOREGROUND_PRESENTS,
    )
}

object LoopMediaLimits {
    const val MAX_MEDIA_BYTES = 64 * 1024 * 1024
    const val MAX_CHUNK_BYTES = 256 * 1024
    const val MAX_ERROR_LENGTH = 64
    private val MEDIA_ID = Regex("^[0-9a-f]{64}$")

    /** A media id is a SHA-256 and a cache key; anything else never becomes a path. */
    fun isValidMediaId(mediaId: String?): Boolean = mediaId != null && MEDIA_ID.matches(mediaId)
}

/** The two formats the panel plays, each with the only mime it accepts. */
enum class LoopMediaKind(val wireValue: String, val mime: String, val extension: String) {
    GIF("gif", "image/gif", "gif"),
    VIDEO("video", "video/mp4", "mp4"),
    ;

    companion object {
        fun fromWireValue(value: String): LoopMediaKind? = entries.firstOrNull { it.wireValue == value }
    }
}

/** Error codes a `loop_media.chunk` may carry instead of data. */
object LoopMediaErrorCode {
    const val STALE_MEDIA = "stale_media"
    const val INVALID_OFFSET = "invalid_offset"
    const val UNAVAILABLE = "unavailable"
}

object PanelProtocolLimits {
    const val MAX_PROFILES = 16
    const val MAX_PAGES_PER_PROFILE = 16
    const val MAX_ROWS = 6
    const val MAX_COLUMNS = 6
    const val MAX_BUTTONS = 1024
    const val MAX_IDENTIFIER_LENGTH = 128
    const val MAX_NAME_LENGTH = 80
    const val MAX_LABEL_LENGTH = 80
    const val MAX_ICON_COUNT = 256
    const val MAX_ICON_REQUEST_COUNT = 64
    const val MAX_REVISION_LENGTH = 64
    const val MAX_TELEMETRY_STORAGE_ITEMS = 64
}

enum class PanelMessageType(val wireValue: String) {
    HELLO("hello"),
    HELLO_ACK("hello_ack"),
    ERROR("error"),
    TELEMETRY("telemetry"),
    MEDIA_STATE("media_state"),
    MEDIA_ARTWORK("media_artwork"),
    VOLUME_STATE("volume_state"),
    DESKTOP_STATE("desktop_state"),
    COMMAND("command"),
    PING("ping"),
    PONG("pong"),
    STREAM_DECK_LAYOUT("streamdeck.layout"),
    STREAM_DECK_INVOKE_RESULT("streamdeck.invoke_result"),
    STREAM_DECK_ICON("streamdeck.icon"),
    NETWORK_RATES("network_rates"),
    PROCESSES("processes"),
    PROCESS_TERMINATE_RESULT("processes.terminate_result"),
    LOCAL_NOTICES("local_notices"),
    CLAUDE_PERMISSIONS("claude_permissions"),
    LOOP_MEDIA_STATE("loop_media.state"),
    LOOP_MEDIA_CHUNK("loop_media.chunk"),
    ;

    companion object {
        fun fromWireValue(value: String): PanelMessageType =
            entries.firstOrNull { it.wireValue == value }
                ?: throw PanelProtocolException("Unknown message type: $value")
    }
}

sealed interface PanelCommand {
    val wireValue: String

    companion object {
        fun fromWireValue(value: String, commandValue: Int? = null): PanelCommand =
            when (value) {
                SetVolumeCommand.WIRE_VALUE -> SetVolumeCommand(
                    value = commandValue
                        ?: throw PanelProtocolException(
                            "Missing value for ${SetVolumeCommand.WIRE_VALUE}",
                        ),
                )

                ToggleVolumeMuteCommand.WIRE_VALUE -> ToggleVolumeMuteCommand
                StreamDeckInvokeCommand.WIRE_VALUE,
                StreamDeckIconsRequestCommand.WIRE_VALUE,
                ProcessesOptionsCommand.WIRE_VALUE,
                ProcessTerminateCommand.WIRE_VALUE,
                ClaudeShownCommand.WIRE_VALUE,
                ClaudeRespondCommand.WIRE_VALUE,
                LoopMediaRequestCommand.WIRE_VALUE,
                -> throw PanelProtocolException(
                    "Stream Deck commands require their own fields: $value",
                )
                TouchCommandAction.DOWN.wireValue,
                TouchCommandAction.MOVE.wireValue,
                TouchCommandAction.UP.wireValue,
                TouchCommandAction.CANCEL.wireValue,
                -> throw PanelProtocolException(
                    "Touch commands require pointer fields: $value",
                )
                else -> MediaCommand.entries.firstOrNull { it.wireValue == value }
                    ?: DesktopCommand.entries.firstOrNull { it.wireValue == value }
                    ?: throw PanelProtocolException("Unknown panel command: $value")
            }
    }
}

enum class MediaCommand(override val wireValue: String) : PanelCommand {
    PREVIOUS("media.previous"),
    PLAY_PAUSE("media.play_pause"),
    NEXT("media.next"),
    ;

    companion object {
        fun fromWireValue(value: String): MediaCommand =
            entries.firstOrNull { it.wireValue == value }
                ?: throw PanelProtocolException("Unknown media command: $value")
    }
}

enum class DesktopCommand(override val wireValue: String) : PanelCommand {
    START("desktop.start"),
    STOP("desktop.stop"),
}

enum class TouchCommandAction(val wireValue: String) {
    DOWN("touch.down"),
    MOVE("touch.move"),
    UP("touch.up"),
    CANCEL("touch.cancel"),
    ;

    companion object {
        fun fromWireValue(value: String): TouchCommandAction? =
            entries.firstOrNull { it.wireValue == value }
    }
}

data class TouchCommand(
    val action: TouchCommandAction,
    val pointerId: Int,
    val x: Double? = null,
    val y: Double? = null,
    val generation: Long,
) : PanelCommand {
    override val wireValue: String = action.wireValue
}

data class SetVolumeCommand(
    val value: Int,
) : PanelCommand {
    override val wireValue: String = WIRE_VALUE

    companion object {
        const val WIRE_VALUE = "volume.set"
    }
}

data object ToggleVolumeMuteCommand : PanelCommand {
    override val wireValue: String = WIRE_VALUE

    const val WIRE_VALUE = "volume.toggle_mute"
}

/**
 * The whole remote surface of the Stream Deck: an id, plus the revision of the
 * layout it was drawn from. PanelHost refuses the invocation when the revision
 * no longer matches, because the same id may since have been re-pointed at a
 * different action on the PC.
 */
data class StreamDeckInvokeCommand(
    val buttonId: String,
    val revision: String,
) : PanelCommand {
    override val wireValue: String = WIRE_VALUE

    companion object {
        const val WIRE_VALUE = "streamdeck.invoke"
    }
}

/** Asks only for the icons this tablet does not already have cached. */
data class StreamDeckIconsRequestCommand(
    val iconIds: List<String>,
) : PanelCommand {
    override val wireValue: String = WIRE_VALUE

    companion object {
        const val WIRE_VALUE = "streamdeck.icons.request"
    }
}

/** Pull request for the next chunk of the published loop media. */
data class LoopMediaRequestCommand(
    val mediaId: String,
    val offset: Int,
) : PanelCommand {
    override val wireValue: String = WIRE_VALUE

    companion object {
        const val WIRE_VALUE = "loop_media.request"
    }
}

/** Failure codes a `streamdeck.invoke_result` may carry. */
object StreamDeckErrorCode {
    const val STALE_LAYOUT = "stale_layout"
    const val BUTTON_NOT_FOUND = "button_not_found"
    const val BUTTON_DISABLED = "button_disabled"
    const val ACTION_NOT_CONFIGURED = "action_not_configured"
    const val APPLICATION_NOT_FOUND = "application_not_found"
}

internal fun DesktopStatus.toWireValue(): String = when (this) {
    DesktopStatus.STOPPED -> "stopped"
    DesktopStatus.STARTING -> "starting"
    DesktopStatus.RUNNING -> "running"
    DesktopStatus.STOPPING -> "stopping"
    DesktopStatus.ERROR -> "error"
}

internal fun desktopStatusFromWireValue(value: String): DesktopStatus = when (value) {
    "stopped" -> DesktopStatus.STOPPED
    "starting" -> DesktopStatus.STARTING
    "running" -> DesktopStatus.RUNNING
    "stopping" -> DesktopStatus.STOPPING
    "error" -> DesktopStatus.ERROR
    else -> throw PanelProtocolException("Unknown desktop state: $value")
}

object DesktopErrorCode {
    const val UNSUPPORTED = "unsupported"
    const val CONNECTION_LOST = "connection_lost"
    const val START_FAILED = "start_failed"
    const val STOP_FAILED = "stop_failed"
    const val INTERNAL_ERROR = "internal_error"
    const val DRIVER_NOT_FOUND = "driver_not_found"
    const val DRIVER_ACCESS_DENIED = "driver_access_denied"
    const val DRIVER_NOT_READY = "driver_not_ready"
    const val DRIVER_IO_ERROR = "driver_io_error"
}

object PanelProtocolErrorCode {
    const val UNSUPPORTED_PROTOCOL = "unsupported_protocol"
}

open class PanelProtocolException(
    message: String,
    cause: Throwable? = null,
) : Exception(message, cause)

/**
 * A well-formed line whose content fails validation. The receiver drops the
 * message and keeps the session, unlike a [PanelProtocolException].
 */
class PanelMessageRejectedException(
    message: String,
    cause: Throwable? = null,
) : PanelProtocolException(message, cause)
