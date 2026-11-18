using PanelOS.ControlCenter.Mvvm;
using PanelOS.Windows.StreamDeck;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

public sealed record ActionKindOption(string Label, PanelActionKind? Kind);

public sealed record IconKindOption(string Label, StreamDeckIconKind Kind);

/// <summary>
/// Editor for one selected button. It writes straight into the domain object
/// and raises <see cref="Changed"/>; the owning editor decides what that means
/// for dirty state and for the grid.
/// </summary>
public sealed class StreamDeckButtonEditorViewModel : ObservableObject
{
    private static readonly ActionKindOption[] SelectableKinds =
    [
        new(PanelActionDescriber.NoAction, null),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.LaunchApplication),
            PanelActionKind.LaunchApplication),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.OpenUrl),
            PanelActionKind.OpenUrl),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.KeyboardShortcut),
            PanelActionKind.KeyboardShortcut),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.MediaCommand),
            PanelActionKind.MediaCommand),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.VolumeCommand),
            PanelActionKind.VolumeCommand),
        new(
            PanelActionDescriber.DescribeKind(PanelActionKind.PanelModeCommand),
            PanelActionKind.PanelModeCommand),
    ];

    private static readonly IconKindOption[] SelectableIconKinds =
    [
        new(Strings.Get("IconKindActionDefault"), StreamDeckIconKind.ActionDefault),
        new(Strings.Get("IconKindNone"), StreamDeckIconKind.None),
        new(Strings.Get("IconKindCustom"), StreamDeckIconKind.Custom),
    ];

    private readonly StreamDeckButton _button;
    private readonly StreamDeckEditorServices _services;
    private ActionKindOption _selectedActionKind;
    private IconKindOption _selectedIconKind;

    public StreamDeckButtonEditorViewModel(
        StreamDeckButton button,
        StreamDeckEditorServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        _button = button;
        _services = services ?? StreamDeckEditorServices.None;

        ActionKinds = button.Action is UnknownPanelAction unknown
            ? [.. SelectableKinds, new ActionKindOption(
                Strings.Format("ActionUnknownFormat", unknown.RawKind),
                PanelActionKind.Unknown)]
            : SelectableKinds;
        _selectedActionKind = ActionKinds.First(
            option => option.Kind == button.Action?.Kind);
        _selectedIconKind = SelectableIconKinds.First(
            option => option.Kind == button.Icon.Kind);

        BrowseApplicationCommand = new RelayCommand(
            BrowseApplication,
            () => _services.ApplicationPicker is not null && IsLaunchApplication);
        BrowseIconCommand = new AsyncRelayCommand(
            BrowseIconAsync,
            () => _services.IconPicker is not null &&
                _services.IconImporter is not null);
        UseApplicationIconCommand = new AsyncRelayCommand(
            UseApplicationIconAsync,
            () => _services.IconImporter is not null &&
                As<LaunchApplicationAction>()?.IsConfigured == true);
    }

    public event EventHandler? Changed;

    public StreamDeckButton Button => _button;

    public IReadOnlyList<ActionKindOption> ActionKinds { get; }

    public IReadOnlyList<IconKindOption> IconKinds => SelectableIconKinds;

    public IReadOnlyList<MediaCommand> MediaCommands { get; } =
        Enum.GetValues<MediaCommand>();

    public IReadOnlyList<VolumeCommand> VolumeCommands { get; } =
    [
        VolumeCommand.MuteToggle,
        VolumeCommand.VolumeUp,
        VolumeCommand.VolumeDown,
    ];

    public IReadOnlyList<PanelMode> PanelModes { get; } = Enum.GetValues<PanelMode>();

    public IReadOnlyList<PanelKey> Keys { get; } = Enum.GetValues<PanelKey>();

    public RelayCommand BrowseApplicationCommand { get; }

    public AsyncRelayCommand BrowseIconCommand { get; }

    public AsyncRelayCommand UseApplicationIconCommand { get; }

    public string ButtonId => _button.Id;

    public string Label
    {
        get => _button.Label;
        set
        {
            if (string.Equals(_button.Label, value, StringComparison.Ordinal))
            {
                return;
            }

            _button.Label = value ?? string.Empty;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    /// <summary>
    /// A disabled button keeps its configuration but refuses to run, here and
    /// through a future tablet invocation.
    /// </summary>
    public bool Enabled
    {
        get => _button.Enabled;
        set
        {
            if (_button.Enabled == value)
            {
                return;
            }

            _button.Enabled = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public ActionKindOption SelectedActionKind
    {
        get => _selectedActionKind;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedActionKind == value)
            {
                return;
            }

            _selectedActionKind = value;
            if (value.Kind != _button.Action?.Kind)
            {
                _button.Action = CreateAction(value.Kind);
            }

            OnPropertyChanged();
            OnActionReplaced();
        }
    }

    public bool IsLaunchApplication => _button.Action is LaunchApplicationAction;

    public bool IsOpenUrl => _button.Action is OpenUrlAction;

    public bool IsKeyboardShortcut => _button.Action is KeyboardShortcutAction;

    public bool IsMediaCommand => _button.Action is MediaCommandAction;

    public bool IsVolumeCommand => _button.Action is VolumeCommandAction;

    public bool IsPanelMode => _button.Action is PanelModeAction;

    public bool IsUnknownAction => _button.Action is UnknownPanelAction;

    /// <summary>
    /// PanelMode is modelled and stored, but nothing on the PC can act on it
    /// until the PanelHost integration exists. The editor says so instead of
    /// letting the user find out by pressing the button.
    /// </summary>
    public string PanelModeNotice => Strings.Get("ButtonEditorPanelModeNotice");

    public string? ExecutablePath
    {
        get => As<LaunchApplicationAction>()?.ExecutablePath;
        set => UpdateLaunch(action => action.ExecutablePath = Normalize(value));
    }

    public string? Arguments
    {
        get => As<LaunchApplicationAction>()?.Arguments;
        set => UpdateLaunch(action => action.Arguments = Normalize(value));
    }

    public string? WorkingDirectory
    {
        get => As<LaunchApplicationAction>()?.WorkingDirectory;
        set => UpdateLaunch(action => action.WorkingDirectory = Normalize(value));
    }

    public string? Url
    {
        get => As<OpenUrlAction>()?.Url;
        set
        {
            if (As<OpenUrlAction>() is not { } action)
            {
                return;
            }

            action.Url = Normalize(value);
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public bool ModifierControl
    {
        get => HasModifier(PanelKeyModifiers.Control);
        set => SetModifier(PanelKeyModifiers.Control, value);
    }

    public bool ModifierShift
    {
        get => HasModifier(PanelKeyModifiers.Shift);
        set => SetModifier(PanelKeyModifiers.Shift, value);
    }

    public bool ModifierAlt
    {
        get => HasModifier(PanelKeyModifiers.Alt);
        set => SetModifier(PanelKeyModifiers.Alt, value);
    }

    public bool ModifierWindows
    {
        get => HasModifier(PanelKeyModifiers.Windows);
        set => SetModifier(PanelKeyModifiers.Windows, value);
    }

    public PanelKey ShortcutKey
    {
        get => As<KeyboardShortcutAction>()?.Key ?? PanelKey.None;
        set
        {
            if (As<KeyboardShortcutAction>() is not { } action || action.Key == value)
            {
                return;
            }

            action.Key = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShortcutPreview));
            NotifyChanged();
        }
    }

    public string ShortcutPreview =>
        As<KeyboardShortcutAction>()?.Describe() is { Length: > 0 } description
            ? description
            : Strings.Get("NotAvailableSymbol");

    public MediaCommand MediaCommand
    {
        get => As<MediaCommandAction>()?.Command ?? MediaCommand.PlayPause;
        set
        {
            if (As<MediaCommandAction>() is not { } action || action.Command == value)
            {
                return;
            }

            action.Command = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public VolumeCommand VolumeCommand
    {
        get => As<VolumeCommandAction>()?.Command ?? VolumeCommand.MuteToggle;
        set
        {
            if (As<VolumeCommandAction>() is not { } action || action.Command == value)
            {
                return;
            }

            action.Command = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public PanelMode PanelMode
    {
        get => As<PanelModeAction>()?.Mode ?? PanelMode.Dashboard;
        set
        {
            if (As<PanelModeAction>() is not { } action || action.Mode == value)
            {
                return;
            }

            action.Mode = value;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public IconKindOption SelectedIconKind
    {
        get => _selectedIconKind;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_selectedIconKind == value)
            {
                return;
            }

            _selectedIconKind = value;
            _button.Icon.Kind = value.Kind;
            if (value.Kind != StreamDeckIconKind.Custom)
            {
                _button.Icon.IconId = null;
                _button.Icon.SourcePath = null;
            }

            OnPropertyChanged();
            OnIconChanged();
        }
    }

    public bool IsCustomIcon => _button.Icon.Kind == StreamDeckIconKind.Custom;

    /// <summary>
    /// Absolute path of the cached PNG, for the preview only. The configuration
    /// stores the icon id, and only the id would ever reach the tablet.
    /// </summary>
    public string? IconPreviewPath =>
        _services.IconImporter?.ResolveIconPath(_button.Icon.IconId);

    public string IconDescription
    {
        get
        {
            if (_button.Icon.Kind != StreamDeckIconKind.Custom)
            {
                return Strings.Get("NotAvailableSymbol");
            }

            if (_button.Icon.IconId is null)
            {
                return Strings.Get("IconNoneImported");
            }

            var origin = _button.Icon.SourcePath is { Length: > 0 } source
                ? Path.GetFileName(source)
                : Strings.Get("IconImportedImage");
            return Strings.Format("IconDescriptionFormat", origin, _button.Icon.IconId);
        }
    }

    public string ActionSummary => PanelActionDescriber.Describe(_button.Action);

    private static PanelAction? CreateAction(PanelActionKind? kind) => kind switch
    {
        PanelActionKind.LaunchApplication => new LaunchApplicationAction(),
        PanelActionKind.OpenUrl => new OpenUrlAction(),
        PanelActionKind.KeyboardShortcut => new KeyboardShortcutAction(),
        PanelActionKind.MediaCommand => new MediaCommandAction(),
        PanelActionKind.VolumeCommand => new VolumeCommandAction(),
        PanelActionKind.PanelModeCommand => new PanelModeAction(),
        _ => null,
    };

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void BrowseApplication()
    {
        if (_services.ApplicationPicker?.PickApplication() is not { } pick ||
            As<LaunchApplicationAction>() is not { } action)
        {
            return;
        }

        action.ExecutablePath = pick.ExecutablePath;
        OnPropertyChanged(nameof(ExecutablePath));

        // Only fill an empty label: a name the user already chose is theirs.
        if (string.IsNullOrWhiteSpace(_button.Label))
        {
            Label = pick.DisplayName;
        }

        NotifyChanged();
        UseApplicationIconCommand.RaiseCanExecuteChanged();

        // A launcher without an icon gets the application's own, which is what
        // the user expects to see on the button.
        if (_button.Icon.Kind != StreamDeckIconKind.Custom)
        {
            _ = UseApplicationIconCommand.ExecuteAsync(CancellationToken.None);
        }
    }

    private async Task BrowseIconAsync(CancellationToken cancellationToken)
    {
        if (_services.IconPicker?.PickImageFile() is not { } imagePath ||
            _services.IconImporter is not { } importer)
        {
            return;
        }

        var iconId = await importer
            .ImportImageAsync(imagePath, cancellationToken)
            .ConfigureAwait(true);
        ApplyImportedIcon(iconId, imagePath);
    }

    private async Task UseApplicationIconAsync(CancellationToken cancellationToken)
    {
        if (As<LaunchApplicationAction>() is not { ExecutablePath: { } path } ||
            _services.IconImporter is not { } importer ||
            string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var iconId = await importer
            .ImportExecutableIconAsync(path, cancellationToken)
            .ConfigureAwait(true);
        ApplyImportedIcon(iconId, path);
    }

    private void ApplyImportedIcon(string? iconId, string sourcePath)
    {
        if (iconId is null)
        {
            return;
        }

        _button.Icon.Kind = StreamDeckIconKind.Custom;
        _button.Icon.IconId = iconId;
        _button.Icon.SourcePath = sourcePath;
        _selectedIconKind = SelectableIconKinds.First(
            option => option.Kind == StreamDeckIconKind.Custom);
        OnPropertyChanged(nameof(SelectedIconKind));
        OnIconChanged();
    }

    private void OnIconChanged()
    {
        OnPropertyChanged(nameof(IsCustomIcon));
        OnPropertyChanged(nameof(IconPreviewPath));
        OnPropertyChanged(nameof(IconDescription));
        NotifyChanged();
    }

    private T? As<T>() where T : PanelAction => _button.Action as T;

    private bool HasModifier(PanelKeyModifiers modifier) =>
        As<KeyboardShortcutAction>()?.Modifiers.HasFlag(modifier) == true;

    private void SetModifier(PanelKeyModifiers modifier, bool enabled)
    {
        if (As<KeyboardShortcutAction>() is not { } action ||
            action.Modifiers.HasFlag(modifier) == enabled)
        {
            return;
        }

        action.Modifiers = enabled
            ? action.Modifiers | modifier
            : action.Modifiers & ~modifier;
        OnPropertyChanged(nameof(ModifierControl));
        OnPropertyChanged(nameof(ModifierShift));
        OnPropertyChanged(nameof(ModifierAlt));
        OnPropertyChanged(nameof(ModifierWindows));
        OnPropertyChanged(nameof(ShortcutPreview));
        NotifyChanged();
    }

    private void UpdateLaunch(Action<LaunchApplicationAction> update)
    {
        if (As<LaunchApplicationAction>() is not { } action)
        {
            return;
        }

        update(action);
        OnPropertyChanged(nameof(ExecutablePath));
        OnPropertyChanged(nameof(Arguments));
        OnPropertyChanged(nameof(WorkingDirectory));
        UseApplicationIconCommand.RaiseCanExecuteChanged();
        NotifyChanged();
    }

    private void OnActionReplaced()
    {
        OnPropertyChanged(nameof(IsLaunchApplication));
        OnPropertyChanged(nameof(IsOpenUrl));
        OnPropertyChanged(nameof(IsKeyboardShortcut));
        OnPropertyChanged(nameof(IsMediaCommand));
        OnPropertyChanged(nameof(IsVolumeCommand));
        OnPropertyChanged(nameof(IsPanelMode));
        OnPropertyChanged(nameof(IsUnknownAction));
        OnPropertyChanged(nameof(ExecutablePath));
        OnPropertyChanged(nameof(Arguments));
        OnPropertyChanged(nameof(WorkingDirectory));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(ModifierControl));
        OnPropertyChanged(nameof(ModifierShift));
        OnPropertyChanged(nameof(ModifierAlt));
        OnPropertyChanged(nameof(ModifierWindows));
        OnPropertyChanged(nameof(ShortcutKey));
        OnPropertyChanged(nameof(ShortcutPreview));
        OnPropertyChanged(nameof(MediaCommand));
        OnPropertyChanged(nameof(VolumeCommand));
        OnPropertyChanged(nameof(PanelMode));
        BrowseApplicationCommand.RaiseCanExecuteChanged();
        UseApplicationIconCommand.RaiseCanExecuteChanged();
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(ActionSummary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
