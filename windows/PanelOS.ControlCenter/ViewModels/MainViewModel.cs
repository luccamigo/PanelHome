using System.Collections.ObjectModel;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.Windows.Driver;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>Sections of the shell, in sidebar order.</summary>
public enum PanelSection
{
    Overview,
    Device,
    StreamDeck,
    Wifi,
    Integrations,
    Diagnostics,
    LoopMedia,
    Settings,
}

/// <summary>
/// One sidebar entry and the page behind it.
/// </summary>
/// <remarks>
/// The title and description live here rather than inside each view so a page
/// never repeats what the sidebar already said, and so every page opens the
/// same way: what this screen is, then one line on what it is for.
/// </remarks>
public sealed class SectionViewModel : ObservableObject
{
    private bool _hasAttention;

    public SectionViewModel(
        PanelSection section,
        string name,
        string description,
        string iconKey,
        object content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconKey);
        ArgumentNullException.ThrowIfNull(content);
        Section = section;
        Name = name;
        Description = description;
        IconKey = iconKey;
        Content = content;
    }

    public PanelSection Section { get; }

    public string Name { get; }

    /// <summary>One line under the page title.</summary>
    public string Description { get; }

    /// <summary>Key of the geometry in the theme; views never build a path.</summary>
    public string IconKey { get; }

    public object Content { get; }

    /// <summary>A small warning dot on the sidebar entry: something on that page is waiting for the operator.</summary>
    public bool HasAttention { get => _hasAttention; set => SetProperty(ref _hasAttention, value); }
}

/// <summary>
/// One entry of the status strip in the window header: which component, how it
/// is doing, in one word.
/// </summary>
public sealed record ShellStatus(string Label, string Text, PanelStatusLevel Level);

/// <summary>
/// Shell: the sidebar, the current section, and the state that stays visible on
/// every page - the four components across the header, the driver and the build
/// along the bottom.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly DashboardViewModel _device;
    private SectionViewModel _selectedSection;
    private string _driverStatusText = Strings.Get("DriverStatusQuerying");
    private PanelStatusLevel _driverStatusLevel = PanelStatusLevel.Neutral;

    public MainViewModel(
        OverviewViewModel overview,
        DashboardViewModel device,
        StreamDeckEditorViewModel streamDeck,
        DiagnosticsViewModel diagnostics,
        SettingsViewModel settings,
        LoopMediaViewModel loopMedia,
        WifiPairingViewModel? wifi = null,
        IntegrationsViewModel? integrations = null)
    {
        ArgumentNullException.ThrowIfNull(overview);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(streamDeck);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(loopMedia);

        Overview = overview;
        Device = device;
        _device = device;
        StreamDeck = streamDeck;
        Diagnostics = diagnostics;
        Settings = settings;
        LoopMedia = loopMedia;
        Wifi = wifi;
        Integrations = integrations;

        var sections = new List<SectionViewModel>
        {
            new SectionViewModel(
                PanelSection.Overview,
                Strings.Get("NavOverview"),
                Strings.Get("NavOverviewDescription"),
                "IconOverview",
                overview),
            new SectionViewModel(
                PanelSection.Device,
                Strings.Get("NavDevice"),
                Strings.Get("NavDeviceDescription"),
                "IconDevice",
                device),
            new SectionViewModel(
                PanelSection.StreamDeck,
                Strings.Get("NavStreamDeck"),
                Strings.Get("NavStreamDeckDescription"),
                "IconStreamDeck",
                streamDeck),
        };
        // Wi-Fi sits after Stream Deck and before the engineering pages. Without a pairing view model (older
        // composition roots and tests) the sidebar is exactly what it was.
        if (wifi is not null)
        {
            var wifiSection = new SectionViewModel(
                PanelSection.Wifi,
                Strings.Get("NavWifi"),
                Strings.Get("NavWifiDescription"),
                "IconWifi",
                wifi);
            sections.Add(wifiSection);
            // The 60 s approval is easy to miss from another page: the sidebar entry shows a dot while a request waits.
            wifi.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WifiPairingViewModel.HasPending)) wifiSection.HasAttention = wifi.HasPending;
            };
        }

        if (integrations is not null)
        {
            sections.Add(new SectionViewModel(PanelSection.Integrations,
                Strings.Get("NavIntegrations"), Strings.Get("NavIntegrationsDescription"), "IconIntegrations", integrations));
        }

        sections.Add(new SectionViewModel(
            PanelSection.Diagnostics,
            Strings.Get("NavDiagnostics"),
            Strings.Get("NavDiagnosticsDescription"),
            "IconDiagnostics",
            diagnostics));
        sections.Add(new SectionViewModel(
            PanelSection.LoopMedia,
            Strings.Get("NavLoopMedia"),
            Strings.Get("NavLoopMediaDescription"),
            "IconVideo",
            loopMedia));
        sections.Add(new SectionViewModel(
            PanelSection.Settings,
            Strings.Get("NavSettings"),
            Strings.Get("NavSettingsDescription"),
            "IconSettings",
            settings));
        Sections = sections;
        _selectedSection = Sections[0];

        device.SnapshotChanged += (_, _) =>
        {
            UpdateDriverStatus(device.Snapshot);
            UpdateShellStatuses();
        };

        // A quick action sends the operator to the page that owns the task
        // instead of that page's controls growing a second copy here.
        device.NavigationRequested += (_, section) => Navigate(section);

        // The editor owns the configuration; the device view model only reports
        // what it persisted, so the summary follows every save without a second
        // reader of the file.
        streamDeck.SummaryChanged += (_, _) =>
            device.ApplyStreamDeckSummary(streamDeck.Summary);

        UpdateShellStatuses();
    }

    public OverviewViewModel Overview { get; }

    /// <summary>Live device state; also the content of the Device section.</summary>
    public DashboardViewModel Device { get; }

    public StreamDeckEditorViewModel StreamDeck { get; }

    public DiagnosticsViewModel Diagnostics { get; }

    public SettingsViewModel Settings { get; }
    public LoopMediaViewModel LoopMedia { get; }

    public WifiPairingViewModel? Wifi { get; }
    public IntegrationsViewModel? Integrations { get; }

    public IReadOnlyList<SectionViewModel> Sections { get; }

    /// <summary>
    /// The four components, always on screen. It is what stops "is the panel
    /// working?" from being a navigation exercise.
    /// </summary>
    public ObservableCollection<ShellStatus> Statuses { get; } = [];

    public SectionViewModel SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value is not null)
            {
                if (_selectedSection.Content is IntegrationsViewModel previous && !ReferenceEquals(_selectedSection, value))
                    previous.ClearSensitive();
                SetProperty(ref _selectedSection, value);
            }
        }
    }

    /// <summary>Driver state, shown in the footer on every page.</summary>
    public string DriverStatusText
    {
        get => _driverStatusText;
        private set => SetProperty(ref _driverStatusText, value);
    }

    public PanelStatusLevel DriverStatusLevel
    {
        get => _driverStatusLevel;
        private set => SetProperty(ref _driverStatusLevel, value);
    }

    public string ControlCenterVersion => Services.ControlCenterVersion.Current;

    public void Navigate(PanelSection section)
    {
        var target = Sections.FirstOrDefault(item => item.Section == section);
        if (target is not null)
        {
            SelectedSection = target;
        }
    }

    /// <summary>
    /// Loads every section. A failure in one of them must not stop the window
    /// from opening, so each load is isolated.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SafeAsync(() => Device.RefreshAsync(cancellationToken))
            .ConfigureAwait(true);
        await SafeAsync(() => Diagnostics.RefreshAsync(cancellationToken))
            .ConfigureAwait(true);
        await SafeAsync(() => StreamDeck.LoadAsync(cancellationToken))
            .ConfigureAwait(true);
        await SafeAsync(() => Settings.LoadAsync(cancellationToken))
            .ConfigureAwait(true);
        await SafeAsync(() => LoopMedia.LoadAsync(cancellationToken))
            .ConfigureAwait(true);
        if (Integrations is not null) await SafeAsync(() => Integrations.RefreshAsync(cancellationToken)).ConfigureAwait(true);
    }

    private static async Task SafeAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Each view model already reports its own failures in the UI; the
            // shell only guarantees the remaining sections still load.
        }
    }

    /// <summary>
    /// Rebuilds the header strip in place. The collection is replaced item by
    /// item rather than cleared and refilled so the four chips do not flicker
    /// on every snapshot the agent pushes.
    /// </summary>
    private void UpdateShellStatuses()
    {
        ShellStatus[] next =
        [
            new(
                Strings.Get("ShellStatusHost"),
                _device.PanelHostStateText,
                _device.PanelHostLevel),
            new(
                Strings.Get("ShellStatusTablet"),
                _device.TabletStatusText,
                _device.TabletLevel),
            new(
                Strings.Get("ShellStatusDisplay"),
                _device.MonitorText,
                _device.MonitorLevel),
            new(
                Strings.Get("ShellStatusVideo"),
                _device.VideoStatusText,
                _device.VideoLevel),
        ];

        for (var index = 0; index < next.Length; index++)
        {
            if (index >= Statuses.Count)
            {
                Statuses.Add(next[index]);
            }
            else if (Statuses[index] != next[index])
            {
                Statuses[index] = next[index];
            }
        }
    }

    private void UpdateDriverStatus(PanelDriverSnapshot snapshot)
    {
        (DriverStatusText, DriverStatusLevel) = snapshot.Availability switch
        {
            PanelDriverAvailability.NotInstalled =>
                (Strings.Get("DriverStatusNotFound"), PanelStatusLevel.Bad),
            PanelDriverAvailability.AccessDenied =>
                (Strings.Get("DriverStatusAccessDenied"), PanelStatusLevel.Warning),
            PanelDriverAvailability.Error =>
                (Strings.Get("DriverUnavailable"), PanelStatusLevel.Bad),
            PanelDriverAvailability.Available => snapshot switch
            {
                { IsMonitorConnected: true } =>
                    (Strings.Get("DriverStatusDisplayConnected"), PanelStatusLevel.Good),
                { Lifecycle: PanelDriverLifecycle.Ready } =>
                    (Strings.Get("DriverStatusReady"), PanelStatusLevel.Good),
                { Lifecycle: PanelDriverLifecycle.Initializing } =>
                    (Strings.Get("DriverStatusInitializing"), PanelStatusLevel.Warning),
                { Lifecycle: PanelDriverLifecycle.Stopped } =>
                    (Strings.Get("DriverStatusStopped"), PanelStatusLevel.Bad),
                _ => (Strings.Get("DriverStatusInstalled"), PanelStatusLevel.Neutral),
            },
            _ => (Strings.Get("DriverStatusQuerying"), PanelStatusLevel.Neutral),
        };
    }
}
