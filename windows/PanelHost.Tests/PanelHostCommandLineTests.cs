using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PanelHost.Hosting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelHostCommandLineTests
{
    [TestMethod]
    public void AllOptionsParseIntoOneCommandModel()
    {
        var commandLine = CreateCommandLine();
        var parse = commandLine.Parse(
        [
            "--transport", "usb",
            "--usb-device-path", "usb-path",
            "--video-codec", "h264",
            "--video-target-fps", "45",
            "--dump-sensors",
            "--telemetry-debug-cpu",
            "--fake-desktop",
            "--video-test-pattern",
            "--video-test-pattern-dynamic",
            "--touch-self-test",
            "--touch-self-test-primary",
            "--transport-benchmark",
            "--usb-probe",
            "--benchmark-output", "artifacts",
            "--benchmark-scenario", "desktop-live",
            "--benchmark-samples", "25",
            "--benchmark-warmup", "3",
            "--benchmark-repetitions", "2",
            "--benchmark-idle-seconds", "0",
        ]);

        Assert.AreEqual(0, parse.Errors.Count);
        var options = commandLine.Bind(parse);
        Assert.AreEqual(PanelHostTransportKind.Usb, options.Transport);
        Assert.AreEqual("usb-path", options.UsbDevicePath);
        Assert.AreEqual(VideoCodecPreference.H264, options.VideoCodec);
        Assert.AreEqual(45, options.VideoTargetFps);
        Assert.IsTrue(options.DumpSensors);
        Assert.IsTrue(options.TelemetryDebugCpu);
        Assert.IsTrue(options.FakeDesktop);
        Assert.IsTrue(options.VideoTestPattern);
        Assert.IsTrue(options.VideoTestPatternDynamic);
        Assert.IsTrue(options.TouchSelfTest);
        Assert.IsTrue(options.TouchSelfTestPrimary);
        Assert.IsTrue(options.TransportBenchmark);
        Assert.IsTrue(options.UsbProbe);
        Assert.AreEqual("artifacts", options.BenchmarkOutput);
        Assert.AreEqual("desktop-live", options.BenchmarkScenario);
        Assert.AreEqual(25, options.BenchmarkSamples);
        Assert.AreEqual(3, options.BenchmarkWarmup);
        Assert.AreEqual(2, options.BenchmarkRepetitions);
        Assert.AreEqual(0, options.BenchmarkIdleSeconds);
    }

    [TestMethod]
    public async Task UnknownOptionIsRejectedWithoutRunningTheApplication()
    {
        var called = false;
        var commandLine = new PanelHostCommandLine((_, _) =>
        {
            called = true;
            return Task.FromResult(0);
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await commandLine.InvokeAsync(
            ["--unknown-option"], output, error);

        Assert.AreNotEqual(0, exitCode);
        Assert.IsFalse(called);
        StringAssert.Contains(error.ToString(), "--unknown-option");
    }

    [TestMethod]
    public async Task HelpContainsEveryRegisteredOptionIncludingBenchmarkSamples()
    {
        var commandLine = CreateCommandLine();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await commandLine.InvokeAsync(["--help"], output, error);

        Assert.AreEqual(0, exitCode);
        var help = output.ToString();
        StringAssert.Contains(help, "--benchmark-samples");
        foreach (var option in commandLine.Options)
        {
            StringAssert.Contains(help, option.Name);
        }
    }

    [TestMethod]
    public void DefaultTransportAndVideoOptionsRemainCompatible()
    {
        var commandLine = CreateCommandLine();
        var options = commandLine.Bind(commandLine.Parse([]));

        Assert.AreEqual(PanelHostTransportKind.Adb, options.Transport);
        Assert.AreEqual(VideoCodecPreference.Auto, options.VideoCodec);
        Assert.AreEqual(60, options.VideoTargetFps);
    }

    [TestMethod]
    public void ExistingCaseInsensitiveArgumentsRemainSupported()
    {
        var commandLine = CreateCommandLine();
        var parse = commandLine.Parse(
            ["--TRANSPORT", "USB", "--VIDEO-CODEC", "H264"]);

        Assert.AreEqual(0, parse.Errors.Count);
        var options = commandLine.Bind(parse);
        Assert.AreEqual(PanelHostTransportKind.Usb, options.Transport);
        Assert.AreEqual(VideoCodecPreference.H264, options.VideoCodec);
    }

    [DataTestMethod]
    [DataRow("29")]
    [DataRow("31")]
    [DataRow("61")]
    [DataRow("not-a-number")]
    public void InvalidFpsIsRejected(string value)
    {
        var parse = CreateCommandLine().Parse(["--video-target-fps", value]);

        Assert.AreNotEqual(0, parse.Errors.Count);
    }

    [TestMethod]
    public void BenchmarkFlagsKeepDefaultsAndValidateRanges()
    {
        var commandLine = CreateCommandLine();
        var defaults = commandLine.Bind(commandLine.Parse(["--transport-benchmark"]));
        Assert.AreEqual(2_000, defaults.BenchmarkSamples);
        Assert.AreEqual(100, defaults.BenchmarkWarmup);
        Assert.AreEqual(3, defaults.BenchmarkRepetitions);
        Assert.AreEqual(10, defaults.BenchmarkIdleSeconds);
        Assert.IsNotNull(defaults.CreateBenchmarkOptions());

        Assert.AreNotEqual(0, commandLine.Parse(
            ["--benchmark-samples", "0"]).Errors.Count);
        Assert.AreNotEqual(0, commandLine.Parse(
            ["--benchmark-warmup", "-1"]).Errors.Count);
        Assert.AreNotEqual(0, commandLine.Parse(
            ["--benchmark-repetitions", "0"]).Errors.Count);
    }

    [TestMethod]
    public void GenericHostGraphResolvesTheCoordinatingHostedService()
    {
        var options = new PanelHostOptions
        {
            Transport = PanelHostTransportKind.Usb,
            FakeDesktop = true,
            VideoTestPattern = true,
        };

        using var host = PanelHostApplication.BuildHost(options);

        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        CollectionAssert.AreEquivalent(new[] { typeof(PanelHostRuntimeHostedService), typeof(LocalIntegrationHostedService), typeof(PanelHost.Services.ClaudeHookPipeServer) },
            hostedServices.Select(service => service.GetType()).ToArray());
        Assert.AreEqual(1, hostedServices.OfType<PanelHostRuntimeHostedService>().Count());
    }

    private static PanelHostCommandLine CreateCommandLine() =>
        new((_, _) => Task.FromResult(0));
}
