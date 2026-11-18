namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Root of the persisted Stream Deck document. The domain is intentionally
/// free of any UI type: the Control Center edits it, PanelHost will later read
/// it to resolve an invoked button, and neither is allowed to leak into it.
/// </summary>
public sealed class StreamDeckConfiguration
{
    /// <summary>
    /// Schema version written to disk. Bump it whenever the persisted shape
    /// changes in a way an older build cannot read, and teach
    /// <see cref="Serialization.StreamDeckJson"/> how to migrate.
    /// </summary>
    /// <remarks>
    /// v2 replaced the icon's local file path with a cache identifier and added
    /// the per-button <c>enabled</c> flag; <see cref="Serialization.StreamDeckJson"/>
    /// migrates a v1 document on read.
    /// </remarks>
    public const int CurrentSchemaVersion = 2;

    public int Version { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Profile the tablet should show. Null means "the first profile".
    /// </summary>
    public string? ActiveProfileId { get; set; }

    public List<StreamDeckProfile> Profiles { get; set; } = [];

    public StreamDeckProfile? FindActiveProfile()
    {
        if (Profiles.Count == 0)
        {
            return null;
        }

        if (ActiveProfileId is null)
        {
            return Profiles[0];
        }

        return Profiles.FirstOrDefault(
                profile => string.Equals(
                    profile.Id,
                    ActiveProfileId,
                    StringComparison.Ordinal))
            ?? Profiles[0];
    }

    public StreamDeckButton? FindButton(string buttonId) =>
        Profiles
            .SelectMany(profile => profile.Pages)
            .SelectMany(page => page.Buttons)
            .FirstOrDefault(
                button => string.Equals(
                    button.Id,
                    buttonId,
                    StringComparison.Ordinal));
}

public sealed class StreamDeckProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<StreamDeckPage> Pages { get; set; } = [];
}

public sealed class StreamDeckPage
{
    public const int DefaultRows = 3;
    public const int DefaultColumns = 4;

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Rows { get; set; } = DefaultRows;

    public int Columns { get; set; } = DefaultColumns;

    public List<StreamDeckButton> Buttons { get; set; } = [];

    public StreamDeckButton? FindButtonAt(int row, int column) =>
        Buttons.FirstOrDefault(
            button => button.Row == row && button.Column == column);
}

public sealed class StreamDeckButton
{
    /// <summary>
    /// Stable identity of the button. This is the only thing the tablet ever
    /// sends back, so it must stay unique across the whole configuration.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public int Row { get; set; }

    public int Column { get; set; }

    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// A disabled button stays configured and visible but refuses to run, both
    /// from the Control Center and from a future tablet invocation.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public StreamDeckIcon Icon { get; set; } = new();

    /// <summary>
    /// Null while the button exists but has no action yet. An unconfigured or
    /// incomplete action is a normal editing state, never an error.
    /// </summary>
    public PanelAction? Action { get; set; }

    /// <summary>The action carries everything it needs to run.</summary>
    public bool IsExecutable => Action?.IsConfigured == true;

    /// <summary>Configured, complete and not switched off.</summary>
    public bool IsRunnable => Enabled && IsExecutable;
}

public enum StreamDeckIconKind
{
    None,

    /// <summary>Whatever glyph the action type implies.</summary>
    ActionDefault,

    /// <summary>An image held in the PC-side icon cache, addressed by id.</summary>
    Custom,
}

public sealed class StreamDeckIcon
{
    public StreamDeckIconKind Kind { get; set; } = StreamDeckIconKind.ActionDefault;

    /// <summary>
    /// Key into the icon cache (%LOCALAPPDATA%\PanelOS\icons). This - and never
    /// a filesystem path - is what a future layout message sends to the tablet.
    /// </summary>
    public string? IconId { get; set; }

    /// <summary>
    /// Where the image came from on this PC, kept so the editor can show the
    /// origin and re-import it. It is PC-side configuration and is deliberately
    /// excluded from the tablet DTO.
    /// </summary>
    public string? SourcePath { get; set; }

    public bool HasImage =>
        Kind == StreamDeckIconKind.Custom && !string.IsNullOrWhiteSpace(IconId);

    public static StreamDeckIcon None() => new() { Kind = StreamDeckIconKind.None };
}
