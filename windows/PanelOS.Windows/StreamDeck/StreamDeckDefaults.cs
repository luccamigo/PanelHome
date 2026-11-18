namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// The profile a fresh installation starts from: one 3×4 page whose first two
/// rows already work on any Windows machine - media, volume and system
/// shortcuts need no path to be configured - and whose last row is left for the
/// user's own applications. No executable is invented: a guessed install
/// location would produce a button that fails the first time it is pressed.
/// </summary>
public static class StreamDeckDefaults
{
    public const string MainProfileId = "profile-main";
    public const string MainPageId = "page-main";

    public static StreamDeckConfiguration CreateConfiguration() => new()
    {
        Version = StreamDeckConfiguration.CurrentSchemaVersion,
        ActiveProfileId = MainProfileId,
        Profiles = [CreateMainProfile()],
    };

    public static StreamDeckProfile CreateMainProfile() => new()
    {
        Id = MainProfileId,
        Name = "Main",
        Pages = [CreateMainPage()],
    };

    private static StreamDeckPage CreateMainPage() => new()
    {
        Id = MainPageId,
        Name = "Principal",
        Rows = StreamDeckPage.DefaultRows,
        Columns = StreamDeckPage.DefaultColumns,
        Buttons =
        [
            // Row 0 - media transport, via the Windows media keys.
            Button("btn-play-pause", 0, 0, "Play/Pause", new MediaCommandAction
            {
                Command = MediaCommand.PlayPause,
            }),
            Button("btn-previous", 0, 1, "Anterior", new MediaCommandAction
            {
                Command = MediaCommand.Previous,
            }),
            Button("btn-next", 0, 2, "Próxima", new MediaCommandAction
            {
                Command = MediaCommand.Next,
            }),
            Button("btn-mute", 0, 3, "Mute", new VolumeCommandAction
            {
                Command = VolumeCommand.MuteToggle,
            }),

            // Row 1 - volume and the two shortcuts everyone reaches for.
            Button("btn-volume-up", 1, 0, "Volume +", new VolumeCommandAction
            {
                Command = VolumeCommand.VolumeUp,
            }),
            Button("btn-volume-down", 1, 1, "Volume −", new VolumeCommandAction
            {
                Command = VolumeCommand.VolumeDown,
            }),
            Button("btn-show-desktop", 1, 2, "Win+D", new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Windows,
                Key = PanelKey.D,
            }),
            Button("btn-screenshot", 1, 3, "Captura", new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Windows | PanelKeyModifiers.Shift,
                Key = PanelKey.S,
            }),

            // Row 2 - placeholders. They are deliberately incomplete: the user
            // points them at an application with "Procurar…".
            Button("btn-app-1", 2, 0, "Aplicativo 1", new LaunchApplicationAction()),
            Button("btn-app-2", 2, 1, "Aplicativo 2", new LaunchApplicationAction()),
            Button("btn-app-3", 2, 2, "Aplicativo 3", new LaunchApplicationAction()),
            Button("btn-browser", 2, 3, "Site", new OpenUrlAction()),
        ],
    };

    private static StreamDeckButton Button(
        string id,
        int row,
        int column,
        string label,
        PanelAction action) => new()
        {
            Id = id,
            Row = row,
            Column = column,
            Label = label,
            Action = action,
        };
}
