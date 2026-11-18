namespace PanelOS.DriverInterop;

/// <summary>
/// Frame-specific part of the PanelDisplayDriver v5 contract. Keeping it
/// separate from lifecycle operations makes capture independently testable.
/// </summary>
public interface IPanelDisplayDriverFrameClient
{
    Task<PanelDisplayFrameInfo> GetFrameInfoAsync(
        ulong lastSequence,
        CancellationToken cancellationToken);

    Task<PanelDisplayDriverFrame> ReadFrameAsync(
        PanelDisplayFrameInfo requestedFrame,
        CancellationToken cancellationToken);
}

public interface IPanelDisplayDriverCaptureMetricsClient
{
    Task<PanelDisplayCaptureMetrics> GetCaptureMetricsAsync(
        CancellationToken cancellationToken);
}

public interface IPanelDisplayDriverCaptureConfigurationClient
{
    Task<uint> SetCaptureTargetFpsAsync(
        uint targetFramesPerSecond,
        CancellationToken cancellationToken);
}
