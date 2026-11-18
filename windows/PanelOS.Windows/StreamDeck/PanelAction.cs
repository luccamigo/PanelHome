using System.Text.Json;

namespace PanelOS.Windows.StreamDeck;

public enum PanelActionKind
{
    LaunchApplication,
    OpenUrl,
    KeyboardShortcut,
    MediaCommand,
    VolumeCommand,
    PanelModeCommand,

    /// <summary>
    /// An action written by a newer build. It is preserved on round-trip so a
    /// downgrade does not silently destroy configuration, and it is never
    /// executed nor exposed to the tablet.
    /// </summary>
    Unknown,
}

/// <summary>
/// A typed action a Stream Deck button can carry.
/// </summary>
/// <remarks>
/// New kinds (RunScript, RunCommand, PowerShell, ...) are added by deriving a
/// new sealed type here plus a branch in the serializer. That extension point
/// is deliberate, but this task implements no arbitrary execution at all: see
/// <see cref="IStreamDeckActionExecutor"/> for the security model.
/// </remarks>
public abstract class PanelAction
{
    public abstract PanelActionKind Kind { get; }

    /// <summary>
    /// Whether the action carries everything it needs to run later. A button
    /// with an incomplete action is still valid configuration.
    /// </summary>
    public abstract bool IsConfigured { get; }

    /// <summary>
    /// Stable glyph name for the default icon of this action. It is visual
    /// metadata only - it never encodes a path, argument or command.
    /// </summary>
    public abstract string DefaultGlyph { get; }
}

public sealed class LaunchApplicationAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.LaunchApplication;

    public string? ExecutablePath { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public override bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ExecutablePath);

    public override string DefaultGlyph => "app";
}

public sealed class OpenUrlAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.OpenUrl;

    public string? Url { get; set; }

    public override bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

    public override string DefaultGlyph => "link";
}

[Flags]
public enum PanelKeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8,
}

/// <summary>
/// Keys a shortcut may target. Structured on purpose: a free-form string would
/// have to be parsed by whatever eventually presses the key.
/// </summary>
public enum PanelKey
{
    None,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Escape,
    Tab,
    Space,
    Enter,
    Backspace,
    Delete,
    Home,
    End,
    PageUp,
    PageDown,
    Left,
    Right,
    Up,
    Down,
    PrintScreen,
}

public sealed class KeyboardShortcutAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.KeyboardShortcut;

    public PanelKeyModifiers Modifiers { get; set; } = PanelKeyModifiers.None;

    public PanelKey Key { get; set; } = PanelKey.None;

    public override bool IsConfigured => Key != PanelKey.None;

    public override string DefaultGlyph => "keyboard";

    /// <summary>Display form such as <c>Win + Shift + S</c>.</summary>
    public string Describe()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(PanelKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(PanelKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(PanelKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(PanelKeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        if (Key != PanelKey.None)
        {
            parts.Add(DescribeKey(Key));
        }

        return parts.Count == 0 ? string.Empty : string.Join(" + ", parts);
    }

    private static string DescribeKey(PanelKey key)
    {
        var name = key.ToString();
        return name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])
            ? name[1..]
            : name;
    }
}

public enum MediaCommand
{
    PlayPause,
    Previous,
    Next,
}

public sealed class MediaCommandAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.MediaCommand;

    public MediaCommand Command { get; set; } = MediaCommand.PlayPause;

    public override bool IsConfigured => true;

    public override string DefaultGlyph => Command switch
    {
        MediaCommand.Previous => "media-previous",
        MediaCommand.Next => "media-next",
        _ => "media-play-pause",
    };
}

public enum VolumeCommand
{
    MuteToggle,
    VolumeUp,
    VolumeDown,

    /// <summary>Reserved for a future absolute level; see <c>Level</c>.</summary>
    SetLevel,
}

public sealed class VolumeCommandAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.VolumeCommand;

    public VolumeCommand Command { get; set; } = VolumeCommand.MuteToggle;

    /// <summary>
    /// Absolute level in percent for <see cref="VolumeCommand.SetLevel"/>. The
    /// model carries it already so adding the behaviour later needs no schema
    /// change; nothing in this task sets a level.
    /// </summary>
    public int? Level { get; set; }

    public override bool IsConfigured =>
        Command != VolumeCommand.SetLevel || Level is >= 0 and <= 100;

    public override string DefaultGlyph => Command switch
    {
        VolumeCommand.VolumeUp => "volume-up",
        VolumeCommand.VolumeDown => "volume-down",
        _ => "volume-mute",
    };
}

public enum PanelMode
{
    Dashboard,
    Desktop,
    Ambient,
    StreamDeck,
}

public sealed class PanelModeAction : PanelAction
{
    public override PanelActionKind Kind => PanelActionKind.PanelModeCommand;

    public PanelMode Mode { get; set; } = PanelMode.Dashboard;

    public override bool IsConfigured => true;

    public override string DefaultGlyph => "panel-mode";
}

/// <summary>
/// Placeholder for an action kind this build does not know. It keeps the raw
/// JSON so saving does not discard a newer build's configuration.
/// </summary>
public sealed class UnknownPanelAction : PanelAction
{
    public UnknownPanelAction(string rawKind, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawKind);
        RawKind = rawKind;
        Payload = payload;
    }

    public override PanelActionKind Kind => PanelActionKind.Unknown;

    public string RawKind { get; }

    /// <summary>Verbatim JSON object, already cloned from the read buffer.</summary>
    public JsonElement Payload { get; }

    public override bool IsConfigured => false;

    public override string DefaultGlyph => "unknown";
}
