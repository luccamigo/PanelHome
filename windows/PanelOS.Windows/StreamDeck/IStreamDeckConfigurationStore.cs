namespace PanelOS.Windows.StreamDeck;

public enum StreamDeckConfigurationSource
{
    /// <summary>No file exists yet; the built-in profile was returned.</summary>
    Defaults,

    Loaded,

    /// <summary>
    /// The file existed but could not be trusted (malformed JSON or a document
    /// that breaks the structural rules). Defaults were returned and the file
    /// was moved aside instead of being overwritten in place.
    /// </summary>
    RecoveredFromInvalidFile,

    /// <summary>
    /// The file was written by a newer schema. Defaults were returned and the
    /// store refuses to save, so a downgrade cannot destroy the real one.
    /// </summary>
    UnsupportedSchema,

    /// <summary>The file could not be read at all (I/O or permissions).</summary>
    Unreadable,
}

public sealed record StreamDeckConfigurationLoadResult(
    StreamDeckConfiguration Configuration,
    StreamDeckConfigurationSource Source,
    string? Detail = null,
    string? QuarantinedFilePath = null)
{
    /// <summary>
    /// Whether saving over the backing file is safe. It is not when this build
    /// could not read what is already there.
    /// </summary>
    public bool CanSave => Source is
        StreamDeckConfigurationSource.Defaults or
        StreamDeckConfigurationSource.Loaded or
        StreamDeckConfigurationSource.RecoveredFromInvalidFile;
}

/// <summary>
/// Persistence boundary for the Stream Deck configuration. The Control Center
/// writes through it today; PanelHost will read through the same abstraction
/// when it starts resolving invoked buttons.
/// </summary>
public interface IStreamDeckConfigurationStore
{
    /// <summary>Where the configuration lives, for display and diagnostics.</summary>
    string Location { get; }

    /// <summary>
    /// Never throws for a missing, corrupt or newer-than-supported file: those
    /// are reported through <see cref="StreamDeckConfigurationLoadResult"/>.
    /// </summary>
    Task<StreamDeckConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Validates and persists the configuration atomically.
    /// </summary>
    /// <exception cref="StreamDeckConfigurationException">
    /// The configuration breaks a structural rule; nothing was written.
    /// </exception>
    Task SaveAsync(
        StreamDeckConfiguration configuration,
        CancellationToken cancellationToken);
}

public sealed class StreamDeckConfigurationException : Exception
{
    public StreamDeckConfigurationException(
        string message,
        IReadOnlyList<string> errors,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}
