using PanelOS.ControlCenter.Mvvm;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;
using PanelOS.Windows.Management;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// The live state of the device: the driver, the virtual display, the agent,
/// the tablet's sessions and the video pipeline, plus the commands that act on
/// them. It backs the Device page directly and the Overview page through
/// <see cref="OverviewViewModel"/>, so the two can never disagree about what
/// is happening.
/// </summary>
/// <remarks>
/// Everything here is measured: the driver through IOCTL_PANEL_GET_STATUS, the
/// agent through the process manager, and the tablet, display and video through
/// the local management IPC. A value the source did not report is reported as
/// absent - never as a zero, which would read as a measurement.
/// </remarks>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly IPanelDriverController _driver;
    private readonly IPanelHostProcessManager _panelHost;
    private readonly IUiScheduler _uiScheduler;
    private readonly IPanelHostManagementClient? _managementClient;

    private PanelDriverSnapshot _snapshot = PanelDriverSnapshot.Unknown;
    private StreamDeckSummary _streamDeck = StreamDeckSummary.Empty;
    private PanelHostSnapshot? _runtimeSnapshot;
    private string? _statusMessage;
    private string? _panelHostMessage;

    public DashboardViewModel(
        IPanelDriverController driver,
        IPanelHostProcessManager panelHost,
        IUiScheduler? uiScheduler = null,
        IPanelHostManagementClient? managementClient = null)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(panelHost);
        _driver = driver;
        _panelHost = panelHost;
        _uiScheduler = uiScheduler ?? ImmediateUiScheduler.Instance;
        _managementClient = managementClient;
        _runtimeSnapshot = managementClient?.Snapshot;

        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            onError: ReportUnexpectedFailure);
        ConnectCommand = new AsyncRelayCommand(
            ConnectAsync,
            () => CanConnect,
            ReportUnexpectedFailure);
        DisconnectCommand = new AsyncRelayCommand(
            DisconnectAsync,
            () => CanDisconnect,
            ReportUnexpectedFailure);

        StartPanelHostCommand = new AsyncRelayCommand(
            token => RunHostAsync(_panelHost.StartAsync, token),
            () => CanStartPanelHost,
            ReportUnexpectedFailure);
        StopPanelHostCommand = new AsyncRelayCommand(
            token => RunHostAsync(_panelHost.StopAsync, token),
            () => CanStopPanelHost,
            ReportUnexpectedFailure);
        RestartPanelHostCommand = new AsyncRelayCommand(
            token => RunHostAsync(_panelHost.RestartAsync, token),
            () => CanRestartPanelHost,
            ReportUnexpectedFailure);

        OpenDeviceCommand = new RelayCommand(
            () => NavigationRequested?.Invoke(this, PanelSection.Device));
        OpenStreamDeckCommand = new RelayCommand(
            () => NavigationRequested?.Invoke(this, PanelSection.StreamDeck));
        OpenLogsCommand = new RelayCommand(
            () => NavigationRequested?.Invoke(this, PanelSection.Diagnostics));

        _panelHost.StatusChanged += OnPanelHostStatusChanged;
        if (_managementClient is not null)
        {
            _managementClient.SnapshotChanged += OnManagementSnapshotChanged;
            _managementClient.ConnectionChanged += OnManagementConnectionChanged;
        }
    }

    /// <summary>Raised after every completed refresh, for the shell and Overview.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>
    /// A quick action asking the shell to open the page that owns the task,
    /// rather than a second copy of it growing here.
    /// </summary>
    public event EventHandler<PanelSection>? NavigationRequested;

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    public AsyncRelayCommand StartPanelHostCommand { get; }

    public AsyncRelayCommand StopPanelHostCommand { get; }

    public AsyncRelayCommand RestartPanelHostCommand { get; }

    public RelayCommand OpenDeviceCommand { get; }

    public RelayCommand OpenStreamDeckCommand { get; }

    public RelayCommand OpenLogsCommand { get; }

    public PanelDriverSnapshot Snapshot => _snapshot;

    /// <summary>The last snapshot the management IPC delivered, or null.</summary>
    public PanelHostSnapshot? RuntimeSnapshot => _runtimeSnapshot;

    public PanelHostStatus PanelHostStatus => _panelHost.Status;

    // ==================================================================
    // Driver
    // ==================================================================

    /// <summary>
    /// A failed Start leaves the host display in Error, which the management snapshot maps to an
    /// unavailable driver. That is a lifecycle result, not a missing driver, so the operator must
    /// still be able to try again without restarting the host.
    /// </summary>
    public bool CanConnect =>
        (_snapshot.IsAvailable || HostReportsDisplayError) && !_snapshot.IsMonitorConnected;

    private bool HostReportsDisplayError =>
        _runtimeSnapshot?.Display is { Connected: false, State: "Error" };

    public bool CanDisconnect =>
        _snapshot.IsAvailable && _snapshot.Monitor != PanelMonitorConnection.Disconnected;

    public string DriverAvailabilityText => _snapshot.Availability switch
    {
        PanelDriverAvailability.Available => Strings.Get("DriverInstalled"),
        PanelDriverAvailability.NotInstalled => Strings.Get("DriverNotFoundShort"),
        PanelDriverAvailability.AccessDenied => Strings.Get("AccessDenied"),
        PanelDriverAvailability.Error => Strings.Get("Unavailable"),
        _ => Strings.Get("Querying"),
    };

    public PanelStatusLevel DriverAvailabilityLevel => _snapshot.Availability switch
    {
        PanelDriverAvailability.Available => PanelStatusLevel.Good,
        PanelDriverAvailability.NotInstalled => PanelStatusLevel.Bad,
        PanelDriverAvailability.AccessDenied => PanelStatusLevel.Warning,
        PanelDriverAvailability.Error => PanelStatusLevel.Bad,
        _ => PanelStatusLevel.Neutral,
    };

    public string DriverStateText => !_snapshot.IsAvailable
        ? Absent
        : _snapshot.Lifecycle switch
        {
            PanelDriverLifecycle.Ready => Strings.Get("DriverStateReady"),
            PanelDriverLifecycle.Initializing => Strings.Get("DriverStateInitializing"),
            PanelDriverLifecycle.Stopped => Strings.Get("DriverStateStopped"),
            _ => Absent,
        };

    /// <summary>
    /// Protocol the installed driver reports. Absent rather than invented when
    /// the driver cannot be queried.
    /// </summary>
    public string DriverProtocolText => _snapshot.IsAvailable
        ? Strings.Format("DriverProtocolFormat", _snapshot.ProtocolVersion)
        : Absent;

    public PanelStatusLevel DriverStateLevel => !_snapshot.IsAvailable
        ? PanelStatusLevel.Neutral
        : _snapshot.Lifecycle switch
        {
            PanelDriverLifecycle.Ready => PanelStatusLevel.Good,
            PanelDriverLifecycle.Initializing => PanelStatusLevel.Warning,
            PanelDriverLifecycle.Stopped => PanelStatusLevel.Bad,
            _ => PanelStatusLevel.Neutral,
        };

    // ==================================================================
    // Virtual display
    // ==================================================================

    public string MonitorText => _runtimeSnapshot?.Display is { } display
        ? display.Connected
            ? Strings.Get("Connected")
            : DescribeDesktopState(display.State)
        : _snapshot.Monitor switch
        {
            PanelMonitorConnection.Connected => Strings.Get("Connected"),
            PanelMonitorConnection.Disconnected => Strings.Get("Disconnected"),
            _ => Absent,
        };

    public PanelStatusLevel MonitorLevel => _runtimeSnapshot?.Display is { } display
        ? display.Connected ? PanelStatusLevel.Good : PanelStatusLevel.Neutral
        : _snapshot.Monitor switch
        {
            PanelMonitorConnection.Connected => PanelStatusLevel.Good,
            _ => PanelStatusLevel.Neutral,
        };

    public bool IsMonitorConnected =>
        _runtimeSnapshot?.Display.Connected ?? _snapshot.IsMonitorConnected;

    public string ResolutionText => _runtimeSnapshot?.Display is
        { Width: { } width, Height: { } height }
        ? Strings.Format("ResolutionFormat", width, height)
        : _snapshot.ActiveMode.IsSpecified
            ? Strings.Format(
                "ResolutionFormat",
                _snapshot.ActiveMode.Width,
                _snapshot.ActiveMode.Height)
            : Absent;

    public string RefreshRateText => _runtimeSnapshot?.Display.RefreshRate is { } refresh
        ? Strings.Format("RefreshRateFormat", refresh)
        : _snapshot.ActiveMode.IsSpecified && _snapshot.ActiveMode.RefreshHz != 0
            ? Strings.Format("RefreshRateFormat", _snapshot.ActiveMode.RefreshHz)
            : Absent;

    // ==================================================================
    // PanelHost
    // ==================================================================

    public string PanelHostStateText => _runtimeSnapshot is not null
        ? Strings.Get("HostStateRunning")
        : _panelHost.Status.State switch
        {
            PanelHostProcessState.Running => Strings.Get("HostStateRunning"),
            PanelHostProcessState.Starting => Strings.Get("HostStateStarting"),
            PanelHostProcessState.Stopping => Strings.Get("HostStateStopping"),
            PanelHostProcessState.Error => Strings.Get("Error"),
            _ => Strings.Get("HostStateStopped"),
        };

    public PanelStatusLevel PanelHostLevel => _runtimeSnapshot is not null
        ? PanelStatusLevel.Good
        : _panelHost.Status.State switch
        {
            PanelHostProcessState.Running => PanelStatusLevel.Good,
            PanelHostProcessState.Starting or PanelHostProcessState.Stopping =>
                PanelStatusLevel.Warning,
            PanelHostProcessState.Error => PanelStatusLevel.Bad,
            _ => PanelStatusLevel.Neutral,
        };

    public bool IsPanelHostRunning =>
        _runtimeSnapshot is not null || _panelHost.Status.IsRunning;

    public string PanelHostProcessText =>
        (_runtimeSnapshot?.Host.ProcessId ?? _panelHost.Status.ProcessId) is { } pid
        ? _runtimeSnapshot?.Host.Version is { Length: > 0 } version
            ? Strings.Format("HostProcessVersionFormat", pid, version)
            : Strings.Format("HostProcessFormat", pid)
        : Absent;

    /// <summary>
    /// How long the agent has been up, from the start the manager recorded or
    /// the uptime the agent itself reports.
    /// </summary>
    public string PanelHostUptimeText
    {
        get
        {
            if (_runtimeSnapshot is { Host.UptimeSeconds: >= 0 } runtime)
            {
                return FormatUptime(TimeSpan.FromSeconds(runtime.Host.UptimeSeconds));
            }

            if (_panelHost.Status is not { IsRunning: true, StartedAt: { } startedAt })
            {
                return Absent;
            }

            var uptime = DateTimeOffset.Now - startedAt;
            return uptime < TimeSpan.Zero ? Absent : FormatUptime(uptime);
        }
    }

    /// <summary>
    /// Text for a transition in progress. It is what stops a second click and
    /// what tells the operator the click landed.
    /// </summary>
    public string? PanelHostBusyText => _panelHost.Status.State switch
    {
        PanelHostProcessState.Starting => Strings.Get("HostStateStarting"),
        PanelHostProcessState.Stopping => Strings.Get("HostStateStopping"),
        _ => null,
    };

    public bool IsPanelHostBusy => _panelHost.Status.IsBusy;

    /// <summary>
    /// Who owns the running agent. The Control Center only ever offers to stop
    /// one it started itself.
    /// </summary>
    public string PanelHostOwnershipText =>
        _runtimeSnapshot is not null && !_panelHost.Status.OwnedByControlCenter
            ? Strings.Get("HostOwnershipExternal")
            : _panelHost.Status switch
            {
                { IsRunning: false } => Absent,
                { OwnedByControlCenter: true } => Strings.Get("HostOwnershipManaged"),
                _ => Strings.Get("HostOwnershipExternal"),
            };

    /// <summary>True while an agent someone else started is being observed.</summary>
    public bool IsPanelHostExternal =>
        IsPanelHostRunning && !_panelHost.Status.OwnedByControlCenter;

    /// <summary>
    /// The outcome of the last operation, or whatever the manager last had to
    /// say. The management channel's own state is a row of its own, so it is
    /// deliberately not repeated here.
    /// </summary>
    public string? PanelHostDetail =>
        _panelHostMessage ?? _panelHost.Status.Detail;

    /// <summary>
    /// The last line the agent wrote to standard error, or null.
    /// </summary>
    /// <remarks>
    /// An agent that refuses to start says why on its own stderr - a missing
    /// credential, a port already taken - while the manager can only report the
    /// exit code it saw. Quoting that line verbatim is the difference between
    /// "encerrou inesperadamente (código 2)" and a message someone can act on.
    ///
    /// It is quoted, never interpreted: nothing in this application decides
    /// what to do by reading a log line.
    /// </remarks>
    public string? PanelHostLastError
    {
        get
        {
            if (!HasPanelHostError)
            {
                return null;
            }

            for (var entries = _panelHost.Log.Snapshot(); entries.Count > 0;)
            {
                for (var index = entries.Count - 1; index >= 0; index--)
                {
                    if (entries[index].Stream == PanelHostLogStream.StandardError)
                    {
                        return entries[index].Text;
                    }
                }

                break;
            }

            return null;
        }
    }

    public bool CanStartPanelHost =>
        _runtimeSnapshot is null &&
        (_panelHost.Status.State is PanelHostProcessState.Stopped
            or PanelHostProcessState.Error);

    public bool CanStopPanelHost =>
        _panelHost.Status is { IsRunning: true, OwnedByControlCenter: true };

    /// <summary>
    /// Restart is stop-then-start, so it needs the same ownership as stop. An
    /// agent someone else launched is shown, never restarted from here.
    /// </summary>
    public bool CanRestartPanelHost => CanStopPanelHost;

    // ==================================================================
    // Management IPC
    // ==================================================================

    /// <summary>
    /// Whether the local management channel is delivering snapshots. Without it
    /// the tablet, the display state and every video metric are unobservable,
    /// which the pages say rather than filling in.
    /// </summary>
    public bool IsManagementConnected => _runtimeSnapshot is not null;

    public string ManagementText => _runtimeSnapshot is { } runtime
        ? Strings.Format(
            "HostManagementConnectedFormat",
            runtime.ManagementProtocolVersion)
        : _managementClient?.ConnectionState switch
        {
            PanelHostManagementConnectionState.Connecting =>
                Strings.Get("IpcReconnecting"),
            _ => Strings.Get("Unavailable"),
        };

    public PanelStatusLevel ManagementLevel => _runtimeSnapshot is not null
        ? PanelStatusLevel.Good
        : IsPanelHostRunning
            ? PanelStatusLevel.Warning
            : PanelStatusLevel.Neutral;

    // ==================================================================
    // Tablet
    // ==================================================================

    public string TabletStatusText => _managementClient is null
        ? Strings.Get("Unavailable")
        : _runtimeSnapshot?.Tablet switch
        {
            { ControlConnected: true, VideoConnected: true } =>
                Strings.Get("TabletStateConnected"),
            { ControlConnected: true } => Strings.Get("TabletStateControlOnly"),
            { VideoConnected: true } => Strings.Get("TabletStateVideoOnly"),
            not null => Strings.Get("TabletStateDisconnected"),
            _ => Strings.Get("HostUnavailable"),
        };

    public PanelStatusLevel TabletLevel => _runtimeSnapshot?.Tablet switch
    {
        { ControlConnected: true, VideoConnected: true } => PanelStatusLevel.Good,
        { ControlConnected: true } or { VideoConnected: true } => PanelStatusLevel.Warning,
        _ => PanelStatusLevel.Neutral,
    };

    public bool IsTabletConnected => _runtimeSnapshot?.Tablet.ControlConnected == true;

    /// <summary>
    /// Whether the tablet has any session at all. With one, the rows carry the
    /// detail; without one, <see cref="TabletDetail"/> is the only thing that
    /// can explain why.
    /// </summary>
    public bool HasTabletSession =>
        _runtimeSnapshot?.Tablet is { ControlConnected: true } or { VideoConnected: true };

    public string TabletDetail
    {
        get
        {
            if (_managementClient is null)
            {
                return Strings.Get("TabletNotObservable");
            }

            if (_runtimeSnapshot is null)
            {
                return Strings.Get("TabletNoSnapshot");
            }

            var tablet = _runtimeSnapshot.Tablet;
            if (!tablet.ControlConnected && !tablet.VideoConnected)
            {
                return Strings.Get("TabletNoSession");
            }

            return Strings.Format(
                "TabletDetailFormat",
                TabletProtocolText,
                TabletFeaturesText,
                tablet.VideoConnected
                    ? Strings.Get("TabletVideoActiveLower")
                    : Strings.Get("TabletVideoInactiveLower"));
        }
    }

    public string TabletProtocolText => _runtimeSnapshot?.Tablet.ProtocolVersion is { } version
        ? Strings.Format("TabletProtocolFormat", version)
        : Strings.Get("TabletProtocolUnknown");

    public string TabletFeaturesText =>
        _runtimeSnapshot?.Tablet.NegotiatedFeatures is { Length: > 0 } features
            ? string.Join(", ", features)
            : Strings.Get("TabletNoFeatures");

    public string TabletControlText => _runtimeSnapshot?.Tablet is { } tablet
        ? tablet.ControlConnected
            ? Strings.Get("Connected")
            : Strings.Get("Disconnected")
        : Absent;

    public PanelStatusLevel TabletControlLevel =>
        _runtimeSnapshot?.Tablet.ControlConnected == true
            ? PanelStatusLevel.Good
            : PanelStatusLevel.Neutral;

    public string TabletVideoText => _runtimeSnapshot?.Tablet is { } tablet
        ? tablet.VideoConnected
            ? Strings.Get("Connected")
            : Strings.Get("Disconnected")
        : Absent;

    public PanelStatusLevel TabletVideoLevel =>
        _runtimeSnapshot?.Tablet.VideoConnected == true
            ? PanelStatusLevel.Good
            : PanelStatusLevel.Neutral;

    /// <summary>
    /// How many control and video sessions the agent has accepted. It is the
    /// only reconnect signal the snapshot carries, and a climbing number is how
    /// a flapping link shows itself.
    /// </summary>
    public string TabletSessionText => _runtimeSnapshot?.Tablet is { } tablet
        ? Strings.Format(
            "TabletSessionFormat",
            tablet.ControlSessionGeneration,
            tablet.VideoSessionGeneration)
        : Absent;

    // ==================================================================
    // Video
    // ==================================================================

    /// <summary>
    /// Whether a video session exists at all. Every metric below is absent
    /// unless this is true, because a disconnected pipeline measures nothing.
    /// </summary>
    public bool HasVideoSession => _runtimeSnapshot?.Video.Connected == true;

    public string VideoStatusText => _runtimeSnapshot is null
        ? Absent
        : HasVideoSession
            ? Strings.Get("VideoStateActive")
            : Strings.Get("VideoStateInactive");

    public PanelStatusLevel VideoLevel => HasVideoSession
        ? _runtimeSnapshot?.Video.SequenceGap > 0
            ? PanelStatusLevel.Warning
            : PanelStatusLevel.Good
        : PanelStatusLevel.Neutral;

    public string VideoCodecText =>
        _runtimeSnapshot?.Video is { Connected: true, Codec: { Length: > 0 } codec }
            ? codec
            : Absent;

    public string VideoEncoderText =>
        _runtimeSnapshot?.Video is { Connected: true, Encoder: { Length: > 0 } encoder }
            ? encoder
            : Absent;

    public string VideoResolutionText =>
        _runtimeSnapshot?.Video is { Connected: true, Width: { } width, Height: { } height }
            ? Strings.Format("ResolutionFormat", width, height)
            : Absent;

    public string VideoConfiguredFpsText => FormatWholeFps(
        _runtimeSnapshot?.Video is { Connected: true } configured
            ? configured.ConfiguredFps
            : null);

    public string VideoNegotiatedFpsText => FormatWholeFps(
        _runtimeSnapshot?.Video is { Connected: true } negotiated
            ? negotiated.NegotiatedFps
            : null);

    /// <summary>Frames per second the driver handed to the agent.</summary>
    public string VideoCaptureFpsText => FormatFps(
        _runtimeSnapshot?.Video is { Connected: true } capture
            ? capture.DriverCaptureFps
            : null);

    /// <summary>Frames per second the agent's pipeline received.</summary>
    public string VideoReceiveFpsText => FormatFps(
        _runtimeSnapshot?.Video is { Connected: true } receive
            ? receive.HostReceiveFps
            : null);

    /// <summary>Frames per second actually sent to the tablet.</summary>
    public string VideoSendFpsText => FormatFps(
        _runtimeSnapshot?.Video is { Connected: true } send ? send.SendFps : null);

    /// <summary>
    /// Always absent: what the tablet draws is measured on the tablet, and
    /// Management v1 does not carry it back. Showing the send rate here instead
    /// would be presenting one measurement as another.
    /// </summary>
    public string VideoRenderFpsText => Absent;

    public string VideoRenderFpsNote => Strings.Get("VideoRenderFpsUnpublished");

    /// <summary>Frames missing from the sequence in the last measured window.</summary>
    public string VideoDropsText =>
        _runtimeSnapshot?.Video is { Connected: true, SequenceGap: { } gap }
            ? Strings.Format("VideoDropsFormat", gap)
            : Absent;

    public PanelStatusLevel VideoDropsLevel =>
        _runtimeSnapshot?.Video is { Connected: true, SequenceGap: > 0 }
            ? PanelStatusLevel.Warning
            : PanelStatusLevel.Neutral;

    public bool HasVideoDrops =>
        _runtimeSnapshot?.Video is { Connected: true, SequenceGap: > 0 };

    public string VideoDetail => _runtimeSnapshot?.Video is { Connected: true } video
        ? Strings.Format(
            "VideoSessionDetail",
            video.Codec ?? Strings.Get("CodecUnknown"),
            video.ConfiguredFps,
            video.NegotiatedFps)
        : Strings.Get("VideoNoSession");

    // ==================================================================
    // Stream Deck
    // ==================================================================

    private bool HasRuntimeStreamDeckProfile =>
        !string.IsNullOrWhiteSpace(_runtimeSnapshot?.StreamDeck.ActiveProfileId);

    public string StreamDeckStatusText => _runtimeSnapshot is not null
        ? HasRuntimeStreamDeckProfile
            ? Strings.Get("StreamDeckStatusPublished")
            : Strings.Get("StreamDeckStatusNoProfile")
        : _streamDeck.HasProfile
            ? Strings.Get("StreamDeckStatusConfigured")
            : Strings.Get("StreamDeckStatusNoProfile");

    public PanelStatusLevel StreamDeckLevel =>
        (_runtimeSnapshot is not null ? HasRuntimeStreamDeckProfile : _streamDeck.HasProfile)
            ? PanelStatusLevel.Good
            : PanelStatusLevel.Neutral;

    public bool HasPublishedStreamDeckProfile =>
        _runtimeSnapshot is null || HasRuntimeStreamDeckProfile;

    public string StreamDeckDetail => _runtimeSnapshot?.StreamDeck is { } streamDeck
        ? HasRuntimeStreamDeckProfile
            ? Strings.Format(
                "StreamDeckRuntimeDetailFormat",
                streamDeck.ActiveProfileName ?? streamDeck.ActiveProfileId,
                Pluralize(
                    streamDeck.PageCount,
                    "StreamDeckPageSingular",
                    "StreamDeckPagePlural"),
                Pluralize(
                    streamDeck.ConfiguredButtonCount,
                    "StreamDeckButtonCountSingular",
                    "StreamDeckButtonCountPlural"))
            : Strings.Get("StreamDeckNotPublished")
        : _streamDeck.Describe();

    public string StreamDeckRevisionText => _runtimeSnapshot?.StreamDeck is { } runtimeDeck
        ? runtimeDeck.PublishedRevision is { Length: > 0 } revision
            ? Strings.Format("StreamDeckRevisionShortFormat", revision)
            : Absent
        : _streamDeck.HasProfile
            ? Strings.Format("StreamDeckRevisionShortFormat", _streamDeck.Revision)
            : Absent;

    public string ControlCenterVersion => Services.ControlCenterVersion.Current;

    /// <summary>
    /// Whether the last operation failed. It drives an inline error card: a
    /// routine failure like "executable not found" is information, not an
    /// event worth a modal dialog.
    /// </summary>
    public bool HasPanelHostError =>
        _panelHostMessage is not null ||
        _panelHost.Status.State == PanelHostProcessState.Error;

    /// <summary>
    /// Result of the last operation, or the driver's failure detail. Null when
    /// there is nothing worth saying.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RefreshCoreAsync(reportDriverDetail: true, cancellationToken);

    /// <summary>
    /// Adopts what the Stream Deck editor last persisted. This view model never
    /// reads the configuration itself, so the card and the editor can never
    /// disagree about what is saved.
    /// </summary>
    public void ApplyStreamDeckSummary(StreamDeckSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        _streamDeck = summary;
        PublishStreamDeckState();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var result = await _driver.ConnectAsync(cancellationToken)
            .ConfigureAwait(true);

        // Refresh first, then report: the command outcome is what the operator
        // asked about, so it must survive the refresh that follows it.
        await RefreshCoreAsync(reportDriverDetail: false, cancellationToken)
            .ConfigureAwait(true);
        StatusMessage = Describe(
            result,
            Strings.Get("MonitorVerbConnected"),
            Strings.Get("MonitorVerbConnect"));
    }

    private async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var result = await _driver.DisconnectAsync(cancellationToken)
            .ConfigureAwait(true);
        await RefreshCoreAsync(reportDriverDetail: false, cancellationToken)
            .ConfigureAwait(true);
        StatusMessage = Describe(
            result,
            Strings.Get("MonitorVerbDisconnected"),
            Strings.Get("MonitorVerbDisconnect"));
    }

    private async Task RunHostAsync(
        Func<CancellationToken, Task<PanelHostOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        var result = await operation(cancellationToken).ConfigureAwait(true);
        _panelHostMessage = result.Success ? null : result.Message;
        PublishPanelHostState();
    }

    private async Task RefreshCoreAsync(
        bool reportDriverDetail,
        CancellationToken cancellationToken)
    {
        var snapshot = await _driver.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(true);
        _snapshot = snapshot;
        _panelHost.Refresh();

        PublishDriverState();
        PublishPanelHostState();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);

        if (!reportDriverDetail)
        {
            return;
        }

        StatusMessage = snapshot.IsAvailable ? null : snapshot.Detail;
    }

    private static string Describe(
        PanelDriverCommandResult result,
        string doneVerb,
        string failedVerb) => result.Status switch
        {
            PanelDriverCommandStatus.Succeeded => Strings.Format("MonitorChanged", doneVerb),
            PanelDriverCommandStatus.AlreadyInDesiredState =>
                Strings.Format("MonitorAlreadyChanged", doneVerb),
            PanelDriverCommandStatus.DriverNotInstalled =>
                Strings.Get("DriverNotFound"),
            PanelDriverCommandStatus.AccessDenied =>
                Strings.Get("DriverAccessDenied"),
            PanelDriverCommandStatus.DriverNotReady =>
                Strings.Get("DriverNotReady"),
            PanelDriverCommandStatus.ElevationDeclined =>
                Strings.Get("DriverElevationDeclined"),
            _ => Strings.Format(
                "MonitorChangeFailed",
                failedVerb,
                result.Detail is null ? string.Empty : $" {result.Detail}"),
        };

    private void OnPanelHostStatusChanged(
        object? sender,
        PanelHostStatusChangedEventArgs e) =>
        _uiScheduler.Post(() =>
        {
            // A status the manager pushed replaces whatever the last command
            // said; the newer fact wins.
            _panelHostMessage = null;
            PublishPanelHostState();
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
        });

    private void OnManagementSnapshotChanged(
        object? sender,
        PanelHostManagementSnapshotChangedEventArgs e) =>
        _uiScheduler.Post(() =>
        {
            _runtimeSnapshot = e.Snapshot;
            PublishRuntimeState();
        });

    private void OnManagementConnectionChanged(
        object? sender,
        PanelHostManagementConnectionChangedEventArgs e) =>
        _uiScheduler.Post(PublishRuntimeState);

    private void PublishRuntimeState()
    {
        PublishDriverState();
        PublishPanelHostState();
        PublishTabletState();
        PublishVideoState();
        PublishStreamDeckState();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishDriverState()
    {
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(DriverAvailabilityText));
        OnPropertyChanged(nameof(DriverAvailabilityLevel));
        OnPropertyChanged(nameof(DriverStateText));
        OnPropertyChanged(nameof(DriverStateLevel));
        OnPropertyChanged(nameof(DriverProtocolText));
        OnPropertyChanged(nameof(MonitorText));
        OnPropertyChanged(nameof(MonitorLevel));
        OnPropertyChanged(nameof(IsMonitorConnected));
        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(RefreshRateText));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
    }

    private void PublishPanelHostState()
    {
        OnPropertyChanged(nameof(PanelHostStatus));
        OnPropertyChanged(nameof(RuntimeSnapshot));
        OnPropertyChanged(nameof(PanelHostStateText));
        OnPropertyChanged(nameof(PanelHostLevel));
        OnPropertyChanged(nameof(IsPanelHostRunning));
        OnPropertyChanged(nameof(PanelHostProcessText));
        OnPropertyChanged(nameof(PanelHostUptimeText));
        OnPropertyChanged(nameof(PanelHostBusyText));
        OnPropertyChanged(nameof(IsPanelHostBusy));
        OnPropertyChanged(nameof(PanelHostOwnershipText));
        OnPropertyChanged(nameof(IsPanelHostExternal));
        OnPropertyChanged(nameof(PanelHostDetail));
        OnPropertyChanged(nameof(PanelHostLastError));
        OnPropertyChanged(nameof(HasPanelHostError));
        OnPropertyChanged(nameof(CanStartPanelHost));
        OnPropertyChanged(nameof(CanStopPanelHost));
        OnPropertyChanged(nameof(CanRestartPanelHost));
        OnPropertyChanged(nameof(IsManagementConnected));
        OnPropertyChanged(nameof(ManagementText));
        OnPropertyChanged(nameof(ManagementLevel));
        StartPanelHostCommand.RaiseCanExecuteChanged();
        StopPanelHostCommand.RaiseCanExecuteChanged();
        RestartPanelHostCommand.RaiseCanExecuteChanged();
    }

    private void PublishTabletState()
    {
        OnPropertyChanged(nameof(TabletStatusText));
        OnPropertyChanged(nameof(TabletLevel));
        OnPropertyChanged(nameof(IsTabletConnected));
        OnPropertyChanged(nameof(HasTabletSession));
        OnPropertyChanged(nameof(TabletDetail));
        OnPropertyChanged(nameof(TabletProtocolText));
        OnPropertyChanged(nameof(TabletFeaturesText));
        OnPropertyChanged(nameof(TabletControlText));
        OnPropertyChanged(nameof(TabletControlLevel));
        OnPropertyChanged(nameof(TabletVideoText));
        OnPropertyChanged(nameof(TabletVideoLevel));
        OnPropertyChanged(nameof(TabletSessionText));
    }

    private void PublishVideoState()
    {
        OnPropertyChanged(nameof(HasVideoSession));
        OnPropertyChanged(nameof(VideoStatusText));
        OnPropertyChanged(nameof(VideoLevel));
        OnPropertyChanged(nameof(VideoCodecText));
        OnPropertyChanged(nameof(VideoEncoderText));
        OnPropertyChanged(nameof(VideoResolutionText));
        OnPropertyChanged(nameof(VideoConfiguredFpsText));
        OnPropertyChanged(nameof(VideoNegotiatedFpsText));
        OnPropertyChanged(nameof(VideoCaptureFpsText));
        OnPropertyChanged(nameof(VideoReceiveFpsText));
        OnPropertyChanged(nameof(VideoSendFpsText));
        OnPropertyChanged(nameof(VideoDropsText));
        OnPropertyChanged(nameof(VideoDropsLevel));
        OnPropertyChanged(nameof(HasVideoDrops));
        OnPropertyChanged(nameof(VideoDetail));
    }

    private void PublishStreamDeckState()
    {
        OnPropertyChanged(nameof(StreamDeckStatusText));
        OnPropertyChanged(nameof(StreamDeckLevel));
        OnPropertyChanged(nameof(StreamDeckDetail));
        OnPropertyChanged(nameof(StreamDeckRevisionText));
        OnPropertyChanged(nameof(HasPublishedStreamDeckProfile));
    }

    private void ReportUnexpectedFailure(Exception exception) =>
        StatusMessage = Strings.Format(
            "UnexpectedFailure",
            exception.GetType().Name,
            exception.Message);

    private static string Absent => Strings.Get("NotAvailableSymbol");

    private static string FormatFps(double? value) =>
        value is { } measured ? Strings.Format("FpsFormat", measured) : Absent;

    private static string FormatWholeFps(int? value) =>
        value is { } measured ? Strings.Format("FpsWholeFormat", measured) : Absent;

    private static string Pluralize(int count, string singular, string plural) =>
        Strings.Format(count == 1 ? singular : plural, count);

    private static string FormatUptime(TimeSpan uptime) => uptime switch
    {
        { TotalMinutes: < 1 } => Strings.Format("HostUptimeSecondsFormat", uptime.Seconds),
        { TotalHours: < 1 } =>
            Strings.Format("HostUptimeMinutesFormat", (int)uptime.TotalMinutes),
        { TotalDays: < 1 } =>
            Strings.Format("HostUptimeHoursFormat", (int)uptime.TotalHours, uptime.Minutes),
        _ => Strings.Format("HostUptimeDaysFormat", (int)uptime.TotalDays, uptime.Hours),
    };

    private static string DescribeDesktopState(string state) => state switch
    {
        "Starting" => Strings.Get("DisplayStateConnecting"),
        "Stopping" => Strings.Get("DisplayStateDisconnecting"),
        "Error" => Strings.Get("Error"),
        _ => Strings.Get("Disconnected"),
    };
}
