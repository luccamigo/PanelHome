using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoPerformanceTests
{
    [TestMethod]
    public void Timing_window_reports_average_p50_p95_and_resets()
    {
        var window = new PerformanceTimingWindow();
        window.RecordMicroseconds(1_000);
        window.RecordMicroseconds(2_000);
        window.RecordMicroseconds(10_000);

        var statistics = window.Drain();

        Assert.AreEqual(3, statistics.Count);
        Assert.AreEqual(13d / 3d, statistics.AverageMilliseconds, 0.001);
        Assert.AreEqual(2d, statistics.P50Milliseconds);
        Assert.AreEqual(10d, statistics.P95Milliseconds);
        Assert.AreEqual(0, window.Drain().Count);
    }

    [TestMethod]
    public void Nv12_converter_reuses_geometry_buffer_and_updates_pixels()
    {
        var converter = new BgraToNv12FrameConverter();
        var first = converter.Convert(Frame(red: 255), 2, 2);
        var firstPixels = first.Pixels;
        var second = converter.Convert(Frame(red: 0), 2, 2);

        Assert.AreSame(firstPixels, second.Pixels);
        CollectionAssert.AreEqual(
            new byte[] { 16, 16, 16, 16, 128, 128 },
            second.Pixels);
    }

    [TestMethod]
    public void Nv12_converter_native_steady_state_has_no_managed_allocations()
    {
        var converter = new BgraToNv12FrameConverter();
        var frame = Frame(red: 64);
        for (var warmup = 0; warmup < 10; warmup++)
        {
            _ = converter.Convert(frame, 2, 2);
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            _ = converter.Convert(frame, 2, 2);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void Session_close_reason_is_first_writer_wins()
    {
        var tracker = new VideoSessionCloseReasonTracker();

        Assert.IsTrue(tracker.TrySet(VideoSessionCloseReason.ClientFin));
        Assert.IsFalse(tracker.TrySet(VideoSessionCloseReason.TransportFailure));
        Assert.AreEqual(VideoSessionCloseReason.ClientFin, tracker.Reason);
    }

    private static FrameSnapshot Frame(byte red)
    {
        var pixels = new byte[16];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }

        return new FrameSnapshot(
            1,
            1,
            1,
            2,
            2,
            8,
            PanelDisplayFrameFormat.Bgra8888,
            pixels);
    }
}
