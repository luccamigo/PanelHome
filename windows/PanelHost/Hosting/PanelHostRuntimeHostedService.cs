using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PanelHost.Management;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Services;
using PanelHost.Transport;
using PanelHost.Video;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;

namespace PanelHost.Hosting;

/// <summary>
/// Coordinates the daemon lifecycle. It groups the related listener loops and
/// applies an explicit failure policy without turning every method into a host service.
/// </summary>
internal sealed class PanelHostRuntimeHostedService : BackgroundService
{
    private readonly PanelHostOptions _options;
    private readonly ITelemetryService _telemetry;
    private readonly IMediaService _media;
    private readonly IVolumeService _volume;
    private readonly IDesktopService _desktop;
    private readonly PanelDisplayBrokerAccessor _driver;
    private readonly PanelHostStreamDeck _streamDeck;
    private readonly LoopMediaService _loopMedia;
    private readonly PanelHostRuntimeState _runtimeState;
    private readonly MediaFoundationH264EncoderFactory _h264;
    private readonly PanelHostServer _control;
    private readonly PanelVideoServer _video;
    private readonly PanelHostManagementServer _management;
    private readonly IPanelTransport _transport;
    private readonly ServiceSupervisor _supervisor;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<PanelHostRuntimeHostedService> _logger;
    private readonly LoggerTextWriter _compatibilityLog;

    public PanelHostRuntimeHostedService(
        PanelHostOptions options,
        ITelemetryService telemetry,
        IMediaService media,
        IVolumeService volume,
        IDesktopService desktop,
        PanelDisplayBrokerAccessor driver,
        PanelHostStreamDeck streamDeck,
        PanelHostRuntimeState runtimeState,
        MediaFoundationH264EncoderFactory h264,
        PanelHostServer control,
        PanelVideoServer video,
        PanelHostManagementServer management,
        IPanelTransport transport,
        ServiceSupervisor supervisor,
        IHostApplicationLifetime applicationLifetime,
        ILogger<PanelHostRuntimeHostedService> logger,
        LoopMediaService loopMedia)
    {
        _options = options;
        _telemetry = telemetry;
        _media = media;
        _volume = volume;
        _desktop = desktop;
        _driver = driver;
        _streamDeck = streamDeck;
        _loopMedia = loopMedia;
        _runtimeState = runtimeState;
        _h264 = h264;
        _control = control;
        _video = video;
        _management = management;
        _transport = transport;
        _supervisor = supervisor;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
        _compatibilityLog = new LoggerTextWriter(logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Environment.ExitCode = 1;
            _logger.LogCritical(exception, "PanelHost fatal service boundary failed");
            _applicationLifetime.StopApplication();
            throw;
        }
        finally
        {
            _desktop.StateChanged -= OnDesktopStateChanged;
            _streamDeck.Provider.SnapshotChanged -= OnStreamDeckChanged;
            var stopped = await DesktopShutdown.StopAsync(
                _desktop,
                _compatibilityLog,
                "PanelHost shutdown").ConfigureAwait(false);
            if (!stopped)
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "Desktop cleanup failed with driverState {DriverState} and errorCode {ErrorCode}",
                    _desktop.State,
                    _desktop.ErrorCode);
            }
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        LogBuildIdentity();
        await InitializeOptionalAsync(
            "telemetry",
            _telemetry,
            _telemetry.InitializeAsync,
            stoppingToken).ConfigureAwait(false);
        await InitializeOptionalAsync(
            "media",
            _media,
            _media.InitializeAsync,
            stoppingToken).ConfigureAwait(false);
        await InitializeOptionalAsync(
            "volume",
            _volume,
            _volume.InitializeAsync,
            stoppingToken).ConfigureAwait(false);

        await _streamDeck.Provider.StartAsync(stoppingToken).ConfigureAwait(false);
        await _loopMedia.StartAsync(stoppingToken).ConfigureAwait(false);
        _runtimeState.DisplayChanged(_desktop.Snapshot);
        _runtimeState.StreamDeckChanged(_streamDeck.Provider.Snapshot);
        _desktop.StateChanged += OnDesktopStateChanged;
        _streamDeck.Provider.SnapshotChanged += OnStreamDeckChanged;

        LogComposition();
        await ProbeH264Async(stoppingToken).ConfigureAwait(false);
        var benchmark = await CreateBenchmarkServerAsync(stoppingToken).ConfigureAwait(false);

        var services = new List<SupervisedService>
        {
            new("control", ServiceFailurePolicy.Fatal, _control.RunAsync),
            new("video", ServiceFailurePolicy.Fatal, _video.RunAsync),
            new("management", ServiceFailurePolicy.Fatal, _management.RunAsync),
            new(
                "transport-metrics",
                ServiceFailurePolicy.Retryable,
                token => _transport.Metrics.LogAsync(
                    _compatibilityLog,
                    _transport.Name,
                    token),
                TimeSpan.FromSeconds(1)),
        };
        if (benchmark is not null)
        {
            services.Add(new(
                "transport-benchmark",
                ServiceFailurePolicy.Fatal,
                benchmark.RunAsync));
        }

        await _supervisor.RunAsync(services, stoppingToken).ConfigureAwait(false);
    }

    private async Task InitializeOptionalAsync(
        string service,
        object instance,
        Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken)
    {
        _runtimeState.ServiceChanged(service, "starting");
        try
        {
            await initialize(cancellationToken).ConfigureAwait(false);
            if (instance is IRuntimeServiceHealth { IsAvailable: false } health)
            {
                _runtimeState.ServiceChanged(service, "degraded", health.ErrorCode);
                _logger.LogWarning(
                    "Optional service {Service} is degraded with errorCode {ErrorCode}; core services remain available",
                    service,
                    health.ErrorCode);
                return;
            }
            _runtimeState.ServiceChanged(service, "running");
            _logger.LogInformation("Optional service {Service} initialized", service);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = exception.GetType().Name;
            _runtimeState.ServiceChanged(service, "degraded", errorCode);
            _logger.LogError(
                exception,
                "Optional service {Service} failed with errorCode {ErrorCode}; core services remain available",
                service,
                errorCode);
        }
    }

    private async Task ProbeH264Async(CancellationToken cancellationToken)
    {
        if (_options.VideoCodec == VideoCodecPreference.Zlib)
        {
            return;
        }

        try
        {
            var encoder = await _h264.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (encoder is null)
            {
                _logger.LogWarning("No Media Foundation H.264 encoder is available");
            }
            else
            {
                _logger.LogInformation(
                    "H.264 encoder {Encoder} selected; hardware={Hardware}",
                    encoder.Name,
                    encoder.IsHardware);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "H.264 encoder discovery failed");
        }
    }

    private async Task<TransportBenchmarkServer?> CreateBenchmarkServerAsync(
        CancellationToken cancellationToken)
    {
        var options = _options.CreateBenchmarkOptions();
        if (options is null)
        {
            return null;
        }

        var precondition = await TransportBenchmarkPrecondition.ProbeAsync(
            _driver.Client,
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Benchmark precondition elevated={Elevated} driverAccessible={DriverAccessible} interface={DriverInterface} detail={Detail}",
            precondition.Elevated,
            precondition.DriverAccessible,
            precondition.DriverInterfacePath,
            precondition.Detail);
        return new TransportBenchmarkServer(
            _transport.Listen(PanelChannelId.Assets),
            _transport,
            _runtimeState,
            options,
            _compatibilityLog,
            precondition);
    }

    private void LogBuildIdentity()
    {
        _logger.LogInformation(
            "Build identity {HostIdentity}; build={Build}; protocol={Protocol}",
            PanelBuildIdentity.Identity,
            PanelBuildIdentity.Number,
            PanelProtocol.PROTOCOL_VERSION);
    }

    private void LogComposition()
    {
        _logger.LogInformation(
            "Transport {Transport} selected; PLMX={PlmxVersion}; Panel Protocol={ProtocolVersion}",
            _transport.Name,
            _options.Transport == PanelHostTransportKind.Usb ? PlmxFrame.Version : null,
            PanelProtocol.PROTOCOL_VERSION);
        _logger.LogInformation(
            "Video target configured with fps={Fps}",
            _options.VideoTargetFps);
        _logger.LogInformation(
            "Desktop implementation ready with driverState={DriverState}",
            _desktop.State);
        _logger.LogInformation(
            "Management listening on named pipe {PipeName}",
            PanelHostManagementProtocol.PipeName);
        if (_options.VideoTestPattern || _options.VideoTestPatternDynamic)
        {
            _logger.LogInformation(
                "Video test pattern {Pattern} selected; driver frames are ignored",
                _options.VideoTestPatternDynamic ? "dynamic" : "static");
        }
    }

    private void OnDesktopStateChanged(
        object? sender,
        DesktopStateChangedEventArgs eventArgs) =>
        _runtimeState.DisplayChanged(eventArgs.State);

    private void OnStreamDeckChanged(
        object? sender,
        StreamDeckSnapshotChangedEventArgs eventArgs) =>
        _runtimeState.StreamDeckChanged(eventArgs.Snapshot);
}
