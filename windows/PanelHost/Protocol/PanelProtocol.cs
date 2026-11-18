namespace PanelHost.Protocol;

public static class PanelProtocol
{
    public const int PROTOCOL_VERSION = 1;
    public const int Port = 8765;
    public const int MaxControlMessageBytes =
        PanelOS.Windows.Protocol.JsonLineProtocolLimits.MaxControlMessageBytes;
}

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string HelloAck = "hello_ack";
    public const string Error = "error";
    public const string Telemetry = "telemetry";
    public const string MediaState = "media_state";
    public const string MediaArtwork = "media_artwork";
    public const string VolumeState = "volume_state";
    public const string DesktopState = "desktop_state";
    public const string Command = "command";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string StreamDeckLayout = "streamdeck.layout";
    public const string StreamDeckInvokeResult = "streamdeck.invoke_result";
    public const string StreamDeckIcon = "streamdeck.icon";
    public const string LoopMediaState = "loop_media.state";
    public const string LoopMediaChunk = "loop_media.chunk";
}

public static class ProtocolFeatures
{
    public const string ComputerStats = "computer_stats";
    public const string Processes = "processes";
    public const string MediaArtwork = "media_artwork";
    public const string Volume = "volume";
    public const string Desktop = "desktop";
    public const string Touch = "touch";

    /// <summary>
    /// Additive: a host or a client that does not announce it simply never
    /// exchanges layouts or invocations, and everything else keeps working.
    /// </summary>
    public const string StreamDeck = "stream_deck";
    public const string LoopMedia = "loop_media";
    public const string ForegroundPresents = "foreground_presents_v1";
}

public enum MediaCommand
{
    Previous,
    PlayPause,
    Next,
}

public enum DesktopCommand
{
    Start,
    Stop,
}

public enum VolumeCommand
{
    Set,
    ToggleMute,
}

public enum DesktopTouchCommand
{
    Down,
    Move,
    Up,
    Cancel,
}

public static class DesktopStates
{
    public const string Stopped = "stopped";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Error = "error";
}

public static class DesktopErrorCodes
{
    public const string StartFailed = "start_failed";
    public const string StopFailed = "stop_failed";
    public const string InternalError = "internal_error";
    public const string DriverNotFound = "driver_not_found";
    public const string DriverAccessDenied = "driver_access_denied";
    public const string DriverNotReady = "driver_not_ready";
    public const string DriverIoError = "driver_io_error";
    public const string BrokerDisconnected = "broker_disconnected";
    // The Windows elevation prompt was not approved. Older tablets show their generic message.
    public const string ElevationDeclined = "elevation_declined";
}

public static class CommandNames
{
    public const string MediaPrevious = "media.previous";
    public const string MediaPlayPause = "media.play_pause";
    public const string MediaNext = "media.next";
    public const string VolumeSet = "volume.set";
    public const string VolumeToggleMute = "volume.toggle_mute";
    public const string DesktopStart = "desktop.start";
    public const string DesktopStop = "desktop.stop";
    public const string TouchDown = "touch.down";
    public const string TouchMove = "touch.move";
    public const string TouchUp = "touch.up";
    public const string TouchCancel = "touch.cancel";
    public const string StreamDeckInvoke = "streamdeck.invoke";
    public const string StreamDeckIconsRequest = "streamdeck.icons.request";
    public const string LoopMediaRequest = "loop_media.request";

    public static bool TryParse(string value, out MediaCommand command)
    {
        switch (value)
        {
            case MediaPrevious:
                command = MediaCommand.Previous;
                return true;
            case MediaPlayPause:
                command = MediaCommand.PlayPause;
                return true;
            case MediaNext:
                command = MediaCommand.Next;
                return true;
            default:
                command = default;
                return false;
        }
    }

    public static bool TryParseDesktop(string value, out DesktopCommand command)
    {
        switch (value)
        {
            case DesktopStart:
                command = DesktopCommand.Start;
                return true;
            case DesktopStop:
                command = DesktopCommand.Stop;
                return true;
            default:
                command = default;
                return false;
        }
    }

    public static bool TryParseVolume(string value, out VolumeCommand command)
    {
        switch (value)
        {
            case VolumeSet:
                command = VolumeCommand.Set;
                return true;
            case VolumeToggleMute:
                command = VolumeCommand.ToggleMute;
                return true;
            default:
                command = default;
                return false;
        }
    }

    public static bool TryParseTouch(string value, out DesktopTouchCommand command)
    {
        switch (value)
        {
            case TouchDown:
                command = DesktopTouchCommand.Down;
                return true;
            case TouchMove:
                command = DesktopTouchCommand.Move;
                return true;
            case TouchUp:
                command = DesktopTouchCommand.Up;
                return true;
            case TouchCancel:
                command = DesktopTouchCommand.Cancel;
                return true;
            default:
                command = default;
                return false;
        }
    }
}

public static class ErrorCodes
{
    public const string UnsupportedProtocol = "unsupported_protocol";
    public const string InvalidHandshake = "invalid_handshake";
    public const string InvalidMessage = "invalid_message";
    public const string UnknownCommand = "unknown_command";
}

/// <summary>
/// Failure codes a <c>streamdeck.invoke_result</c> may carry, on top of the
/// ones the shared executor already defines.
/// </summary>
public static class StreamDeckProtocolErrorCodes
{
    /// <summary>
    /// The tablet invoked against a layout that is no longer current. Nothing
    /// runs and the up-to-date layout is sent immediately, because the same
    /// button id may now stand for a different action.
    /// </summary>
    public const string StaleLayout = "stale_layout";

    /// <summary>The invocation did not name a button.</summary>
    public const string InvalidRequest = "invalid_request";
}

/// <summary>Limits applied to what a tablet may ask for in one message.</summary>
public static class StreamDeckProtocolLimits
{
    public const int MaximumIconRequestCount = 64;

    public const int MaximumButtonIdLength = 128;

    public const int MaximumRevisionLength = 64;

    public const int MaximumPages = 16;

    public const int MaximumRows = 6;

    public const int MaximumColumns = 6;

    public const int MaximumButtons = 1024;

    public const int MaximumNameLength = 80;

    public const int MaximumLabelLength = 80;

    public const int MaximumIconCount = 256;
}

public static class PanelProtocolLimits
{
    public const int MaximumPeerNameLength = 80;
    public const int MaximumTelemetryStorageItems = 64;
}
