namespace PanelOS.Windows.Host;

/// <summary>
/// Lifecycle of the PanelHost agent as the Control Center sees it. A boolean
/// would not be enough: a start that is still coming up and a start that failed
/// are different things to show and to act on.
/// </summary>
public enum PanelHostProcessState
{
    Stopped,
    Starting,
    Running,
    Stopping,

    /// <summary>The last operation failed, or the process died on its own.</summary>
    Error,
}

public sealed record PanelHostStatus
{
    public required PanelHostProcessState State { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public string? ExecutablePath { get; init; }

    /// <summary>
    /// True only for a process this Control Center started. An agent the user
    /// launched from a terminal is reported, never adopted: stopping it is not
    /// offered and closing the Control Center leaves it alone.
    /// </summary>
    public bool OwnedByControlCenter { get; init; }

    /// <summary>Exit code of the previous run, when one has ended.</summary>
    public int? LastExitCode { get; init; }

    public TimeSpan? LastRunDuration { get; init; }

    /// <summary>Human readable detail for a failure or an unexpected exit.</summary>
    public string? Detail { get; init; }

    public static PanelHostStatus NotRunning { get; } =
        new() { State = PanelHostProcessState.Stopped };

    public bool IsRunning => State == PanelHostProcessState.Running;

    public bool IsBusy => State is
        PanelHostProcessState.Starting or PanelHostProcessState.Stopping;

    /// <summary>Running, but started outside the Control Center.</summary>
    public bool IsExternal => IsRunning && !OwnedByControlCenter;
}

public sealed class PanelHostStatusChangedEventArgs : EventArgs
{
    public PanelHostStatusChangedEventArgs(PanelHostStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
    }

    public PanelHostStatus Status { get; }
}

public static class PanelHostErrorCodes
{
    public const string ExecutableNotFound = "executable_not_found";
    public const string AlreadyRunning = "already_running";
    public const string NotRunning = "not_running";

    /// <summary>The running agent was not started by the Control Center.</summary>
    public const string NotOwned = "not_owned";
    public const string StartFailed = "start_failed";
    public const string StopFailed = "stop_failed";
}

public sealed record PanelHostOperationResult(
    bool Success,
    string? ErrorCode,
    string Message)
{
    public static PanelHostOperationResult Ok(string message) =>
        new(true, null, message);

    public static PanelHostOperationResult Error(string errorCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new PanelHostOperationResult(false, errorCode, message);
    }
}
