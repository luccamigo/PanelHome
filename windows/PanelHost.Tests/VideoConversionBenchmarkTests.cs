using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoConversionBenchmarkTests
{
    private const string FrameCountVariable = "PANELHOST_NV12_BENCHMARK_FRAMES";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Benchmark")]
    public void CompareResizeAndNativeNv12Paths()
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable(FrameCountVariable),
                out var frameCount) ||
            frameCount < 1_000)
        {
            Assert.Inconclusive(
                $"Set {FrameCountVariable}=2000 or more to run the NV12 benchmark.");
            return;
        }

        var resized = Benchmark(
            sourceWidth: 1920,
            sourceHeight: 1200,
            targetWidth: 1280,
            targetHeight: 800,
            frameCount);
        var native = Benchmark(
            sourceWidth: 1280,
            sourceHeight: 800,
            targetWidth: 1280,
            targetHeight: 800,
            frameCount);

        TestContext.WriteLine(
            $"1920x1200 BGRA -> 1280x800 NV12: {resized}");
        TestContext.WriteLine(
            $"1280x800 BGRA -> 1280x800 NV12: {native}");
        Assert.IsTrue(native.P95Milliseconds <= resized.P95Milliseconds * 1.10,
            "Native-resolution conversion unexpectedly regressed beyond resized p95.");
    }

    private static BenchmarkStatistics Benchmark(
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        int frameCount)
    {
        var sourcePixels = new byte[checked(sourceWidth * sourceHeight * 4)];
        for (var offset = 0; offset < sourcePixels.Length; offset += 4)
        {
            sourcePixels[offset] = checked((byte)(offset / 4 % 251));
            sourcePixels[offset + 1] = checked((byte)(offset / 8 % 241));
            sourcePixels[offset + 2] = checked((byte)(offset / 16 % 239));
            sourcePixels[offset + 3] = 255;
        }

        var source = new FrameSnapshot(
            Generation: 1,
            Sequence: 1,
            CaptureTimestampMicros: 1,
            sourceWidth,
            sourceHeight,
            Stride: checked(sourceWidth * 4),
            PanelDisplayFrameFormat.Bgra8888,
            sourcePixels);
        var converter = new BgraToNv12FrameConverter();
        for (var warmup = 0; warmup < 50; warmup++)
        {
            _ = converter.Convert(source, targetWidth, targetHeight);
        }

        var samples = new long[frameCount];
        byte checksum = 0;
        for (var index = 0; index < frameCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var converted = converter.Convert(source, targetWidth, targetHeight);
            samples[index] = Stopwatch.GetElapsedTime(started).Ticks / 10;
            checksum ^= converted.Pixels[index % converted.Pixels.Length];
        }
        GC.KeepAlive(checksum);

        Array.Sort(samples);
        var average = samples.Average() / 1000d;
        return new BenchmarkStatistics(
            frameCount,
            average,
            samples[PercentileIndex(frameCount, 0.50)] / 1000d,
            samples[PercentileIndex(frameCount, 0.95)] / 1000d);
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);

    private readonly record struct BenchmarkStatistics(
        int Frames,
        double AverageMilliseconds,
        double P50Milliseconds,
        double P95Milliseconds)
    {
        public override string ToString() =>
            $"frames={Frames} avg={AverageMilliseconds:F3}ms " +
            $"p50={P50Milliseconds:F3}ms p95={P95Milliseconds:F3}ms";
    }
}
