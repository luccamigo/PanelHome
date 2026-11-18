using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PanelHost.Broker;
using PanelHost.Input;
using PanelHost.Management;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Services;
using PanelHost.Security.Wifi;
using PanelHost.Transport;
using PanelHost.Video;

namespace PanelHost.Hosting;

internal static class PanelHostApplication
{
    internal const int CommandLineErrorExitCode = 2;
    internal const int UsbProbeFailureExitCode = 3;

    internal static Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var commandLine = new PanelHostCommandLine(ExecuteAsync);
        return commandLine.InvokeAsync(
            args,
            Console.Out,
            Console.Error,
            cancellationToken);
    }

    internal static IHost BuildHost(PanelHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ApplicationName = typeof(PanelHostApplication).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(console =>
        {
            console.SingleLine = true;
            console.IncludeScopes = true;
        });
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(host =>
            host.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

        RegisterServices(builder.Services, options);
        return builder.Build();
    }

    private static async Task<int> ExecuteAsync(
        PanelHostOptions options,
        CancellationToken cancellationToken)
    {
        // Local identity actions open no socket and never touch the cable host, so they do not need the mutex.
        if (options.WifiAdmin.Enabled)
            return WifiBenchAdmin.Run(options.WifiAdmin, new WifiIdentityStore(), Console.Out, Console.Error);

        using var singleInstance = PanelHostSingleInstance.TryAcquire();
        if (!singleInstance.IsOwner)
        {
            Console.Error.WriteLine(
                "[PanelHost] Já existe uma instância do PanelHost em execução.");
            return PanelHostSingleInstance.AlreadyRunningExitCode;
        }

        ProcessCrashDiagnostics.Install(Console.Error);
        if (options.WifiBench.Enabled)
        {
            try
            {
                if (options.UsbProbe || options.RunsTouchSelfTest || options.RunsTelemetryCommand || options.TransportBenchmark)
                    throw new ArgumentException("WiFi bench cannot run with diagnostics or benchmarks.");
                await WifiBenchListener.RunAsync(options.WifiBench, Console.Out, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return CommandLineErrorExitCode; }
        }
        if (options.UsbProbe)
        {
            return await RunUsbProbeAsync(options, cancellationToken).ConfigureAwait(false);
        }
        if (options.RunsTouchSelfTest)
        {
            return await RunTouchSelfTestAsync(options, cancellationToken).ConfigureAwait(false);
        }
        if (options.RunsTelemetryCommand)
        {
            return await RunTelemetryCommandAsync(options, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var host = BuildHost(options);
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return Environment.ExitCode;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"[PanelHost] {exception.Message}");
            return CommandLineErrorExitCode;
        }
    }

    private static void RegisterServices(
        IServiceCollection services,
        PanelHostOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(_ => new PanelHostRuntimeState());
        services.AddSingleton<LocalNoticeBroker>();
        services.AddSingleton<LocalIntegrationSettingsStore>();
        services.AddSingleton(provider => new LocalIntegrationService(
            provider.GetRequiredService<LocalIntegrationSettingsStore>(), provider.GetRequiredService<LocalNoticeBroker>(),
            provider.GetRequiredService<ILogger<LocalIntegrationService>>(),
            permissions: provider.GetRequiredService<ClaudePermissionBroker>(),
            termination: provider.GetRequiredService<ProcessTerminationService>(),
            pageExists: id => id is "computer" or "processes" or "notices" ||
                provider.GetRequiredService<PanelHostStreamDeck>().Service.Snapshot.Layout?.Pages.Any(page => page.Id == id) == true));
        services.AddSingleton<ClaudePermissionBroker>();
        services.AddSingleton<IProcessTerminationTargets, WindowsProcessTerminationTargets>();
        services.AddSingleton<ProcessTerminationService>();
        services.AddHostedService<ClaudeHookPipeServer>();
        services.AddHostedService<LocalIntegrationHostedService>();
        services.AddSingleton(provider => new ServiceSupervisor(
            provider.GetRequiredService<ILogger<ServiceSupervisor>>()));

        services.AddSingleton<ITelemetryService>(provider =>
        {
            var broker = provider.GetRequiredService<PanelDisplayBrokerAccessor>().Client;
            return new SupplementalTelemetryService(
                new LibreHardwareTelemetryService(startSampling: true,
                    provider.GetRequiredService<ILogger<LibreHardwareTelemetryService>>()),
                () => broker?.IsConnected == true,
                token => broker is null ? Task.FromResult<double?>(null) : broker.ReadCpuTemperatureIfConnectedAsync(token));
        });
        services.AddSingleton<IMediaService>(provider => new WindowsMediaService(
            new LoggerTextWriter(provider.GetRequiredService<ILogger<WindowsMediaService>>())));
        services.AddSingleton<IVolumeService>(provider => new WindowsVolumeService(
            new CoreAudioDeviceProvider(),
            new LoggerTextWriter(provider.GetRequiredService<ILogger<WindowsVolumeService>>())));

        if (options.FakeDesktop)
        {
            services.AddSingleton<IDesktopService, FakeDesktopService>();
            services.AddSingleton(new PanelDisplayBrokerAccessor(null));
        }
        else
        {
            services.AddSingleton(provider => new PanelDisplayBrokerClient(
                new LoggerTextWriter(provider.GetRequiredService<ILogger<PanelDisplayBrokerClient>>())));
            services.AddSingleton<IDesktopService>(provider =>
                new PanelDisplayDriverDesktopService(
                    provider.GetRequiredService<PanelDisplayBrokerClient>(),
                    new LoggerTextWriter(provider.GetRequiredService<ILogger<PanelDisplayDriverDesktopService>>()),
                    options.VideoTargetFps));
            services.AddSingleton(provider => new PanelDisplayBrokerAccessor(
                provider.GetRequiredService<PanelDisplayBrokerClient>()));
        }

        services.AddSingleton<IFrameSource>(provider =>
        {
            if (options.VideoTestPattern || options.VideoTestPatternDynamic)
            {
                return new VideoTestPatternFrameSource(
                    options.VideoTestPatternDynamic
                        ? VideoTestPatternMode.Dynamic
                        : VideoTestPatternMode.Static);
            }
            if (options.FakeDesktop)
            {
                return new EmptyFrameSource();
            }
            return new PanelDisplayDriverFrameSource(
                provider.GetRequiredService<PanelDisplayBrokerClient>(),
                new LoggerTextWriter(provider.GetRequiredService<ILogger<PanelDisplayDriverFrameSource>>()));
        });

        services.AddSingleton(new VideoPipelineOptions(
            options.VideoCodec,
            VideoPipelineOptions.Default.H264MaximumWidth,
            VideoPipelineOptions.Default.H264MaximumHeight,
            options.VideoTargetFps,
            VideoPipelineOptions.Default.H264Bitrate,
            VideoPipelineOptions.Default.ZlibMaximumWidth,
            VideoPipelineOptions.Default.ZlibMaximumHeight,
            VideoPipelineOptions.Default.ZlibFps));
        services.AddSingleton(_ => new MediaFoundationH264EncoderFactory());
        services.AddSingleton(_ => new VideoSessionLifetime());
        services.AddSingleton<WindowsTouchInjectionService>(provider => new(
            new LoggerTextWriter(provider.GetRequiredService<ILogger<WindowsTouchInjectionService>>())));
        services.AddSingleton(provider => new LoggerStreamDeckLog(
            provider.GetRequiredService<ILogger<LoggerStreamDeckLog>>()));
        services.AddSingleton(provider => new PanelHostStreamDeck(
            provider.GetRequiredService<LoggerStreamDeckLog>()));
        services.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<LoopMediaService>>();
            void Log(string message) => logger.LogInformation("{Message}", message);
            return new LoopMediaService(new PanelOS.Windows.LoopMedia.JsonLoopMediaStore(log: Log), Log);
        });

        services.AddSingleton<IPanelTransport>(provider =>
            options.Transport == PanelHostTransportKind.Usb
                ? new NativeUsbTransport(
                    options.UsbDevicePath,
                    new LoggerTextWriter(provider.GetRequiredService<ILogger<NativeUsbTransport>>()))
                : new AdbTcpTransport(AdbTcpAuthentication.ReadTokenFromEnvironment()));
        services.AddSingleton<PanelClientSession>(provider => new(
            provider.GetRequiredService<ITelemetryService>(),
            provider.GetRequiredService<IMediaService>(),
            provider.GetRequiredService<IVolumeService>(),
            provider.GetRequiredService<IDesktopService>(),
            provider.GetRequiredService<VideoSessionLifetime>(),
            provider.GetRequiredService<WindowsTouchInjectionService>(),
            provider.GetRequiredService<PanelHostStreamDeck>().Service,
            provider.GetRequiredService<PanelHostRuntimeState>(),
            provider.GetRequiredService<ILogger<PanelClientSession>>(),
            notices: provider.GetRequiredService<LocalNoticeBroker>(),
            permissions: provider.GetRequiredService<ClaudePermissionBroker>(),
            termination: provider.GetRequiredService<ProcessTerminationService>(),
            loopMediaService: provider.GetRequiredService<LoopMediaService>()));
        services.AddSingleton<PanelHostServer>(provider => new(
            provider.GetRequiredService<PanelClientSession>(),
            provider.GetRequiredService<IPanelTransport>().Listen(PanelChannelId.Control),
            provider.GetRequiredService<ILogger<PanelHostServer>>()));
        services.AddSingleton<PanelVideoServer>(provider => new(
            provider.GetRequiredService<IDesktopService>(),
            provider.GetRequiredService<IFrameSource>(),
            new LoggerTextWriter(provider.GetRequiredService<ILogger<PanelVideoServer>>()),
            options: provider.GetRequiredService<VideoPipelineOptions>(),
            h264EncoderFactory: provider.GetRequiredService<MediaFoundationH264EncoderFactory>(),
            videoSessionLifetime: provider.GetRequiredService<VideoSessionLifetime>(),
            listener: provider.GetRequiredService<IPanelTransport>().Listen(PanelChannelId.Video),
            runtimeObserver: provider.GetRequiredService<PanelHostRuntimeState>(),
            logger: provider.GetRequiredService<ILogger<PanelVideoServer>>()));
        services.AddSingleton<PanelHostManagementServer>(provider => new(
            provider.GetRequiredService<PanelHostRuntimeState>(),
            provider.GetRequiredService<IDesktopService>(),
            integrations: provider.GetRequiredService<LocalIntegrationService>()));
        services.AddHostedService<PanelHostRuntimeHostedService>();
    }

    private static async Task<int> RunUsbProbeAsync(
        PanelHostOptions options,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var stream = await WindowsUsbAccessory.OpenAsync(
                options.UsbDevicePath,
                Console.Out,
                timeout.Token).ConfigureAwait(false);
            Console.WriteLine("[USB] Probe succeeded: AOA FF/FF/00 bulk IN/OUT interface is usable.");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            Console.Error.WriteLine($"[USB] Probe failed: {exception.Message}");
            return UsbProbeFailureExitCode;
        }
    }

    private static async Task<int> RunTouchSelfTestAsync(
        PanelHostOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await TouchSelfTest.RunAsync(
                options.TouchSelfTestPrimary,
                Console.Out,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }

    private static async Task<int> RunTelemetryCommandAsync(
        PanelHostOptions options,
        CancellationToken cancellationToken)
    {
        using var loggerFactory = LoggerFactory.Create(logging =>
            logging.AddSimpleConsole(console => console.SingleLine = true));
        await using var telemetry = new LibreHardwareTelemetryService(
            startSampling: false,
            logger: loggerFactory.CreateLogger<LibreHardwareTelemetryService>());
        try
        {
            await telemetry.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (options.TelemetryDebugCpu)
            {
                await telemetry.DumpCpuDiagnosticsAsync(Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await telemetry.DumpSensorsAsync(Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
    }
}

internal sealed record PanelDisplayBrokerAccessor(PanelDisplayBrokerClient? Client);
