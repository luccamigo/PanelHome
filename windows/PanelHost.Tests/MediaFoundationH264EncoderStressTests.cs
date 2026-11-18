using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class MediaFoundationH264EncoderStressTests
{
    private const string FrameCountVariable = "PANELHOST_H264_STRESS_FRAMES";

    [TestMethod]
    [TestCategory("Stress")]
    public async Task Dynamic_frames_survive_repeated_need_more_input_and_gc()
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable(FrameCountVariable),
                out var frameCount) ||
            frameCount <= 0)
        {
            Assert.Inconclusive(
                $"Set {FrameCountVariable}=3000 to run the Media Foundation stress test.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        await using var factory = new MediaFoundationH264EncoderFactory(Console.Out);
        var descriptor = await factory.ProbeAsync(timeout.Token);
        if (descriptor is null)
        {
            Assert.Inconclusive("No Media Foundation H.264 encoder is available.");
            return;
        }

        var settings = new H264EncoderSettings(
            VideoTestPatternFrameSource.PatternWidth,
            VideoTestPatternFrameSource.PatternHeight,
            Fps: 30,
            Bitrate: 4_000_000,
            KeyFrameInterval: 30);
        await using var encoder = await factory.CreateAsync(settings, timeout.Token);
        var source = new VideoTestPatternFrameSource(VideoTestPatternMode.Dynamic);
        var converter = new BgraToNv12FrameConverter();
        ulong lastSequence = 0;
        var outputCount = 0;

        for (var index = 0; index < frameCount; index++)
        {
            var sourceFrame = await source.GetLatestFrameAsync(
                lastSequence,
                timeout.Token);
            Assert.IsNotNull(sourceFrame);
            lastSequence = sourceFrame.Sequence;
            var nv12 = converter.Convert(
                sourceFrame,
                settings.Width,
                settings.Height);
            var outputs = await encoder.EncodeAsync(
                nv12,
                forceKeyFrame: index % settings.KeyFrameInterval == 0,
                timeout.Token);
            outputCount += outputs.Count;

            if ((index + 1) % 100 == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        Assert.AreEqual((ulong)frameCount, lastSequence);
        Assert.IsTrue(outputCount > 0);
    }
}
