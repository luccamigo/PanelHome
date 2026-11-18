using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoTestPatternFrameSourceTests
{
    [TestMethod]
    public async Task Pattern_has_expected_bgra_quadrants_and_latest_semantics()
    {
        var source = new VideoTestPatternFrameSource();

        var frame = await source.GetLatestFrameAsync(0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(640, frame.Width);
        Assert.AreEqual(400, frame.Height);
        Assert.AreEqual(2560, frame.Stride);
        Assert.AreEqual(VideoTestPatternFrameSource.PatternSequence, frame.Sequence);
        AssertPixel(frame.Pixels, frame.Stride, 100, 100, 0, 0, 255, 255);
        AssertPixel(frame.Pixels, frame.Stride, 500, 100, 0, 255, 0, 255);
        AssertPixel(frame.Pixels, frame.Stride, 100, 300, 255, 0, 0, 255);
        AssertPixel(frame.Pixels, frame.Stride, 500, 300, 255, 255, 255, 255);

        Assert.IsNull(await source.GetLatestFrameAsync(
            frame.Sequence,
            CancellationToken.None));
        Assert.IsNotNull(await source.GetLatestFrameAsync(
            0,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task Pattern_resize_and_zlib_preserve_known_colors_and_crc()
    {
        var source = new VideoTestPatternFrameSource();
        var frame = await source.GetLatestFrameAsync(0, CancellationToken.None);
        Assert.IsNotNull(frame);

        var preview = VideoFrameProcessor.CreatePreview(frame, 320, 200);
        var encoded = VideoFrameProcessor.EncodePreview(preview);
        var inflated = Inflate(encoded.CompressedPixels);

        Assert.AreEqual(320, preview.Width);
        Assert.AreEqual(200, preview.Height);
        AssertPixel(inflated, preview.Stride, 50, 50, 0, 0, 255, 255);
        AssertPixel(inflated, preview.Stride, 250, 50, 0, 255, 0, 255);
        AssertPixel(inflated, preview.Stride, 50, 150, 255, 0, 0, 255);
        AssertPixel(inflated, preview.Stride, 250, 150, 255, 255, 255, 255);
        Assert.AreEqual(
            VideoFrameDiagnostics.ComputeCrc32(preview.Pixels),
            VideoFrameDiagnostics.ComputeCrc32(inflated));
        CollectionAssert.AreEqual(preview.Pixels, inflated);
    }

    [TestMethod]
    public async Task Dynamic_pattern_publishes_a_changed_frame_on_every_poll()
    {
        var source = new VideoTestPatternFrameSource(VideoTestPatternMode.Dynamic);

        var first = await source.GetLatestFrameAsync(0, CancellationToken.None);
        Assert.IsNotNull(first);
        var second = await source.GetLatestFrameAsync(
            first.Sequence,
            CancellationToken.None);
        Assert.IsNotNull(second);

        Assert.AreEqual(first.Sequence + 1, second.Sequence);
        Assert.AreNotEqual(first.CaptureTimestampMicros, second.CaptureTimestampMicros);
        Assert.AreNotEqual(
            VideoFrameDiagnostics.ComputeCrc32(first.Pixels),
            VideoFrameDiagnostics.ComputeCrc32(second.Pixels));
    }

    private static byte[] Inflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static void AssertPixel(
        byte[] pixels,
        int stride,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        var offset = checked(y * stride + x * 4);
        CollectionAssert.AreEqual(
            new[] { blue, green, red, alpha },
            pixels[offset..(offset + 4)]);
    }
}
