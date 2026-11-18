namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Turns a structured shortcut into the exact key sequence Windows expects.
/// Pure and side-effect free, so the ordering rule is testable on its own.
/// </summary>
public static class KeyboardChordBuilder
{
    /// <summary>
    /// Order modifiers are pressed in. Releasing walks this list backwards, so
    /// Ctrl+Shift+S produces ctrl↓ shift↓ s↓ s↑ shift↑ ctrl↑.
    /// </summary>
    private static readonly (PanelKeyModifiers Modifier, PanelVirtualKey Key)[]
        ModifierOrder =
        [
            (PanelKeyModifiers.Control, PanelVirtualKey.Control),
            (PanelKeyModifiers.Shift, PanelVirtualKey.Shift),
            (PanelKeyModifiers.Alt, PanelVirtualKey.Alt),
            (PanelKeyModifiers.Windows, PanelVirtualKey.LeftWindows),
        ];

    public static IReadOnlyList<KeyboardInputEvent> Build(
        PanelKeyModifiers modifiers,
        PanelVirtualKey key)
    {
        if (key == PanelVirtualKey.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                "A shortcut needs a key to press.");
        }

        var pressed = ModifierOrder
            .Where(entry => modifiers.HasFlag(entry.Modifier))
            .Select(entry => entry.Key)
            .ToArray();

        var events = new List<KeyboardInputEvent>((pressed.Length * 2) + 2);
        foreach (var modifier in pressed)
        {
            events.Add(new KeyboardInputEvent(modifier, KeyTransition.Down));
        }

        events.Add(new KeyboardInputEvent(key, KeyTransition.Down));
        events.Add(new KeyboardInputEvent(key, KeyTransition.Up));

        for (var index = pressed.Length - 1; index >= 0; index--)
        {
            events.Add(new KeyboardInputEvent(pressed[index], KeyTransition.Up));
        }

        return events;
    }

    /// <summary>A single key press and release, for media and volume keys.</summary>
    public static IReadOnlyList<KeyboardInputEvent> BuildTap(PanelVirtualKey key) =>
        Build(PanelKeyModifiers.None, key);

    /// <summary>
    /// Maps the domain key onto a virtual key. Keys the runtime cannot press
    /// yet return false instead of guessing a code.
    /// </summary>
    public static bool TryMap(PanelKey key, out PanelVirtualKey virtualKey)
    {
        virtualKey = key switch
        {
            // The three runs are contiguous in both enums, so an offset from
            // the first member is enough and no per-key table is needed.
            >= PanelKey.A and <= PanelKey.Z => Offset(PanelVirtualKey.A, key, PanelKey.A),
            >= PanelKey.D0 and <= PanelKey.D9 =>
                Offset(PanelVirtualKey.D0, key, PanelKey.D0),
            >= PanelKey.F1 and <= PanelKey.F12 =>
                Offset(PanelVirtualKey.F1, key, PanelKey.F1),
            PanelKey.Escape => PanelVirtualKey.Escape,
            PanelKey.Tab => PanelVirtualKey.Tab,
            PanelKey.Space => PanelVirtualKey.Space,
            PanelKey.Enter => PanelVirtualKey.Enter,
            PanelKey.Backspace => PanelVirtualKey.Backspace,
            PanelKey.Delete => PanelVirtualKey.Delete,
            PanelKey.Home => PanelVirtualKey.Home,
            PanelKey.End => PanelVirtualKey.End,
            PanelKey.PageUp => PanelVirtualKey.PageUp,
            PanelKey.PageDown => PanelVirtualKey.PageDown,
            PanelKey.Left => PanelVirtualKey.ArrowLeft,
            PanelKey.Right => PanelVirtualKey.ArrowRight,
            PanelKey.Up => PanelVirtualKey.ArrowUp,
            PanelKey.Down => PanelVirtualKey.ArrowDown,
            PanelKey.PrintScreen => PanelVirtualKey.PrintScreen,
            _ => PanelVirtualKey.None,
        };

        return virtualKey != PanelVirtualKey.None;
    }

    private static PanelVirtualKey Offset(
        PanelVirtualKey first,
        PanelKey key,
        PanelKey firstKey) =>
        (PanelVirtualKey)((ushort)first + (key - firstKey));

    public static PanelVirtualKey ForMediaCommand(MediaCommand command) =>
        command switch
        {
            MediaCommand.Previous => PanelVirtualKey.MediaPreviousTrack,
            MediaCommand.Next => PanelVirtualKey.MediaNextTrack,
            _ => PanelVirtualKey.MediaPlayPause,
        };

    /// <summary>
    /// Volume keys for the commands that map onto one. <see
    /// cref="VolumeCommand.SetLevel"/> has no keyboard equivalent and returns
    /// <see cref="PanelVirtualKey.None"/>.
    /// </summary>
    public static PanelVirtualKey ForVolumeCommand(VolumeCommand command) =>
        command switch
        {
            VolumeCommand.MuteToggle => PanelVirtualKey.VolumeMute,
            VolumeCommand.VolumeUp => PanelVirtualKey.VolumeUp,
            VolumeCommand.VolumeDown => PanelVirtualKey.VolumeDown,
            _ => PanelVirtualKey.None,
        };
}
