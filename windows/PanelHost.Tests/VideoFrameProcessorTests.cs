using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoFrameProcessorTests
{
    [TestMethod]
    public void Fit_inside_preserves_aspect_ratio_and_never_upscales()
    {
        Assert.AreEqual((640, 400),
            VideoFrameProcessor.FitInside(1280, 800, 640, 400));
        Assert.AreEqual((320, 200),
            VideoFrameProcessor.FitInside(320, 200, 640, 400));
        Assert.AreEqual((225, 400),
            VideoFrameProcessor.FitInside(900, 1600, 640, 400));
    }

    [TestMethod]
    public void Rgba_is_normalized_to_bgra()
    {
        var source = Snapshot(
            width: 1,
            height: 1,
            PanelDisplayFrameFormat.Rgba8888,
            [10, 20, 30, 0]);

        var pixels = VideoFrameProcessor.NormalizeAndResize(source, 1, 1);

        CollectionAssert.AreEqual(new byte[] { 30, 20, 10, 255 }, pixels);
    }

    [TestMethod]
    public void Bgra_alpha_zero_is_forced_opaque_without_changing_color()
    {
        var source = Snapshot(
            width: 1,
            height: 1,
            PanelDisplayFrameFormat.Bgra8888,
            [30, 20, 10, 0]);

        var pixels = VideoFrameProcessor.NormalizeAndResize(source, 1, 1);

        CollectionAssert.AreEqual(new byte[] { 30, 20, 10, 255 }, pixels);
    }

    [TestMethod]
    public void Nearest_neighbor_downscale_uses_source_pixels_and_padding_safely()
    {
        var pixels = new byte[40]; // 4x2 at stride 20.
        for (var x = 0; x < 4; x++)
        {
            pixels[x * 4] = (byte)x;
            pixels[20 + x * 4] = (byte)(10 + x);
            pixels[x * 4 + 3] = pixels[20 + x * 4 + 3] = 255;
        }

        var source = new FrameSnapshot(
            1, 1, 1, 4, 2, 20,
            PanelDisplayFrameFormat.Bgra8888,
            pixels);

        var result = VideoFrameProcessor.NormalizeAndResize(source, 2, 2);

        Assert.AreEqual(0, result[0]);
        Assert.AreEqual(2, result[4]);
        Assert.AreEqual(10, result[8]);
        Assert.AreEqual(12, result[12]);
        Assert.AreEqual(255, result[11]);
        Assert.AreEqual(255, result[15]);
    }

    [TestMethod]
    public void Preview_exposes_compact_opaque_bgra_before_compression()
    {
        var source = new FrameSnapshot(
            1,
            7,
            123,
            2,
            2,
            12,
            PanelDisplayFrameFormat.Rgba8888,
            [
                255, 0, 0, 0, 0, 255, 0, 0, 91, 92, 93, 94,
                0, 0, 255, 0, 255, 255, 255, 0, 95, 96, 97, 98,
            ]);

        var preview = VideoFrameProcessor.CreatePreview(source, 640, 400);

        Assert.AreEqual(7UL, preview.Sequence);
        Assert.AreEqual(2, preview.Width);
        Assert.AreEqual(2, preview.Height);
        Assert.AreEqual(8, preview.Stride);
        CollectionAssert.AreEqual(
            new byte[]
            {
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 255,
            },
            preview.Pixels);
    }

    [TestMethod]
    public void Zlib_payload_round_trips_canonical_bgra()
    {
        var original = new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 };
        var encoded = VideoFrameProcessor.Process(
            Snapshot(2, 1, PanelDisplayFrameFormat.Bgra8888, original),
            640,
            400);

        using var input = new MemoryStream(encoded.CompressedPixels);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);

        CollectionAssert.AreEqual(original, output.ToArray());
        Assert.AreEqual(8u, encoded.UncompressedBytes);
        Assert.AreEqual(
            VideoFrameDiagnostics.ComputeCrc32(original),
            VideoFrameDiagnostics.ComputeCrc32(output.ToArray()));
    }

    private static FrameSnapshot Snapshot(
        int width,
        int height,
        PanelDisplayFrameFormat format,
        byte[] pixels) => new(
            1,
            1,
            1,
            width,
            height,
            width * 4,
            format,
            pixels);
}
