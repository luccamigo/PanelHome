using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Development;
using PanelOS.Windows.Host;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// Only switches that actually do something are exposed. When one cannot work
/// on this machine it is shown disabled with the reason instead of pretending.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly IControlCenterSettingsStore _store;
    private readonly IStartupRegistration _controlCenterStartup;
    private readonly IPanelHostProcessManager _panelHost;
    private readonly IAdbDevelopmentTransport? _adb;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private ControlCenterSettings _settings = new();
    private bool _startControlCenterWithWindows;
    private bool _minimizeToTray;
    private bool _startPanelHostWithControlCenter;
    private bool _stopPanelHostOnExit;
    private bool _restartPanelHostAfterCrash;
    private bool _developmentMode;
    private PanelHostTransportOption _transport = PanelHostTransportOption.Options[0];
    private string? _statusMessage;
    private string _adbSummary = Strings.Get("SettingsAdbNotProbed");
    private string? _adbDetail;
    private PanelStatusLevel _adbLevel = PanelStatusLevel.Neutral;
    private bool _suppressPersist;

    public SettingsViewModel(
        IControlCenterSettingsStore store,
        IStartupRegistration controlCenterStartup,
        IPanelHostProcessManager panelHost,
        IAdbDevelopmentTransport? adb = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(controlCenterStartup);
        ArgumentNullException.ThrowIfNull(panelHost);
        _store = store;
        _controlCenterStartup = controlCenterStartup;
        _panelHost = panelHost;
        _adb = adb;

        ProbeAdbCommand = new AsyncRelayCommand(
            ProbeAdbAsync,
            () => _adb is not null && DevelopmentMode,
            ReportFailure);
        ConfigureUsbTransportCommand = new AsyncRelayCommand(
            ConfigureUsbTransportAsync,
            () => _adb is not null && DevelopmentMode,
            ReportFailure);
    }

    /// <summary>Raised when a preference that the shell acts on changes.</summary>
    public event EventHandler? SettingsChanged;

    public AsyncRelayCommand ProbeAdbCommand { get; }

    public AsyncRelayCommand ConfigureUsbTransportCommand { get; }

    public bool IsControlCenterStartupSupported => _controlCenterStartup.IsSupported;

    public string? ControlCenterStartupUnsupportedReason =>
        _controlCenterStartup.UnsupportedReason;

    public string SettingsLocation => _store.Location;

    /// <summary>Where PanelHost.exe was found, for the agent section.</summary>
    public string PanelHostExecutableText
    {
        get
        {
            var executable = _panelHost.LocateExecutable();
            return executable is null
                ? Strings.Get("PanelHostExecutableNotFound")
                : Strings.Format(
                    "PathWithSourceFormat",
                    executable.Path,
                    executable.Source);
        }
    }

    public bool StartControlCenterWithWindows
    {
        get => _startControlCenterWithWindows;
        set
        {
            if (!SetProperty(ref _startControlCenterWithWindows, value) ||
                _suppressPersist)
            {
                return;
            }

            ApplyStartup(value);
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!SetProperty(ref _minimizeToTray, value) || _suppressPersist)
            {
                return;
            }

            _settings.MinimizeToTray = value;
            Persist();
        }
    }

    public bool StartPanelHostWithControlCenter
    {
        get => _startPanelHostWithControlCenter;
        set
        {
            if (!SetProperty(ref _startPanelHostWithControlCenter, value) ||
                _suppressPersist)
            {
                return;
            }

            _settings.StartPanelHostWithControlCenter = value;
            Persist();
        }
    }

    public bool StopPanelHostOnExit
    {
        get => _stopPanelHostOnExit;
        set
        {
            if (!SetProperty(ref _stopPanelHostOnExit, value) || _suppressPersist)
            {
                return;
            }

            _settings.StopPanelHostOnExit = value;
            Persist();
        }
    }

    public bool RestartPanelHostAfterCrash
    {
        get => _restartPanelHostAfterCrash;
        set
        {
            if (!SetProperty(ref _restartPanelHostAfterCrash, value) ||
                _suppressPersist)
            {
                return;
            }

            _settings.RestartPanelHostAfterCrash = value;
            ApplyRestartPolicy();
            Persist();
        }
    }

    /// <summary>
    /// Carrier the agent is launched with. It is a launch argument, so a change
    /// only reaches a running agent through a restart - which the page says.
    /// </summary>
    public IReadOnlyList<PanelHostTransportOption> TransportOptions =>
        PanelHostTransportOption.Options;

    public PanelHostTransportOption PanelHostTransport
    {
        get => _transport;
        set
        {
            if (value is null || !SetProperty(ref _transport, value))
            {
                return;
            }

            ApplyTransport();
            if (_suppressPersist)
            {
                return;
            }

            _settings.PanelHostTransport = value.Value.ToCommandLineValue();
            Persist();
        }
    }

    /// <summary>
    /// Shows the ADB tools. ADB is the development transport only, so it stays
    /// out of the way of anyone just using PanelOS.
    /// </summary>
    public bool DevelopmentMode
    {
        get => _developmentMode;
        set
        {
            if (!SetProperty(ref _developmentMode, value))
            {
                return;
            }

            ProbeAdbCommand.RaiseCanExecuteChanged();
            ConfigureUsbTransportCommand.RaiseCanExecuteChanged();
            if (_suppressPersist)
            {
                return;
            }

            _settings.DevelopmentMode = value;
            Persist();
        }
    }

    public bool IsAdbAvailable => _adb is not null;

    public string AdbSummary
    {
        get => _adbSummary;
        private set => SetProperty(ref _adbSummary, value);
    }

    public string? AdbDetail
    {
        get => _adbDetail;
        private set => SetProperty(ref _adbDetail, value);
    }

    public PanelStatusLevel AdbLevel
    {
        get => _adbLevel;
        private set => SetProperty(ref _adbLevel, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        // Loading reflects existing state; it must not write it back.
        _suppressPersist = true;
        try
        {
            MinimizeToTray = _settings.MinimizeToTray;
            StartPanelHostWithControlCenter =
                _settings.StartPanelHostWithControlCenter;
            StopPanelHostOnExit = _settings.StopPanelHostOnExit;
            RestartPanelHostAfterCrash = _settings.RestartPanelHostAfterCrash;
            DevelopmentMode = _settings.DevelopmentMode;
            PanelHostTransport = PanelHostTransportOption.For(
                PanelHostTransportExtensions.ParseTransport(
                    _settings.PanelHostTransport));
            StartControlCenterWithWindows = _controlCenterStartup.IsSupported &&
                _controlCenterStartup.IsEnabled();
        }
        finally
        {
            _suppressPersist = false;
        }

        ApplyRestartPolicy();
        ApplyTransport();
        OnPropertyChanged(nameof(PanelHostExecutableText));
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            await _store.SaveAsync(_settings, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task ProbeAdbAsync(CancellationToken cancellationToken)
    {
        if (_adb is null)
        {
            return;
        }

        AdbSummary = Strings.Get("SettingsAdbProbing");
        AdbLevel = PanelStatusLevel.Neutral;
        var status = await _adb.ProbeAsync(cancellationToken).ConfigureAwait(true);
        ApplyAdbStatus(status);
    }

    private async Task ConfigureUsbTransportAsync(CancellationToken cancellationToken)
    {
        if (_adb is null)
        {
            return;
        }

        AdbSummary = Strings.Get("SettingsAdbConfiguring");
        AdbLevel = PanelStatusLevel.Neutral;

        // Reverse forwarding only reaches an agent listening on ADB/TCP, so the
        // restart below has to switch the carrier as well as mint the token.
        _panelHost.Transport = PanelOS.Windows.Host.PanelHostTransport.AdbTcp;
        var result = await _adb.ConfigureReverseAsync(cancellationToken)
            .ConfigureAwait(true);
        PanelHostOperationResult? hostResult = null;
        if (result.Success)
        {
            hostResult = _panelHost.Status.IsRunning
                ? await _panelHost.RestartAsync(cancellationToken).ConfigureAwait(true)
                : await _panelHost.StartAsync(cancellationToken).ConfigureAwait(true);
        }
        var success = result.Success && hostResult is { Success: true };
        AdbSummary = result.Success
            ? success
                ? Strings.Format(
                    "SettingsDevelopmentActiveFormat",
                    string.Join(" e ", result.Ports))
                : Strings.Get("SettingsDevelopmentHostFailed")
            : Strings.Get("UsbReverseFailed");
        AdbDetail = hostResult is { Success: false }
            ? $"{result.Message} {hostResult.Message}"
            : result.Message;
        AdbLevel = success ? PanelStatusLevel.Good : PanelStatusLevel.Bad;
    }

    private void ApplyAdbStatus(AdbStatus status)
    {
        AdbSummary = status switch
        {
            { AdbFound: false } => Strings.Get("SettingsAdbNotFound"),
            { Device: AdbDeviceState.Ready } =>
                Strings.Format("SettingsAdbDeviceReadyFormat", status.DeviceSerial),
            { Device: AdbDeviceState.Unauthorized } =>
                Strings.Get("SettingsAdbUnauthorized"),
            { Device: AdbDeviceState.Offline } => Strings.Get("SettingsAdbOffline"),
            { Device: AdbDeviceState.MultipleDevices } =>
                Strings.Get("SettingsAdbMultiple"),
            { Device: AdbDeviceState.NoDevice } => Strings.Get("SettingsAdbNoDevice"),
            _ => Strings.Get("SettingsAdbUnknown"),
        };
        AdbDetail = status.AdbPath is null
            ? status.Detail
            : status.Detail is null
                ? status.AdbPath
                : Strings.Format(
                    "SettingsAdbDetailFormat",
                    status.AdbPath,
                    status.Detail);
        AdbLevel = status switch
        {
            { IsReady: true } => PanelStatusLevel.Good,
            { AdbFound: false } => PanelStatusLevel.Bad,
            _ => PanelStatusLevel.Warning,
        };
    }

    private void ApplyTransport() => _panelHost.Transport = _transport.Value;

    private void ApplyRestartPolicy() =>
        _panelHost.RestartPolicy = _restartPanelHostAfterCrash
            ? PanelHostRestartPolicy.Default
            : PanelHostRestartPolicy.Disabled;

    private void ApplyStartup(bool enabled)
    {
        try
        {
            _controlCenterStartup.SetEnabled(enabled);
            StatusMessage = enabled
                ? Strings.Get("StartupEnabled")
                : Strings.Get("StartupDisabled");
        }
        catch (Exception exception)
        {
            StatusMessage = Strings.Format("StartupChangeFailed", exception.Message);

            // Snap the switch back to what the registry actually says.
            _suppressPersist = true;
            try
            {
                StartControlCenterWithWindows = _controlCenterStartup.IsSupported &&
                    _controlCenterStartup.IsEnabled();
            }
            finally
            {
                _suppressPersist = false;
            }
        }
    }

    private void Persist()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        _ = PersistAsync();
    }

    private async Task PersistAsync()
    {
        try
        {
            await SaveAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = Strings.Format("PreferencesSaveFailed", exception.Message);
        }
    }

    private void ReportFailure(Exception exception)
    {
        AdbSummary = Strings.Get("AdbProbeFailed");
        AdbDetail = $"{exception.GetType().Name}: {exception.Message}";
        AdbLevel = PanelStatusLevel.Bad;
    }
}
