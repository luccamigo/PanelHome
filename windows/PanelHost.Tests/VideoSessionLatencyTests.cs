using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class VideoSessionLatencyTests
{
    [TestMethod]
    public void Frame_captured_before_session_does_not_contaminate_latency()
    {
        Assert.IsFalse(VideoSessionLatency.TryCalculate(
            sessionStartedMicros: 2_000_000,
            captureTimestampMicros: 1_000_000,
            sendTimestampMicros: 2_100_000,
            out var latency));
        Assert.AreEqual(0L, latency);
    }

    [TestMethod]
    public void Frame_captured_in_session_contributes_latency()
    {
        Assert.IsTrue(VideoSessionLatency.TryCalculate(
            sessionStartedMicros: 2_000_000,
            captureTimestampMicros: 2_050_000,
            sendTimestampMicros: 2_141_000,
            out var latency));
        Assert.AreEqual(91_000L, latency);
    }
}
