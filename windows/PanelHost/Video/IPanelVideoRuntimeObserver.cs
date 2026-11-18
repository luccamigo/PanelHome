namespace PanelHost.Video;

/// <summary>Structured view of the exact values emitted by VideoPerf/VideoPacing.</summary>
internal sealed record PanelVideoRuntimeMetrics(
    string Codec,
    string? Encoder,
    int? Width,
    int? Height,
    int ConfiguredFps,
    int NegotiatedFps,
    double DriverCaptureFps,
    double HostReceiveFps,
    double SendFps,
    double BandwidthMegabytesPerSecond,
    double? EncodeLatencyMilliseconds,
    double? CaptureToSendMilliseconds,
    long SequenceGap);

internal interface IPanelVideoRuntimeObserver
{
    long SessionConnected(VideoAck ack, int configuredFps);

    void MetricsUpdated(long sessionGeneration, PanelVideoRuntimeMetrics metrics);

    void SessionDisconnected(long sessionGeneration);
}

internal sealed class NullPanelVideoRuntimeObserver : IPanelVideoRuntimeObserver
{
    internal static NullPanelVideoRuntimeObserver Instance { get; } = new();

    public long SessionConnected(VideoAck ack, int configuredFps) => 0;

    public void MetricsUpdated(long sessionGeneration, PanelVideoRuntimeMetrics metrics)
    {
    }

    public void SessionDisconnected(long sessionGeneration)
    {
    }
}
