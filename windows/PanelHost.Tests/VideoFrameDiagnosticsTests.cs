using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoFrameDiagnosticsTests
{
    [TestMethod]
    public void Crc32_matches_standard_check_value()
    {
        Assert.AreEqual(
            0xCBF43926u,
            VideoFrameDiagnostics.ComputeCrc32("123456789"u8));
    }

    [TestMethod]
    public void Grid_distinguishes_black_from_colored_frame()
    {
        var black = VideoFrameDiagnostics.Analyze(
            new byte[16 * 16 * 4],
            16,
            16,
            16 * 4,
            PanelDisplayFrameFormat.Bgra8888);
        var coloredPixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray();
        var colored = VideoFrameDiagnostics.Analyze(
            coloredPixels,
            16,
            16,
            16 * 4,
            PanelDisplayFrameFormat.Bgra8888);

        Assert.AreEqual(0, black.MinimumRgb);
        Assert.AreEqual(0, black.MaximumRgb);
        Assert.AreEqual(0, black.NonBlackSamples);
        Assert.AreEqual(255, colored.MinimumRgb);
        Assert.AreEqual(255, colored.MaximumRgb);
        Assert.AreEqual(256, colored.NonBlackSamples);
    }

    [TestMethod]
    public void Luminance_matches_driver_average_rgb_definition()
    {
        var red = Enumerable.Range(0, 16 * 16)
            .SelectMany(_ => new byte[] { 0, 0, 255, 255 })
            .ToArray();

        var analysis = VideoFrameDiagnostics.Analyze(
            red,
            16,
            16,
            16 * 4,
            PanelDisplayFrameFormat.Bgra8888);

        Assert.AreEqual(85, analysis.MinimumRgb);
        Assert.AreEqual(85, analysis.MaximumRgb);
        Assert.AreEqual(85d, analysis.AverageRgb);
        Assert.AreEqual(256, analysis.NonBlackSamples);
    }

    [TestMethod]
    public void Logging_is_bounded_per_stage()
    {
        using var output = new StringWriter();
        var diagnostics = new VideoFrameDiagnostics(output);
        for (ulong sequence = 1; sequence <= 4; sequence++)
        {
            diagnostics.LogRaw(new FrameSnapshot(
                1,
                sequence,
                sequence,
                1,
                1,
                4,
                PanelDisplayFrameFormat.Bgra8888,
                [1, 2, 3, 4]));
        }

        var log = output.ToString();
        Assert.IsTrue(log.Contains("sequence=1", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("sequence=2", StringComparison.Ordinal));
        Assert.IsFalse(log.Contains("sequence=3", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("crc32=", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("nonBlack=256/256", StringComparison.Ordinal));
    }
}
