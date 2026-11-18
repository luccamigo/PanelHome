using PanelHost.Transport;
using PanelHost.Security.Wifi;
using PanelHost.Video;

namespace PanelHost.Hosting;

internal enum PanelHostTransportKind
{
    Adb,
    Usb,
}

internal sealed record PanelHostOptions
{
    internal PanelHostTransportKind Transport { get; init; } = PanelHostTransportKind.Adb;
    internal string? UsbDevicePath { get; init; }
    internal VideoCodecPreference VideoCodec { get; init; } = VideoCodecPreference.Auto;
    internal int VideoTargetFps { get; init; } = VideoFrameRatePolicy.DefaultPreferredSessionFps;
    internal bool DumpSensors { get; init; }
    internal bool TelemetryDebugCpu { get; init; }
    internal bool FakeDesktop { get; init; }
    internal bool VideoTestPattern { get; init; }
    internal bool VideoTestPatternDynamic { get; init; }
    internal bool TouchSelfTest { get; init; }
    internal bool TouchSelfTestPrimary { get; init; }
    internal bool TransportBenchmark { get; init; }
    internal bool UsbProbe { get; init; }
    internal WifiBenchOptions WifiBench { get; init; } = new();
    internal WifiBenchAdminOptions WifiAdmin { get; init; } = new();
    internal string BenchmarkOutput { get; init; } =
        Path.Combine("docs", "benchmarks", "transport", "raw");
    internal string BenchmarkScenario { get; init; } = "dashboard";
    internal int BenchmarkSamples { get; init; } = 2_000;
    internal int BenchmarkWarmup { get; init; } = 100;
    internal int BenchmarkRepetitions { get; init; } = 3;
    internal int BenchmarkIdleSeconds { get; init; } = 10;

    internal bool RunsTelemetryCommand => DumpSensors || TelemetryDebugCpu;
    internal bool RunsTouchSelfTest => TouchSelfTest || TouchSelfTestPrimary;
    internal string TransportName => Transport == PanelHostTransportKind.Usb ? "usb" : "adb";

    internal TransportBenchmarkOptions? CreateBenchmarkOptions() => TransportBenchmark
        ? new TransportBenchmarkOptions(
            BenchmarkOutput,
            BenchmarkScenario,
            BenchmarkSamples,
            BenchmarkWarmup,
            BenchmarkRepetitions,
            BenchmarkIdleSeconds)
        : null;
}
