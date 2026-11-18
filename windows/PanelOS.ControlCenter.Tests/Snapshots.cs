using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// Management snapshots for the view-model tests: one healthy baseline that
/// each case bends with a <c>with</c> expression, so a test states only the
/// fact it is about.
/// </summary>
internal static class Snapshots
{
    /// <summary>Agent up, tablet connected, display live, video flowing.</summary>
    internal static PanelHostSnapshot Healthy() => new()
    {
        Sequence = 1,
        ManagementProtocolVersion = PanelHostManagementProtocol.Version,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Host = new HostManagementSnapshot(
            4242,
            "1.2.3",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            300),
        Tablet = new TabletManagementSnapshot(
            ControlConnected: true,
            VideoConnected: true,
            ProtocolVersion: 1,
            NegotiatedFeatures: ["desktop", "touch", "stream_deck"],
            ControlSessionGeneration: 2,
            VideoSessionGeneration: 2),
        Display = new DisplayManagementSnapshot(true, "Running", 1920, 1200, 60),
        Video = new VideoManagementSnapshot
        {
            Connected = true,
            SessionGeneration = 2,
            Codec = "H264",
            Encoder = "hardware encoder",
            Width = 1920,
            Height = 1200,
            ConfiguredFps = 60,
            NegotiatedFps = 60,
            DriverCaptureFps = 59.4,
            HostReceiveFps = 59.2,
            SendFps = 59.1,
            BandwidthMegabytesPerSecond = 3.25,
            EncodeLatencyMilliseconds = 4.2,
            CaptureToSendMilliseconds = 18.5,
            SequenceGap = 0,
        },
        StreamDeck = new StreamDeckManagementSnapshot(
            "profile-1",
            "Principal",
            "abcdef",
            2,
            8),
    };

    /// <summary>
    /// The agent is up and answering, but nothing has connected to it: no
    /// tablet, no display, no video session.
    /// </summary>
    internal static PanelHostSnapshot Idle() => Healthy() with
    {
        Tablet = new TabletManagementSnapshot(
            ControlConnected: false,
            VideoConnected: false,
            ProtocolVersion: null,
            NegotiatedFeatures: [],
            ControlSessionGeneration: 0,
            VideoSessionGeneration: 0),
        Display = new DisplayManagementSnapshot(false, "Stopped", null, null, null),
        Video = new VideoManagementSnapshot(),
    };
}
