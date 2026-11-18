namespace PanelHost.Video;

internal sealed record H264EncoderSettings(
    int Width,
    int Height,
    int Fps,
    int Bitrate,
    int KeyFrameInterval)
{
    internal void Validate()
    {
        if (Width <= 0 || Height <= 0 || (Width & 1) != 0 || (Height & 1) != 0 ||
            Width > 16_384 || Height > 16_384 ||
            Fps is <= 0 or > 60 || Bitrate <= 0 || KeyFrameInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Width),
                "Invalid H.264 encoder settings.");
        }
    }
}

internal sealed record H264EncoderDescriptor(string Name, bool IsHardware);

internal static class H264EncoderSelection
{
    internal static IEnumerable<H264EncoderDescriptor> Order(
        IEnumerable<H264EncoderDescriptor> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.OrderByDescending(candidate => candidate.IsHardware);
    }

    internal static H264EncoderDescriptor? Select(
        IEnumerable<H264EncoderDescriptor> candidates) =>
        Order(candidates).FirstOrDefault();
}

internal sealed record H264EncodedAccessUnit(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    byte[] AnnexB,
    bool IsKeyFrame,
    long EncodeDurationMicros,
    byte[]? DecoderConfiguration,
    long InputSampleCopyDurationMicros = 0,
    long ProcessInputDurationMicros = 0,
    long ProcessOutputDurationMicros = 0,
    long OutputSampleCopyDurationMicros = 0);

internal interface IH264EncoderFactory : IAsyncDisposable
{
    H264EncoderDescriptor? SelectedEncoder { get; }

    Task<H264EncoderDescriptor?> ProbeAsync(CancellationToken cancellationToken);

    Task<IH264Encoder> CreateAsync(
        H264EncoderSettings settings,
        CancellationToken cancellationToken);
}

internal interface IH264Encoder : IAsyncDisposable
{
    H264EncoderDescriptor Descriptor { get; }

    H264EncoderSettings Settings { get; }

    Task<IReadOnlyList<H264EncodedAccessUnit>> EncodeAsync(
        Nv12VideoFrame frame,
        bool forceKeyFrame,
        CancellationToken cancellationToken);
}

internal sealed class UnavailableH264EncoderFactory : IH264EncoderFactory
{
    internal static UnavailableH264EncoderFactory Instance { get; } = new();

    public H264EncoderDescriptor? SelectedEncoder => null;

    public Task<H264EncoderDescriptor?> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<H264EncoderDescriptor?>(null);
    }

    public Task<IH264Encoder> CreateAsync(
        H264EncoderSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("No H.264 Media Foundation encoder is available.");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
