using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.Tray;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Development;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Icons;
using PanelOS.Windows.StreamDeck.Runtime;
using PanelOS.Windows.Wifi;

namespace PanelOS.ControlCenter;

/// <summary>
/// Composition root. Everything the Control Center needs is constructed here;
/// there is no container and no service locator.
/// </summary>
public partial class App : Application
{
    /// <summary>Run value this application used to create for the agent.</summary>
    private const string LegacyPanelHostRunValue = "PanelOS PanelHost";

    private static readonly TimeSpan ExitStopTimeout = TimeSpan.FromSeconds(6);

    private readonly CancellationTokenSource _shutdown = new();
    private MainViewModel? _mainViewModel;
    private WifiPairingViewModel? _wifi;
    private PanelHostProcessManager? _panelHost;
    private PanelHostManagementClient? _managementClient;
    private PanelHostFileLog? _fileLog;
    private TrayIconController? _tray;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var settingsStore = new JsonControlCenterSettingsStore();
        var settings = LoadSettings(settingsStore);

        // Captured on the dispatcher thread, so background events - a PanelHost
        // that exits on its own, a log line - reach the view models safely.
        var uiScheduler = new SynchronizationContextUiScheduler();

        var configurationStore = new JsonStreamDeckConfigurationStore();
        var iconCache = new StreamDeckIconCache();

        var logBuffer = new PanelHostLogBuffer();
        if (settings.WritePanelHostLogsToDisk)
        {
            _fileLog = new PanelHostFileLog();
            logBuffer.Appended += (_, args) => _fileLog.Write(args.Entry);
        }

        var developmentSession = new DevelopmentSessionToken();
        _panelHost = new PanelHostProcessManager(
            new PanelHostExecutableLocator(() => settings.PanelHostExecutablePath),
            new WindowsPanelHostProcessController(),
            logBuffer,
            developmentSessionToken: developmentSession)
        {
            RestartPolicy = settings.RestartPanelHostAfterCrash
                ? PanelHostRestartPolicy.Default
                : PanelHostRestartPolicy.Disabled,

            // Applied here rather than from the Settings view model: the agent
            // can be started before the window has finished loading, and a
            // launch on the wrong carrier is not something a later correction
            // can fix without restarting it.
            Transport = PanelHostTransportExtensions.ParseTransport(
                settings.PanelHostTransport),
        };

        // Development-only transport. Nothing outside the Settings screen -
        // and only with Development Mode on - touches it.
        var adb = new AdbDevelopmentTransport(
            new AdbLocator(() => settings.AdbExecutablePath),
            sessionToken: developmentSession);

        var actionExecutor = new WindowsStreamDeckActionExecutor(
            configurationStore,
            new SendInputKeyboardService(),
            new WindowsProcessLauncher(),
            log: new TraceStreamDeckLog());

        _managementClient = new PanelHostManagementClient();
        var driver = new PanelHostManagementDriverController(
            _managementClient,
            _managementClient);
        var device = new DashboardViewModel(
            driver,
            _panelHost,
            uiScheduler,
            _managementClient);
        var overview = new OverviewViewModel(device);
        var streamDeck = new StreamDeckEditorViewModel(
            configurationStore,
            new StreamDeckEditorServices
            {
                ApplicationPicker = new ApplicationPickerService(),
                IconPicker = new IconFilePicker(),
                IconImporter = new IconImporter(iconCache),
                Confirmation = new ConfirmationService(),
                ActionExecutor = actionExecutor,
            });
        var diagnostics = new DiagnosticsViewModel(
            driver,
            configurationStore,
            _panelHost,
            new ClipboardService(),
            uiScheduler,
            _managementClient);
        var settingsViewModel = new SettingsViewModel(
            settingsStore,
            CreateControlCenterStartup(),
            _panelHost,
            adb);

        // Wi-Fi pairing talks to the process that owns the WiFi identity through a user-only pipe. With no such
        // process (the default) the page just reports that Wi-Fi is off.
        _wifi = new WifiPairingViewModel(new WifiAdminClient(), new QrCoderEncoder(), new ConfirmationService());
        var integrations = new IntegrationsViewModel(
            new PanelOS.Windows.Management.LocalIntegrationAdminClient(),
            new PanelOS.Windows.Management.ClaudeHookInstaller(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json"),
                Path.Combine(AppContext.BaseDirectory, "PanelOS.ClaudeHook.exe")),
            new ClipboardService(), new ConfirmationService());

        RetireLegacyPanelHostStartupEntry(logBuffer);

        _mainViewModel = new MainViewModel(
            overview,
            device,
            streamDeck,
            diagnostics,
            settingsViewModel,
            new LoopMediaViewModel(
                new PanelOS.Windows.LoopMedia.JsonLoopMediaStore(),
                new LoopMediaFilePicker()),
            _wifi,
            integrations);

        var window = new MainWindow(_mainViewModel);
        window.Closing += OnMainWindowClosing;
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        window.Show();

        _tray = new TrayIconController(
            Strings.Get("AppTitle"),
            new TrayCommands(
                Open: ShowMainWindow,
                ConnectDisplay: () => Run(device.ConnectCommand),
                DisconnectDisplay: () => Run(device.DisconnectCommand),
                StartPanelHost: () => Run(device.StartPanelHostCommand),
                RestartPanelHost: () => Run(device.RestartPanelHostCommand),
                StopPanelHost: () => Run(device.StopPanelHostCommand),
                Exit: RequestExit));
        _panelHost.StatusChanged += (_, args) =>
            uiScheduler.Post(() => _tray?.UpdateHostState(args.Status));

        // The tray offers exactly what the Device page offers, from the same
        // state, so the menu can never present an action the window refuses.
        device.SnapshotChanged += (_, _) =>
            _tray?.UpdateDisplayState(device.CanConnect, device.CanDisconnect);

        _ = _managementClient.RunAsync(_shutdown.Token);
        _ = _wifi.RunAsync(_shutdown.Token);
        _ = integrations.RunAsync(_shutdown.Token);
        _ = StartAsync(settings.StartPanelHostWithControlCenter);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        StopPanelHostIfRequested();
        _tray?.Dispose();
        _managementClient?.Dispose();
        _panelHost?.Dispose();
        _fileLog?.Dispose();
        _shutdown.Dispose();
        base.OnExit(e);
    }

    private static ControlCenterSettings LoadSettings(
        IControlCenterSettingsStore store)
    {
        try
        {
            // A few hundred bytes read once, before anything is shown; the
            // store already falls back to defaults for a missing or bad file.
            return store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return new ControlCenterSettings();
        }
    }

    /// <summary>
    /// The startup entry only exists when the process path is known, which is
    /// always true for a normal launch but not for an unusual host.
    /// </summary>
    private static IStartupRegistration CreateControlCenterStartup() =>
        new RegistryStartupRegistration(
            "PanelOS Control Center",
            Environment.ProcessPath,
            Strings.Get("StartupControlCenterPathUnknown"));

    private static void RetireLegacyPanelHostStartupEntry(PanelHostLogBuffer log)
    {
        if (RegistryStartupRegistration.RemoveValue(LegacyPanelHostRunValue))
        {
            log.Append(
                PanelHostLogStream.Manager,
                Strings.Get("StartupLegacyEntryRemoved"));
        }
    }

    private static void Run(AsyncRelayCommand command)
    {
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private async Task StartAsync(bool startPanelHost)
    {
        if (_mainViewModel is null)
        {
            return;
        }

        if (startPanelHost && _panelHost is not null)
        {
            // Never a second agent: an externally started PanelHost is detected
            // and left alone.
            if (!_panelHost.Refresh().IsRunning)
            {
                await _panelHost.StartAsync(_shutdown.Token).ConfigureAwait(true);
            }
        }

        await _mainViewModel.InitializeAsync(_shutdown.Token).ConfigureAwait(true);
    }

    private void StopPanelHostIfRequested()
    {
        if (_panelHost is null ||
            _mainViewModel?.Settings.StopPanelHostOnExit != true)
        {
            return;
        }

        var status = _panelHost.Status;
        if (!status.IsRunning || !status.OwnedByControlCenter)
        {
            // Closing the Control Center never ends an agent someone else
            // started.
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(ExitStopTimeout);
            _panelHost.StopAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Shutdown();
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested ||
            _mainViewModel?.Settings.MinimizeToTray != true ||
            sender is not Window window)
        {
            Shutdown();
            return;
        }

        // "Minimize to tray" is on: closing hides the window and the tray icon
        // stays as the way back in.
        e.Cancel = true;
        window.Hide();
        _tray?.ShowMessage(
            Strings.Get("AppTitle"),
            Strings.Get("TrayStillRunning"));
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        // A failure in one screen must not take the window down with it.
        e.Handled = true;
        MessageBox.Show(
            Strings.Format(
                "UnexpectedError",
                e.Exception.GetType().Name,
                e.Exception.Message),
            Strings.Get("AppTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
