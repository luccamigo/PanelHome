using PanelOS.DriverInterop;

namespace PanelHost.Video;

internal interface IFrameSource
{
    Task<FrameSnapshot?> GetLatestFrameAsync(
        ulong lastSequence,
        CancellationToken cancellationToken);
}

internal interface IFrameSourceCaptureMetrics
{
    Task<PanelDisplayCaptureMetrics?> GetCaptureMetricsAsync(
        CancellationToken cancellationToken);
}

internal interface IFrameSourceCaptureConfiguration
{
    Task ConfigureCaptureTargetFpsAsync(
        int targetFramesPerSecond,
        CancellationToken cancellationToken);
}

internal interface IFrameSourceSessionBoundary
{
    Task<FrameSourceSessionBoundary> BeginSessionAsync(
        CancellationToken cancellationToken);
}

internal readonly record struct FrameSourceSessionBoundary(
    ulong Generation,
    ulong LastSequence);

internal sealed record FrameSnapshot(
    ulong Generation,
    ulong Sequence,
    ulong CaptureTimestampMicros,
    int Width,
    int Height,
    int Stride,
    PanelDisplayFrameFormat Format,
    byte[] Pixels);

internal sealed class EmptyFrameSource : IFrameSource
{
    public Task<FrameSnapshot?> GetLatestFrameAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<FrameSnapshot?>(null);
    }
}
