using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using PanelHost.Security.Wifi;
using PanelHost.Video;

namespace PanelHost.Hosting;

/// <summary>
/// Owns every public option. Parsing, defaults, validation and help all use
/// these same symbol instances.
/// </summary>
internal sealed class PanelHostCommandLine
{
    private readonly Option<string> _transport = Value(
        "--transport", "Transport carrier: adb or usb.", "adb");
    private readonly Option<bool> _wifiBench = Flag("--wifi-bench", "Enable the reviewed read-only WiFi bench session listener.");
    private readonly Option<string> _wifiBind = Value("--wifi-bind", "Selected physical Private RFC1918 IPv4 address.", "");
    private readonly Option<int> _wifiPort = Integer("--wifi-session-port", "Explicit WiFi bench session port (not ADB ports).", 0);
    private readonly Option<int> _wifiPairPort = Integer("--wifi-pair-port", "Port of the pairing listener that the Control Center opens during the window (0 = pairing off).", 0);
    private readonly Option<int> _wifiWindow = Integer("--wifi-window-seconds", "Explicit WiFi bench duration, 1..900 seconds.", 0);
    private readonly Option<bool> _wifiInit = Flag("--wifi-bench-init", "Create the local WiFi bench identity (explicit, one time).");
    private readonly Option<bool> _wifiCheck = Flag("--wifi-bench-check", "Read-only check that the interface of --wifi-bind is physical, Up and already Private.");
    private readonly Option<bool> _wifiIssuePeer = Flag("--wifi-bench-issue-peer", "Issue and activate one test peer from a public key file and write the bench offer.");
    private readonly Option<bool> _wifiRevoke = Flag("--wifi-bench-revoke", "Revoke one test peer by --wifi-peer-fingerprint.");
    private readonly Option<bool> _wifiRecover = Flag("--wifi-bench-recover", "Preview, or with --wifi-recover-confirm apply, local recovery after an unclean WiFi owner exit.");
    private readonly Option<string> _wifiPeerSpki = Value("--wifi-peer-spki", "DER SubjectPublicKeyInfo file written by the test client keygen.", "");
    private readonly Option<string> _wifiPeerFingerprint = Value("--wifi-peer-fingerprint", "SHA-256 (64 hex) of the public key; the operator's local approval.", "");
    private readonly Option<string> _wifiOfferOut = Value("--wifi-offer-out", "New file for the public bench offer.", "");
    private readonly Option<string> _wifiRecoverConfirm = Value("--wifi-recover-confirm", "Scope reported by the preview: none, one or all.", "");
    private readonly Option<bool> _wifiCableDisconnected = Flag("--wifi-cable-disconnected", "Local operator confirms all cable carriers are disconnected for this bench.");
    private readonly Option<string?> _usbDevicePath = new("--usb-device-path")
    {
        Description = "Native USB device path (defaults to PANELOS_USB_DEVICE_PATH).",
    };
    private readonly Option<string> _videoCodec = Value(
        "--video-codec", "Video codec preference: auto, h264 or zlib.", "auto");
    private readonly Option<int> _videoTargetFps = Integer(
        "--video-target-fps",
        "Preferred video session frame rate: 30, 45 or 60.",
        VideoFrameRatePolicy.DefaultPreferredSessionFps);
    private readonly Option<bool> _dumpSensors = Flag(
        "--dump-sensors", "Print the hardware sensor inventory and exit.");
    private readonly Option<bool> _telemetryDebugCpu = Flag(
        "--telemetry-debug-cpu", "Print CPU telemetry diagnostics and exit.");
    private readonly Option<bool> _fakeDesktop = Flag(
        "--fake-desktop", "Use the development desktop implementation.");
    private readonly Option<bool> _videoTestPattern = Flag(
        "--video-test-pattern", "Use a deterministic static video test pattern.");
    private readonly Option<bool> _videoTestPatternDynamic = Flag(
        "--video-test-pattern-dynamic", "Use a dynamic video test pattern.");
    private readonly Option<bool> _touchSelfTest = Flag(
        "--touch-self-test", "Run the touch injection self-test and exit.");
    private readonly Option<bool> _touchSelfTestPrimary = Flag(
        "--touch-self-test-primary", "Run the primary-display touch self-test and exit.");
    private readonly Option<bool> _transportBenchmark = Flag(
        "--transport-benchmark", "Enable the transport benchmark server.");
    private readonly Option<bool> _usbProbe = Flag(
        "--usb-probe", "Probe the native USB accessory interface and exit.");
    private readonly Option<string> _benchmarkOutput = Value(
        "--benchmark-output",
        "Directory for raw benchmark artifacts.",
        Path.Combine("docs", "benchmarks", "transport", "raw"));
    private readonly Option<string> _benchmarkScenario = Value(
        "--benchmark-scenario", "Benchmark scenario name.", "dashboard");
    private readonly Option<int> _benchmarkSamples = Positive(
        "--benchmark-samples", "Samples per benchmark payload.", 2_000);
    private readonly Option<int> _benchmarkWarmup = NonNegative(
        "--benchmark-warmup", "Warmup samples per benchmark payload.", 100);
    private readonly Option<int> _benchmarkRepetitions = Positive(
        "--benchmark-repetitions", "Benchmark repetitions.", 3);
    private readonly Option<int> _benchmarkIdleSeconds = NonNegative(
        "--benchmark-idle-seconds", "Idle observation time in seconds.", 10);

    internal PanelHostCommandLine(
        Func<PanelHostOptions, CancellationToken, Task<int>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Root = new RootCommand("PanelOS host for control, video and management channels")
        {
            TreatUnmatchedTokensAsErrors = true,
        };

        _transport.AcceptOnlyFromAmong("adb", "usb");
        _videoCodec.AcceptOnlyFromAmong("auto", "h264", "zlib");
        _videoTargetFps.Validators.Add(ValidateTargetFps);
        foreach (var option in Options)
        {
            Root.Options.Add(option);
        }

        Root.SetAction((parseResult, cancellationToken) =>
            action(Bind(parseResult), cancellationToken));
    }

    internal RootCommand Root { get; }

    internal IReadOnlyList<Option> Options =>
    [
        _transport, _usbDevicePath, _videoCodec, _videoTargetFps,
        _wifiBench, _wifiBind, _wifiPort, _wifiPairPort, _wifiWindow, _wifiCableDisconnected,
        _wifiInit, _wifiCheck, _wifiIssuePeer, _wifiRevoke, _wifiRecover, _wifiPeerSpki, _wifiPeerFingerprint, _wifiOfferOut, _wifiRecoverConfirm,
        _dumpSensors, _telemetryDebugCpu, _fakeDesktop,
        _videoTestPattern, _videoTestPatternDynamic,
        _touchSelfTest, _touchSelfTestPrimary,
        _transportBenchmark, _usbProbe,
        _benchmarkOutput, _benchmarkScenario, _benchmarkSamples,
        _benchmarkWarmup, _benchmarkRepetitions, _benchmarkIdleSeconds,
    ];

    internal ParseResult Parse(IReadOnlyList<string> args) =>
        Root.Parse(NormalizeArguments(args));

    internal async Task<int> InvokeAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var configuration = new InvocationConfiguration
        {
            Output = output ?? throw new ArgumentNullException(nameof(output)),
            Error = error ?? throw new ArgumentNullException(nameof(error)),
            EnableDefaultExceptionHandler = false,
        };
        var parseResult = Root.Parse(NormalizeArguments(args));
        var exitCode = await parseResult
            .InvokeAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        return parseResult.Errors.Count == 0
            ? exitCode
            : PanelHostApplication.CommandLineErrorExitCode;
    }

    internal PanelHostOptions Bind(ParseResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Errors.Count != 0)
        {
            throw new InvalidOperationException("Cannot bind an invalid command line.");
        }

        var transport = result.GetRequiredValue(_transport);
        var codec = result.GetRequiredValue(_videoCodec);
        return new PanelHostOptions
        {
            WifiBench = new(result.GetValue(_wifiBench), result.GetRequiredValue(_wifiBind), result.GetValue(_wifiPort),
                result.GetValue(_wifiWindow), result.GetValue(_wifiCableDisconnected), result.GetValue(_wifiPairPort)),
            WifiAdmin = BindWifiAdmin(result),
            Transport = transport.Equals("usb", StringComparison.OrdinalIgnoreCase)
                ? PanelHostTransportKind.Usb
                : PanelHostTransportKind.Adb,
            UsbDevicePath = result.GetValue(_usbDevicePath) ??
                Environment.GetEnvironmentVariable("PANELOS_USB_DEVICE_PATH"),
            VideoCodec = codec.ToLowerInvariant() switch
            {
                "h264" => VideoCodecPreference.H264,
                "zlib" => VideoCodecPreference.Zlib,
                _ => VideoCodecPreference.Auto,
            },
            VideoTargetFps = result.GetValue(_videoTargetFps),
            DumpSensors = result.GetValue(_dumpSensors),
            TelemetryDebugCpu = result.GetValue(_telemetryDebugCpu),
            FakeDesktop = result.GetValue(_fakeDesktop),
            VideoTestPattern = result.GetValue(_videoTestPattern),
            VideoTestPatternDynamic = result.GetValue(_videoTestPatternDynamic),
            TouchSelfTest = result.GetValue(_touchSelfTest),
            TouchSelfTestPrimary = result.GetValue(_touchSelfTestPrimary),
            TransportBenchmark = result.GetValue(_transportBenchmark),
            UsbProbe = result.GetValue(_usbProbe),
            BenchmarkOutput = result.GetRequiredValue(_benchmarkOutput),
            BenchmarkScenario = result.GetRequiredValue(_benchmarkScenario),
            BenchmarkSamples = result.GetValue(_benchmarkSamples),
            BenchmarkWarmup = result.GetValue(_benchmarkWarmup),
            BenchmarkRepetitions = result.GetValue(_benchmarkRepetitions),
            BenchmarkIdleSeconds = result.GetValue(_benchmarkIdleSeconds),
        };
    }

    private WifiBenchAdminOptions BindWifiAdmin(ParseResult result)
    {
        var selected = new[]
        {
            (result.GetValue(_wifiInit), WifiAdminCommand.Init), (result.GetValue(_wifiIssuePeer), WifiAdminCommand.IssuePeer),
            (result.GetValue(_wifiRevoke), WifiAdminCommand.Revoke), (result.GetValue(_wifiRecover), WifiAdminCommand.Recover),
            (result.GetValue(_wifiCheck), WifiAdminCommand.Check),
        }.Where(item => item.Item1).Select(item => item.Item2).ToArray();
        // More than one action, or an action next to the listener, is ambiguous: keep it visible as a usage error.
        var command = selected.Length == 1 && !result.GetValue(_wifiBench) ? selected[0] : WifiAdminCommand.None;
        return new(command, result.GetRequiredValue(_wifiPeerSpki), result.GetRequiredValue(_wifiPeerFingerprint), result.GetRequiredValue(_wifiOfferOut),
            result.GetRequiredValue(_wifiBind), result.GetValue(_wifiPort), result.GetRequiredValue(_wifiRecoverConfirm)) { Ambiguous = selected.Length > 1 || selected.Length == 1 && result.GetValue(_wifiBench) };
    }

    private static Option<bool> Flag(string name, string description) => new(name)
    {
        Description = description,
    };

    private static Option<string> Value(
        string name,
        string description,
        string defaultValue) => new(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue,
    };

    private static Option<int> Integer(
        string name,
        string description,
        int defaultValue) => new(name)
    {
        Description = description,
        DefaultValueFactory = _ => defaultValue,
    };

    private static Option<int> Positive(string name, string description, int value)
    {
        var option = Integer(name, description, value);
        option.Validators.Add(result =>
        {
            if (TryReadInteger(result, out var parsed) && parsed <= 0)
            {
                result.AddError($"{name} must be positive.");
            }
        });
        return option;
    }

    private static Option<int> NonNegative(string name, string description, int value)
    {
        var option = Integer(name, description, value);
        option.Validators.Add(result =>
        {
            if (TryReadInteger(result, out var parsed) && parsed < 0)
            {
                result.AddError($"{name} must be a non-negative integer.");
            }
        });
        return option;
    }

    private static void ValidateTargetFps(OptionResult result)
    {
        if (TryReadInteger(result, out var targetFps) &&
            !VideoFrameRatePolicy.IsSupportedHostPreference(targetFps))
        {
            result.AddError("--video-target-fps requires 30, 45, or 60.");
        }
    }

    private static bool TryReadInteger(OptionResult result, out int value)
    {
        if (result.Tokens.Count == 0)
        {
            value = result.GetValueOrDefault<int>();
            return true;
        }
        return int.TryParse(
            result.Tokens[0].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private IReadOnlyList<string> NormalizeArguments(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var names = Options.ToDictionary(
            option => option.Name,
            StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(args.Count);
        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!names.TryGetValue(token, out var option))
            {
                normalized.Add(token);
                continue;
            }

            normalized.Add(option.Name);
            if ((ReferenceEquals(option, _transport) ||
                 ReferenceEquals(option, _videoCodec)) &&
                index + 1 < args.Count)
            {
                normalized.Add(args[++index].ToLowerInvariant());
            }
        }
        return normalized;
    }
}
