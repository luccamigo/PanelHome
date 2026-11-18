using System.Collections.ObjectModel;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>Where a diagnostic row belongs, decided when the row is created.</summary>
/// <remarks>
/// The grouping used to be recovered afterwards by matching label text, which
/// meant renaming a label silently moved a row into "everything else". The
/// section is now stated once, at the only place that knows it.
/// </remarks>
public enum DiagnosticSection
{
    Summary,
    Host,
    Transport,
    Display,
    Video,
    StreamDeck,
    Files,
    Logs,
}

/// <summary>
/// One measured fact. <see cref="Technical"/> holds the raw form - a Win32 code,
/// a path, a GUID - so the row can read as a sentence and still be expanded
/// when someone is actually debugging.
/// </summary>
public sealed record DiagnosticRow(
    string Label,
    string Value,
    PanelStatusLevel Level = PanelStatusLevel.Neutral,
    string? Technical = null,
    string? Hint = null)
{
    public bool HasTechnical => !string.IsNullOrWhiteSpace(Technical);

    public bool HasHint => !string.IsNullOrWhiteSpace(Hint);
}

/// <summary>
/// A named group of rows, and one segment of the Diagnostics page. Fourteen
/// facts in one flat list is a dump; the same fourteen behind "Host",
/// "Transporte" and "Vídeo" is a diagnosis.
/// </summary>
public sealed class DiagnosticGroup
{
    public DiagnosticGroup(DiagnosticSection section, string title, bool isLogs = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Section = section;
        Title = title;
        IsLogs = isLogs;
    }

    public DiagnosticSection Section { get; }

    public string Title { get; }

    /// <summary>The log viewer occupies its segment instead of a row list.</summary>
    public bool IsLogs { get; }

    /// <summary>The summary segment carries a line explaining how to read it.</summary>
    public bool IsSummary => Section == DiagnosticSection.Summary;

    /// <summary>The video segment also shows the live metric tiles.</summary>
    public bool IsVideo => Section == DiagnosticSection.Video;

    public List<DiagnosticRow> Rows { get; } = [];
}

public sealed record RuntimeMetric(
    string Label,
    string Value,
    string Detail,
    PanelStatusLevel Level = PanelStatusLevel.Neutral);

/// <summary>One choice in the log's stream filter.</summary>
public sealed record LogStreamFilter(string Label, PanelHostLogStream? Stream);

public sealed class DiagnosticsViewModel : ObservableObject
{
    /// <summary>Lines kept on screen; the buffer behind it is bounded too.</summary>
    private const int MaximumVisibleLogEntries = PanelHostLogBuffer.DefaultCapacity;

    private readonly IPanelDriverController _driver;
    private readonly IStreamDeckConfigurationStore _configurationStore;
    private readonly IPanelHostProcessManager _panelHost;
    private readonly IUiScheduler _uiScheduler;
    private readonly IClipboardService? _clipboard;
    private readonly IPanelHostManagementClient? _managementClient;
    private readonly List<(DiagnosticSection Section, DiagnosticRow Row)> _sectionedRows = [];
    private PanelHostSnapshot? _runtimeSnapshot;

    private DiagnosticGroup? _selectedGroup;
    private string _logFilter = string.Empty;
    private LogStreamFilter _logStream;
    private bool _autoScrollLog = true;

    public DiagnosticsViewModel(
        IPanelDriverController driver,
        IStreamDeckConfigurationStore configurationStore,
        IPanelHostProcessManager panelHost,
        IClipboardService? clipboard = null,
        IUiScheduler? uiScheduler = null,
        IPanelHostManagementClient? managementClient = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(configurationStore);
        ArgumentNullException.ThrowIfNull(panelHost);
        _driver = driver;
        _configurationStore = configurationStore;
        _panelHost = panelHost;
        _clipboard = clipboard;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;
        _managementClient = managementClient;
        _runtimeSnapshot = managementClient?.Snapshot;

        LogStreamFilters =
        [
            new(Strings.Get("DiagnosticsLogStreamAll"), null),
            new(Strings.Get("DiagnosticsLogStreamError"), PanelHostLogStream.StandardError),
            new(Strings.Get("DiagnosticsLogStreamOutput"), PanelHostLogStream.StandardOutput),
            new(Strings.Get("DiagnosticsLogStreamManager"), PanelHostLogStream.Manager),
        ];
        _logStream = LogStreamFilters[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CopyLogCommand = new RelayCommand(
            CopyLog,
            () => _clipboard is not null && VisibleLogEntries.Count > 0);
        ClearLogViewCommand = new RelayCommand(
            ClearLogView,
            () => LogEntries.Count > 0);
        CopyRowCommand = new RelayCommand<DiagnosticRow>(
            CopyRow,
            _ => _clipboard is not null);

        foreach (var entry in _panelHost.Log.Snapshot())
        {
            LogEntries.Add(entry);
        }

        RebuildVisibleLog();
        _panelHost.Log.Appended += OnLogAppended;
        if (_managementClient is not null)
        {
            _managementClient.SnapshotChanged += OnManagementSnapshotChanged;
            _managementClient.ConnectionChanged += OnManagementConnectionChanged;
        }

        PublishRuntimeMetrics();
        RebuildGroups();
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand CopyLogCommand { get; }

    public RelayCommand ClearLogViewCommand { get; }

    /// <summary>Copies one row's technical form, which is the part worth pasting.</summary>
    public RelayCommand<DiagnosticRow> CopyRowCommand { get; }

    /// <summary>Every row, flat, in the order they were measured.</summary>
    public ObservableCollection<DiagnosticRow> Rows { get; } = [];

    /// <summary>The same rows filed under the component each one describes.</summary>
    public ObservableCollection<DiagnosticGroup> Groups { get; } = [];

    public ObservableCollection<RuntimeMetric> RuntimeMetrics { get; } = [];

    /// <summary>The segment on screen. The log viewer is one of them.</summary>
    public DiagnosticGroup? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (value is not null && SetProperty(ref _selectedGroup, value))
            {
                OnPropertyChanged(nameof(IsLogsSelected));
            }
        }
    }

    public bool IsLogsSelected => _selectedGroup?.IsLogs == true;

    /// <summary>
    /// Visible subset of <see cref="LogEntries"/>. Filtering happens here so
    /// the buffer keeps every captured line while the screen shows the ones
    /// being looked for.
    /// </summary>
    public ObservableCollection<PanelHostLogEntry> VisibleLogEntries { get; } = [];

    /// <summary>Substring the log is filtered by; empty shows everything.</summary>
    public string LogFilter
    {
        get => _logFilter;
        set
        {
            if (SetProperty(ref _logFilter, value ?? string.Empty))
            {
                RebuildVisibleLog();
            }
        }
    }

    /// <summary>
    /// The streams the agent writes on, offered as the log's severity filter.
    /// It is the only level the capture actually carries - inventing INFO/WARN
    /// would mean parsing the text, and no decision here may depend on that.
    /// </summary>
    public IReadOnlyList<LogStreamFilter> LogStreamFilters { get; }

    public LogStreamFilter SelectedLogStream
    {
        get => _logStream;
        set
        {
            if (value is not null && SetProperty(ref _logStream, value))
            {
                RebuildVisibleLog();
            }
        }
    }

    /// <summary>
    /// Keeps the newest line in view. Off while the operator is reading back,
    /// which is the only time scrolling away means anything.
    /// </summary>
    public bool AutoScrollLog
    {
        get => _autoScrollLog;
        set => SetProperty(ref _autoScrollLog, value);
    }

    public bool HasVisibleLogEntries => VisibleLogEntries.Count > 0;

    /// <summary>True when a filter is hiding lines that were captured.</summary>
    public bool IsLogFiltered =>
        !string.IsNullOrWhiteSpace(_logFilter) || _logStream.Stream is not null;

    public string LogCountText => IsLogFiltered
        ? Strings.Format("DiagnosticsLogCountFiltered", VisibleLogEntries.Count, LogEntries.Count)
        : Strings.Format("DiagnosticsLogCount", LogEntries.Count);

    /// <summary>
    /// Output of an agent the Control Center started. An externally started
    /// PanelHost keeps its own console, so nothing appears here for it.
    /// </summary>
    public ObservableCollection<PanelHostLogEntry> LogEntries { get; } = [];

    public string ControlCenterVersion { get; } = Services.ControlCenterVersion.Current;

    public string LogDirectory => PanelOSPaths.LogDirectory;

    public bool HasLogEntries => LogEntries.Count > 0;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _driver.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(true);
        var host = _panelHost.Refresh();
        var executable = _panelHost.LocateExecutable();

        Rows.Clear();
        _sectionedRows.Clear();

        AddSummaryRows(snapshot, host);
        AddHostRows(host, executable);
        AddTransportRows();
        AddDisplayRows(snapshot);
        AddVideoRows();
        AddStreamDeckRows();
        AddFileRows();

        RebuildGroups();
    }

    // ==================================================================
    // Rows
    // ==================================================================

    /// <summary>
    /// The headline: what is wrong, said in Portuguese, with the raw form
    /// tucked behind "detalhes técnicos". A row is only added when it has
    /// something to report, so the section is short enough to read.
    /// </summary>
    private void AddSummaryRows(PanelDriverSnapshot snapshot, PanelHostStatus host)
    {
        Add(DiagnosticSection.Summary, new DiagnosticRow(
            Strings.Get("SummaryDriverLabel"),
            snapshot.Availability switch
            {
                PanelDriverAvailability.Available => Strings.Get("SummaryDriverOk"),
                PanelDriverAvailability.NotInstalled => Strings.Get("SummaryDriverMissing"),
                PanelDriverAvailability.AccessDenied => Strings.Get("SummaryDriverDenied"),
                PanelDriverAvailability.Error => Strings.Get("SummaryDriverFailed"),
                _ => Strings.Get("Querying"),
            },
            snapshot.IsAvailable ? PanelStatusLevel.Good : PanelStatusLevel.Bad,
            snapshot.Detail,
            snapshot.Availability switch
            {
                PanelDriverAvailability.NotInstalled =>
                    Strings.Get("ProblemDriverMissingDetail"),
                PanelDriverAvailability.AccessDenied =>
                    Strings.Get("ProblemDriverAccessDetail"),
                _ => null,
            }));

        Add(DiagnosticSection.Summary, new DiagnosticRow(
            Strings.Get("SummaryHostLabel"),
            host.State switch
            {
                PanelHostProcessState.Running => Strings.Get("SummaryHostRunning"),
                PanelHostProcessState.Error => Strings.Get("SummaryHostError"),
                PanelHostProcessState.Starting => Strings.Get("HostStateStarting"),
                PanelHostProcessState.Stopping => Strings.Get("HostStateStopping"),
                _ => Strings.Get("SummaryHostStopped"),
            },
            host.State switch
            {
                PanelHostProcessState.Running => PanelStatusLevel.Good,
                PanelHostProcessState.Error => PanelStatusLevel.Bad,
                _ => PanelStatusLevel.Warning,
            },
            host.Detail,
            host.State is PanelHostProcessState.Running
                ? null
                : Strings.Get("ProblemHostStoppedDetail")));

        Add(DiagnosticSection.Summary, new DiagnosticRow(
            Strings.Get("SummaryManagementLabel"),
            _runtimeSnapshot is not null
                ? Strings.Get("SummaryManagementOk")
                : Strings.Get("SummaryManagementMissing"),
            _runtimeSnapshot is not null
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Warning,
            Strings.Format(
                "SummaryManagementTechnicalFormat",
                PanelHostManagementProtocol.PipeName,
                _managementClient?.ConnectionState.ToString()
                    ?? PanelHostManagementConnectionState.Disconnected.ToString()),
            _runtimeSnapshot is not null ? null : Strings.Get("ProblemManagementDetail")));

        Add(DiagnosticSection.Summary, new DiagnosticRow(
            Strings.Get("SummaryTabletLabel"),
            _runtimeSnapshot?.Tablet is { } tablet
                ? tablet.ControlConnected
                    ? Strings.Get("SummaryTabletConnected")
                    : Strings.Get("SummaryTabletWaiting")
                : Strings.Get("NotAvailableSymbol"),
            _runtimeSnapshot?.Tablet.ControlConnected == true
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));

        Add(DiagnosticSection.Summary, new DiagnosticRow(
            Strings.Get("SummaryVideoLabel"),
            _runtimeSnapshot?.Video is { } video
                ? video.Connected
                    ? Strings.Get("VideoStateActive")
                    : Strings.Get("VideoStateInactive")
                : Strings.Get("NotAvailableSymbol"),
            _runtimeSnapshot?.Video.Connected == true
                ? _runtimeSnapshot.Video.SequenceGap > 0
                    ? PanelStatusLevel.Warning
                    : PanelStatusLevel.Good
                : PanelStatusLevel.Neutral,
            null,
            _runtimeSnapshot?.Video is { Connected: true, SequenceGap: > 0 }
                ? Strings.Get("ProblemVideoGapDetail")
                : null));

        if (_runtimeSnapshot?.Services is { } services)
        {
            Add(DiagnosticSection.Summary, new DiagnosticRow(
                Strings.Get("SummaryTelemetryLabel"),
                DescribeRuntimeService(services.Telemetry),
                RuntimeServiceLevel(services.Telemetry),
                services.Telemetry.ErrorCode));
            Add(DiagnosticSection.Summary, new DiagnosticRow(
                Strings.Get("SummaryMediaLabel"),
                DescribeRuntimeService(services.Media),
                RuntimeServiceLevel(services.Media),
                services.Media.ErrorCode));
        }
    }

    private static string DescribeRuntimeService(HostServiceManagementSnapshot service) =>
        service.State switch
        {
            "running" => Strings.Get("RuntimeServiceRunning"),
            "degraded" => Strings.Get("RuntimeServiceDegraded"),
            "starting" => Strings.Get("HostStateStarting"),
            _ => Strings.Get("NotAvailableSymbol"),
        };

    private static PanelStatusLevel RuntimeServiceLevel(
        HostServiceManagementSnapshot service) => service.State switch
        {
            "running" => PanelStatusLevel.Good,
            "degraded" => PanelStatusLevel.Warning,
            _ => PanelStatusLevel.Neutral,
        };

    private void AddHostRows(
        PanelHostStatus host,
        PanelHostExecutable? executable)
    {
        Add(DiagnosticSection.Host, new DiagnosticRow(
            Strings.Get("PanelHostName"),
            host.State switch
            {
                PanelHostProcessState.Running => host.OwnedByControlCenter
                    ? Strings.Format("HostRunningManaged", host.ProcessId)
                    : Strings.Format("HostRunningExternal", host.ProcessId),
                PanelHostProcessState.Error => Strings.Get("ErrorLower"),
                _ => host.State.ToString().ToLowerInvariant(),
            },
            host.State switch
            {
                PanelHostProcessState.Running => PanelStatusLevel.Good,
                PanelHostProcessState.Error => PanelStatusLevel.Bad,
                _ => PanelStatusLevel.Neutral,
            },
            host.Detail));
        Add(DiagnosticSection.Host, new DiagnosticRow(
            Strings.Get("DiagnosticsHostExecutable"),
            executable is null
                ? Strings.Get("NotFoundLower")
                : Strings.Format(
                    "PathWithSourceFormat",
                    executable.Path,
                    executable.Source),
            executable is null ? PanelStatusLevel.Warning : PanelStatusLevel.Neutral,
            executable?.Path));
        if (host.LastExitCode is { } exitCode)
        {
            Add(DiagnosticSection.Host, new DiagnosticRow(
                Strings.Get("DiagnosticsHostLastExit"),
                host.LastRunDuration is { } duration
                    ? Strings.Format("HostExitWithDuration", exitCode, duration.TotalSeconds)
                    : Strings.Format("HostExitCode", exitCode),
                PanelStatusLevel.Warning));
        }

        Add(DiagnosticSection.Host, new DiagnosticRow(
            Strings.Get("DiagnosticsLocalIpc"),
            _runtimeSnapshot is not null
                ? Strings.Get("IpcConnected")
                : (_managementClient?.ConnectionState ??
                    PanelHostManagementConnectionState.Disconnected) switch
                {
                    PanelHostManagementConnectionState.Connecting =>
                        Strings.Get("IpcReconnecting"),
                    _ => Strings.Get("UnavailableLower"),
                },
            _runtimeSnapshot is not null
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral,
            PanelHostManagementProtocol.PipeName));
        Add(DiagnosticSection.Host, new DiagnosticRow(
            Strings.Get("DiagnosticsManagementProtocol"),
            _runtimeSnapshot is { } runtime
                ? $"v{runtime.ManagementProtocolVersion}"
                : Strings.Get("NotAvailableSymbol")));
        Add(DiagnosticSection.Host, new DiagnosticRow(
            Strings.Get("ControlCenterName"),
            ControlCenterVersion));
    }

    private void AddTransportRows()
    {
        Add(DiagnosticSection.Transport, new DiagnosticRow(
            Strings.Get("DiagnosticsTabletControl"),
            _runtimeSnapshot?.Tablet is { } tablet
                ? tablet.ControlConnected
                    ? Strings.Format("TabletConnectedProtocol", tablet.ProtocolVersion)
                    : Strings.Get("DisconnectedLower")
                : Strings.Get("NotAvailableSymbol"),
            _runtimeSnapshot?.Tablet.ControlConnected == true
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));
        Add(DiagnosticSection.Transport, new DiagnosticRow(
            Strings.Get("DiagnosticsTabletVideo"),
            _runtimeSnapshot?.Tablet.VideoConnected switch
            {
                true => Strings.Get("ConnectedLower"),
                false => Strings.Get("DisconnectedLower"),
                null => Strings.Get("NotAvailableSymbol"),
            },
            _runtimeSnapshot?.Tablet.VideoConnected == true
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));
        Add(DiagnosticSection.Transport, new DiagnosticRow(
            Strings.Get("DiagnosticsTabletFeatures"),
            _runtimeSnapshot?.Tablet.NegotiatedFeatures is { Length: > 0 } features
                ? string.Join(", ", features)
                : Strings.Get("NotAvailableSymbol")));
        Add(DiagnosticSection.Transport, new DiagnosticRow(
            Strings.Get("DiagnosticsTabletSessions"),
            _runtimeSnapshot?.Tablet is { } sessions
                ? Strings.Format(
                    "TabletSessionFormat",
                    sessions.ControlSessionGeneration,
                    sessions.VideoSessionGeneration)
                : Strings.Get("NotAvailableSymbol")));

        // Which physical path the tablet used is not part of the management
        // snapshot, so it is reported as unpublished rather than guessed from
        // the port the session arrived on.
        Add(DiagnosticSection.Transport, new DiagnosticRow(
            Strings.Get("TabletTransportLabel"),
            Strings.Get("TabletTransportUnpublished"),
            PanelStatusLevel.Neutral,
            null,
            Strings.Get("TabletTransportHint")));
    }

    private void AddDisplayRows(PanelDriverSnapshot snapshot)
    {
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsDriverInterface"),
            snapshot.InterfacePath ?? Strings.Get("NotFoundFeminineLower"),
            snapshot.InterfacePath is null
                ? PanelStatusLevel.Bad
                : PanelStatusLevel.Good,
            snapshot.InterfacePath));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsInterfaceGuid"),
            PanelDisplayDriverController.DeviceInterfaceGuid.ToString("B").ToUpperInvariant()));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsDriverProtocol"),
            snapshot.IsAvailable
                ? $"v{snapshot.ProtocolVersion}"
                : Strings.Get("NotAvailableSymbol"),
            snapshot.IsAvailable &&
            snapshot.ProtocolVersion ==
                PanelDisplayDriverController.ExpectedProtocolVersion
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsExpectedProtocol"),
            $"v{PanelDisplayDriverController.ExpectedProtocolVersion}"));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsDriverLifecycle"),
            snapshot.Availability switch
            {
                PanelDriverAvailability.Available => snapshot.Lifecycle.ToString(),
                PanelDriverAvailability.NotInstalled => Strings.Get("NotInstalledLower"),
                PanelDriverAvailability.AccessDenied => Strings.Get("AccessDeniedLower"),
                PanelDriverAvailability.Error => Strings.Get("QueryFailedLower"),
                _ => Strings.Get("NotAvailableSymbol"),
            },
            snapshot.IsReady ? PanelStatusLevel.Good : PanelStatusLevel.Warning));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsVirtualMonitor"),
            snapshot.Monitor switch
            {
                PanelMonitorConnection.Connected => Strings.Get("ConnectedLower"),
                PanelMonitorConnection.Disconnected => Strings.Get("DisconnectedLower"),
                _ => Strings.Get("NotAvailableSymbol"),
            },
            snapshot.IsMonitorConnected
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsActiveMode"),
            snapshot.ActiveMode.ToString()));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsAdvertisedModes"),
            snapshot.SupportedModes.Count == 0
                ? Strings.Get("NotAvailableSymbol")
                : string.Join(", ", snapshot.SupportedModes)));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsConnectionsSinceLoad"),
            snapshot.IsAvailable
                ? snapshot.ConnectCount.ToString()
                : Strings.Get("NotAvailableSymbol")));
        Add(DiagnosticSection.Display, new DiagnosticRow(
            Strings.Get("DiagnosticsHostDisplay"),
            DescribeHostDisplay(_runtimeSnapshot?.Display),
            _runtimeSnapshot?.Display.Connected == true
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));

        if (snapshot.Detail is not null)
        {
            Add(DiagnosticSection.Display, new DiagnosticRow(
                Strings.Get("DiagnosticsDetail"),
                snapshot.Detail,
                PanelStatusLevel.Warning,
                snapshot.Detail));
        }
    }

    /// <summary>
    /// Everything the agent measured about the pipeline. A metric it did not
    /// report is an em dash; none of them is defaulted to zero, because a zero
    /// reads as "measured, and it is stalled".
    /// </summary>
    private void AddVideoRows()
    {
        var video = _runtimeSnapshot?.Video;
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("DiagnosticsNegotiatedVideo"),
            video is { Connected: true }
                ? Strings.Format(
                    "DiagnosticsNegotiatedVideoFormat",
                    video.Codec ?? Strings.Get("NotAvailableSymbol"),
                    video.Width,
                    video.Height,
                    video.NegotiatedFps)
                : _runtimeSnapshot is null
                    ? Strings.Get("NotAvailableSymbol")
                    : Strings.Get("DisconnectedLower"),
            video?.Connected == true
                ? PanelStatusLevel.Good
                : PanelStatusLevel.Neutral));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoEncoderLabel"),
            video is { Connected: true, Encoder: { Length: > 0 } encoder }
                ? encoder
                : Strings.Get("NotAvailableSymbol")));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoConfiguredFpsLabel"),
            FormatWholeFps(video is { Connected: true } ? video.ConfiguredFps : null)));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoCaptureFpsLabel"),
            FormatFps(video is { Connected: true } ? video.DriverCaptureFps : null)));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoReceiveFpsLabel"),
            FormatFps(video is { Connected: true } ? video.HostReceiveFps : null)));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoSendFpsLabel"),
            FormatFps(video is { Connected: true } ? video.SendFps : null)));

        // The tablet measures what it draws; Management v1 does not carry it
        // back, so it stays absent instead of borrowing the send rate.
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoRenderFpsLabel"),
            Strings.Get("NotAvailableSymbol"),
            PanelStatusLevel.Neutral,
            null,
            Strings.Get("VideoRenderFpsUnpublished")));
        Add(DiagnosticSection.Video, new DiagnosticRow(
            Strings.Get("VideoDropsLabel"),
            video is { Connected: true, SequenceGap: { } gap }
                ? Strings.Format("VideoDropsFormat", gap)
                : Strings.Get("NotAvailableSymbol"),
            video is { Connected: true, SequenceGap: > 0 }
                ? PanelStatusLevel.Warning
                : PanelStatusLevel.Neutral,
            null,
            video is { Connected: true, SequenceGap: > 0 }
                ? Strings.Get("ProblemVideoGapDetail")
                : null));
    }

    private void AddStreamDeckRows() =>
        Add(DiagnosticSection.StreamDeck, new DiagnosticRow(
            Strings.Get("DiagnosticsPublishedStreamDeck"),
            _runtimeSnapshot?.StreamDeck is { } deck
                ? string.IsNullOrWhiteSpace(deck.ActiveProfileId)
                    ? Strings.Get("StreamDeckNoProfile")
                    : Strings.Format(
                        "StreamDeckRevision",
                        deck.ActiveProfileName ?? deck.ActiveProfileId,
                        deck.PublishedRevision ?? Strings.Get("NotAvailableSymbol"))
                : Strings.Get("NotAvailableSymbol"),
            PanelStatusLevel.Neutral,
            _runtimeSnapshot?.StreamDeck.ActiveProfileId));

    private void AddFileRows()
    {
        Add(DiagnosticSection.Files, new DiagnosticRow(
            Strings.Get("DiagnosticsStreamDeckConfiguration"),
            _configurationStore.Location,
            PanelStatusLevel.Neutral,
            _configurationStore.Location));
        Add(DiagnosticSection.Files, new DiagnosticRow(
            Strings.Get("DiagnosticsHostLogs"),
            LogDirectory,
            PanelStatusLevel.Neutral,
            LogDirectory));
    }

    private void Add(DiagnosticSection section, DiagnosticRow row)
    {
        Rows.Add(row);
        _sectionedRows.Add((section, row));
    }

    /// <summary>
    /// Rebuilds the segments from the rows that were measured. An empty section
    /// is left out entirely rather than shown as a heading over nothing, and
    /// the log segment is always present because it always has something to
    /// say - even if that is "nothing captured yet".
    /// </summary>
    private void RebuildGroups()
    {
        var previous = _selectedGroup?.Section;
        Groups.Clear();

        foreach (var section in (DiagnosticSection[])
            [
                DiagnosticSection.Summary,
                DiagnosticSection.Host,
                DiagnosticSection.Transport,
                DiagnosticSection.Display,
                DiagnosticSection.Video,
                DiagnosticSection.StreamDeck,
                DiagnosticSection.Files,
            ])
        {
            var group = new DiagnosticGroup(section, TitleOf(section));
            foreach (var (rowSection, row) in _sectionedRows)
            {
                if (rowSection == section)
                {
                    group.Rows.Add(row);
                }
            }

            // An empty section is left out rather than shown as a heading over
            // nothing. The summary is the exception: it is where the page
            // opens, so it exists from the first frame and says that nothing
            // has been measured yet.
            if (group.Rows.Count > 0 || section == DiagnosticSection.Summary)
            {
                Groups.Add(group);
            }
        }

        Groups.Add(new DiagnosticGroup(
            DiagnosticSection.Logs,
            TitleOf(DiagnosticSection.Logs),
            isLogs: true));

        // A refresh must not send the operator back to the first segment. On
        // the very first build no rows have been measured yet, so the log is
        // the only group there is - and landing on it would make the log the
        // page's front door, which is exactly what this layout is avoiding.
        SelectedGroup =
            Groups.FirstOrDefault(group => group.Section == previous) ??
            Groups.FirstOrDefault(group => !group.IsLogs) ??
            Groups[0];
    }

    /// <summary>
    /// The display as the agent publishes it: its state, then the mode when it
    /// reported one. A missing width is not a zero-sized display, so the mode
    /// is simply left out.
    /// </summary>
    private static string DescribeHostDisplay(DisplayManagementSnapshot? display)
    {
        if (display is null)
        {
            return Strings.Get("NotAvailableSymbol");
        }

        if (display is not { Width: { } width, Height: { } height })
        {
            return display.State;
        }

        var mode = Strings.Format(
            "DiagnosticsHostDisplayFormat",
            display.State,
            width,
            height);
        return display.RefreshRate is { } hz
            ? Strings.Format("DiagnosticsHostDisplayRefreshFormat", mode, hz)
            : mode;
    }

    private static string TitleOf(DiagnosticSection section) => section switch
    {
        DiagnosticSection.Summary => Strings.Get("DiagnosticsTabSummary"),
        DiagnosticSection.Host => Strings.Get("DiagnosticsTabHost"),
        DiagnosticSection.Transport => Strings.Get("DiagnosticsTabTransport"),
        DiagnosticSection.Display => Strings.Get("DiagnosticsTabDisplay"),
        DiagnosticSection.Video => Strings.Get("DiagnosticsTabVideo"),
        DiagnosticSection.StreamDeck => Strings.Get("DiagnosticsTabStreamDeck"),
        DiagnosticSection.Files => Strings.Get("DiagnosticsTabFiles"),
        _ => Strings.Get("DiagnosticsTabLogs"),
    };

    // ==================================================================
    // Runtime metrics
    // ==================================================================

    private void OnManagementSnapshotChanged(
        object? sender,
        PanelHostManagementSnapshotChangedEventArgs e) =>
        _uiScheduler.Post(() =>
        {
            _runtimeSnapshot = e.Snapshot;
            PublishRuntimeMetrics();
        });

    private void OnManagementConnectionChanged(
        object? sender,
        PanelHostManagementConnectionChangedEventArgs e) =>
        _uiScheduler.Post(PublishRuntimeMetrics);

    private void PublishRuntimeMetrics()
    {
        RuntimeMetrics.Clear();
        var video = _runtimeSnapshot?.Video;
        if (video is null)
        {
            var unavailable = Strings.Get("RuntimeUnavailable");
            var empty = Strings.Get("NotAvailableSymbol");
            RuntimeMetrics.Add(new(Strings.Get("RuntimeDriverCaptureFps"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeHostReceiveFps"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeSendFps"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeBandwidth"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeEncodeLatency"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeCaptureToSend"), empty, unavailable));
            RuntimeMetrics.Add(new(Strings.Get("RuntimeSequenceGap"), empty, unavailable));
            return;
        }

        var sessionDetail = video.Connected
            ? Strings.Format(
                "VideoSessionDetail",
                video.Codec ?? Strings.Get("CodecUnknown"),
                video.ConfiguredFps,
                video.NegotiatedFps)
            : Strings.Get("VideoNoSession");
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeDriverCaptureFps"),
            Format(video.DriverCaptureFps, "0.0", " FPS"),
            sessionDetail));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeHostReceiveFps"),
            Format(video.HostReceiveFps, "0.0", " FPS"),
            sessionDetail));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeSendFps"),
            Format(video.SendFps, "0.0", " FPS"),
            sessionDetail));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeBandwidth"),
            Format(video.BandwidthMegabytesPerSecond, "0.00", " MB/s"),
            Strings.Get("VideoBytesSentDetail")));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeEncodeLatency"),
            Format(video.EncodeLatencyMilliseconds, "0.0", " ms"),
            video.Encoder ?? Strings.Get("VideoEncoderUnknown")));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeCaptureToSend"),
            Format(video.CaptureToSendMilliseconds, "0.0", " ms"),
            Strings.Get("VideoCaptureToSendDetail")));
        RuntimeMetrics.Add(new(
            Strings.Get("RuntimeSequenceGap"),
            video.SequenceGap?.ToString() ?? Strings.Get("NotAvailableSymbol"),
            Strings.Get("VideoSequenceGapDetail"),
            video.SequenceGap > 0 ? PanelStatusLevel.Warning : PanelStatusLevel.Neutral));
    }

    private static string Format(double? value, string format, string suffix) =>
        value is { } measured
            ? measured.ToString(format) + suffix
            : Strings.Get("NotAvailableSymbol");

    private static string FormatFps(double? value) =>
        value is { } measured
            ? Strings.Format("FpsFormat", measured)
            : Strings.Get("NotAvailableSymbol");

    private static string FormatWholeFps(int? value) =>
        value is { } measured
            ? Strings.Format("FpsWholeFormat", measured)
            : Strings.Get("NotAvailableSymbol");

    // ==================================================================
    // Log
    // ==================================================================

    private void CopyLog()
    {
        if (_clipboard is null)
        {
            return;
        }

        // What is on screen is what gets copied: with a filter active, the
        // lines the operator is looking at are the ones worth pasting.
        _clipboard.SetText(string.Join(
            Environment.NewLine,
            VisibleLogEntries.Select(entry => entry.Format())));
    }

    private void CopyRow(DiagnosticRow? row)
    {
        if (_clipboard is null || row is null)
        {
            return;
        }

        _clipboard.SetText(row.Technical ?? row.Value);
    }

    private void ClearLogView()
    {
        // Only the view: the manager keeps recording whatever comes next.
        LogEntries.Clear();
        RebuildVisibleLog();
    }

    private void OnLogAppended(object? sender, PanelHostLogAppendedEventArgs e) =>
        _uiScheduler.Post(() =>
        {
            LogEntries.Add(e.Entry);
            while (LogEntries.Count > MaximumVisibleLogEntries)
            {
                LogEntries.RemoveAt(0);
            }

            // Append instead of rebuilding: a busy agent would otherwise redraw
            // the whole list on every line.
            if (Matches(e.Entry))
            {
                VisibleLogEntries.Add(e.Entry);
            }

            while (VisibleLogEntries.Count > MaximumVisibleLogEntries)
            {
                VisibleLogEntries.RemoveAt(0);
            }

            OnLogCountChanged();
        });

    private void RebuildVisibleLog()
    {
        VisibleLogEntries.Clear();
        foreach (var entry in LogEntries.Where(Matches))
        {
            VisibleLogEntries.Add(entry);
        }

        OnLogCountChanged();
    }

    private bool Matches(PanelHostLogEntry entry) =>
        (_logStream.Stream is not { } stream || entry.Stream == stream) &&
        (string.IsNullOrWhiteSpace(_logFilter) ||
            entry.Format().Contains(_logFilter, StringComparison.OrdinalIgnoreCase));

    private void OnLogCountChanged()
    {
        OnPropertyChanged(nameof(HasLogEntries));
        OnPropertyChanged(nameof(HasVisibleLogEntries));
        OnPropertyChanged(nameof(IsLogFiltered));
        OnPropertyChanged(nameof(LogCountText));
        CopyLogCommand.RaiseCanExecuteChanged();
        ClearLogViewCommand.RaiseCanExecuteChanged();
    }
}
