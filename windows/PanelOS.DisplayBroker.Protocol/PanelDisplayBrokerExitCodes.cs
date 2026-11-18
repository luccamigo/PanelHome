namespace PanelOS.DisplayBroker.Protocol;

/// <summary>
/// Process exit codes of PanelDisplayBroker. The broker runs hidden and elevated and used to end with
/// exit code 0 for several different failures (it swallowed them), so the cause could not be told
/// apart. Every cause now has its own code, and the host prints the meaning when the broker leaves
/// while it is still starting.
/// </summary>
public static class PanelDisplayBrokerExitCodes
{
    /// <summary>Orderly end: the host closed the pipe (lease), the parent exited, or the broker was cancelled.</summary>
    public const int Ok = 0;
    public const int InvalidArguments = 2;
    public const int NotElevated = 3;
    public const int AuthenticationFailed = 4;
    public const int ParentUnavailable = 5;
    /// <summary>Watchdog: shutdown did not finish in time.</summary>
    public const int ShutdownTimeout = 6;
    /// <summary>Watchdog: a driver call did not return in time.</summary>
    public const int RequestTimeout = 7;
    /// <summary>Could not connect to the host pipe (timeout or I/O error while connecting).</summary>
    public const int PipeConnectFailed = 8;
    /// <summary>Connected, but the pipe owner is not the user (or token owner), or access was denied.</summary>
    public const int PipeOwnerRejected = 9;
    /// <summary>I/O error while exchanging the hello with the host.</summary>
    public const int HandshakeFailed = 10;
    /// <summary>Malformed JSON or data from the host.</summary>
    public const int ProtocolError = 11;
    /// <summary>The shared frame mailbox could not be opened.</summary>
    public const int FrameMailboxUnavailable = 12;
    /// <summary>Any other exception; the full text is in the broker log.</summary>
    public const int Unexpected = 13;

    public static string Describe(int exitCode) => exitCode switch
    {
        Ok => "orderly end (the broker closed the connection by itself; see the broker log)",
        InvalidArguments => "invalid arguments",
        NotElevated => "not elevated",
        AuthenticationFailed => "the host rejected the hello (authentication)",
        ParentUnavailable => "the host process was not found or its start time differs",
        ShutdownTimeout => "watchdog: shutdown timed out",
        RequestTimeout => "watchdog: a driver call did not return",
        PipeConnectFailed => "could not connect to the host pipe",
        PipeOwnerRejected => "the host pipe owner was rejected or access was denied",
        HandshakeFailed => "I/O error during the hello exchange",
        ProtocolError => "malformed data from the host",
        FrameMailboxUnavailable => "the frame mailbox could not be opened",
        Unexpected => "unexpected exception (see the broker log)",
        _ => "unknown exit code (a crash code means an unhandled .NET exception)",
    };

    /// <summary>Where the elevated broker writes its own log. Created by the broker the first time it runs elevated.</summary>
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PanelOS", "Broker", "panelbroker.log");
}
