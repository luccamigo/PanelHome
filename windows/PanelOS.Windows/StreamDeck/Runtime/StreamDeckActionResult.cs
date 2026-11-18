namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Stable failure codes. They are part of the contract a future
/// <c>streamdeck.invoke</c> reply will carry, so they are spelled once here and
/// never derived from an exception type or a message.
/// </summary>
public static class StreamDeckErrorCodes
{
    public const string ButtonNotFound = "button_not_found";
    public const string ButtonDisabled = "button_disabled";
    public const string ActionNotConfigured = "action_not_configured";
    public const string ApplicationNotFound = "application_not_found";
    public const string InvalidUrl = "invalid_url";
    public const string UnsupportedAction = "unsupported_action";
    public const string ExecutionFailed = "execution_failed";

    /// <summary>
    /// A shell interpreter was configured with arguments. Launching a terminal
    /// is fine; handing it a command line is arbitrary execution by another
    /// name, and this build refuses it.
    /// </summary>
    public const string ShellNotAllowed = "shell_not_allowed";

    /// <summary>The configuration could not be read.</summary>
    public const string ConfigurationUnavailable = "configuration_unavailable";
}

/// <summary>
/// Outcome of running a button. Failures are values, not exceptions: an
/// unconfigured action and a missing executable are ordinary states of a
/// user-edited configuration.
/// </summary>
public sealed record StreamDeckActionResult
{
    private StreamDeckActionResult(bool success, string? errorCode, string message)
    {
        Success = success;
        ErrorCode = errorCode;
        Message = message;
    }

    public bool Success { get; }

    /// <summary>One of <see cref="StreamDeckErrorCodes"/>, or null on success.</summary>
    public string? ErrorCode { get; }

    /// <summary>Human readable detail for the Control Center.</summary>
    public string Message { get; }

    public static StreamDeckActionResult Ok(string message) =>
        new(true, null, message);

    public static StreamDeckActionResult Error(string errorCode, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new StreamDeckActionResult(false, errorCode, message);
    }
}
