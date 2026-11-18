using PanelOS.DriverInterop;

namespace PanelHost.Video;

internal sealed class PanelDisplayDriverFrameSource :
    IFrameSource,
    IFrameSourceCaptureMetrics,
    IFrameSourceCaptureConfiguration,
    IFrameSourceSessionBoundary
{
    private const int MaximumReadAttempts = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;

    private readonly IPanelDisplayDriverFrameClient _driverClient;
    private readonly IPanelDisplayDriverCaptureMetricsClient? _metricsClient;
    private readonly IPanelDisplayDriverCaptureConfigurationClient? _configurationClient;
    private readonly TextWriter _log;

    internal PanelDisplayDriverFrameSource(
        IPanelDisplayDriverFrameClient driverClient,
        TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(driverClient);
        _driverClient = driverClient;
        _metricsClient = driverClient as IPanelDisplayDriverCaptureMetricsClient;
        _configurationClient =
            driverClient as IPanelDisplayDriverCaptureConfigurationClient;
        _log = log ?? TextWriter.Null;
    }

    public async Task ConfigureCaptureTargetFpsAsync(
        int targetFramesPerSecond,
        CancellationToken cancellationToken)
    {
        if (_configurationClient is null)
        {
            _log.WriteLine(
                $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
                $"success=false requiredProtocol=v{PanelDisplayDriverContract.ProtocolVersion} " +
                "reason=capture_configuration_unavailable");
            throw new NotSupportedException(
                $"PanelDisplayDriver protocol v{PanelDisplayDriverContract.ProtocolVersion} " +
                "capture configuration is unavailable.");
        }

        _log.WriteLine(
            $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
            $"requiredProtocol=v{PanelDisplayDriverContract.ProtocolVersion}");
        uint configured;
        try
        {
            configured = await _configurationClient.SetCaptureTargetFpsAsync(
                checked((uint)targetFramesPerSecond),
                cancellationToken).ConfigureAwait(false);
        }
        catch (PanelDisplayDriverException exception)
        {
            var win32 = exception.Win32ErrorCode is { } error
                ? $"{error} (0x{error:X8})"
                : "n/a";
            _log.WriteLine(
                $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
                $"success=false Win32={win32} HRESULT=0x{exception.HResult:X8} " +
                $"operation={exception.Operation}");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.WriteLine(
                $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
                $"success=false Win32=n/a HRESULT=0x{exception.HResult:X8} " +
                $"exception={exception.GetType().Name}");
            throw;
        }

        if (configured != targetFramesPerSecond)
        {
            _log.WriteLine(
                $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
                $"success=false configured={configured} " +
                $"requiredProtocol=v{PanelDisplayDriverContract.ProtocolVersion}");
            throw new InvalidDataException(
                $"Driver configured unexpected capture target {configured} FPS.");
        }

        _log.WriteLine(
            $"[VideoTarget] sendingDriverTarget={targetFramesPerSecond} " +
            $"success=true configured={configured} " +
            $"protocol=v{PanelDisplayDriverContract.ProtocolVersion}");
    }

    public async Task<PanelDisplayCaptureMetrics?> GetCaptureMetricsAsync(
        CancellationToken cancellationToken)
    {
        if (_metricsClient is null)
        {
            return null;
        }

        return await _metricsClient.GetCaptureMetricsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<FrameSourceSessionBoundary> BeginSessionAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await _driverClient.GetFrameInfoAsync(
                0,
                cancellationToken).ConfigureAwait(false);
            switch (info.Result)
            {
                case PanelDisplayFrameResult.NoFrame:
                case PanelDisplayFrameResult.NoNewFrame:
                    return default;
                case PanelDisplayFrameResult.FrameReplaced:
                    continue;
                case PanelDisplayFrameResult.Ok:
                    ValidateAvailableFrame(info);
                    return new FrameSourceSessionBoundary(
                        info.Generation,
                        info.Sequence);
                default:
                    throw new InvalidDataException(
                        $"Unknown driver frame result {(uint)info.Result}.");
            }
        }

        throw new IOException(
            "Could not establish a stable driver frame boundary for the new session.");
    }

    public async Task<FrameSnapshot?> GetLatestFrameAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumReadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = await _driverClient.GetFrameInfoAsync(
                lastSequence,
                cancellationToken).ConfigureAwait(false);
            switch (info.Result)
            {
                case PanelDisplayFrameResult.NoFrame:
                case PanelDisplayFrameResult.NoNewFrame:
                    return null;
                case PanelDisplayFrameResult.FrameReplaced:
                    continue;
                case PanelDisplayFrameResult.Ok:
                    ValidateAvailableFrame(info);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unknown driver frame result {(uint)info.Result}.");
            }

            PanelDisplayDriverFrame frame;
            try
            {
                frame = await _driverClient.ReadFrameAsync(
                    info,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PanelDisplayDriverException exception)
                when (exception.Win32ErrorCode is
                    ErrorInsufficientBuffer or ErrorMoreData)
            {
                // The latest frame changed size between query and read. Query
                // again instead of treating normal producer progress as fatal.
                continue;
            }

            if (frame.Info.Result == PanelDisplayFrameResult.FrameReplaced)
            {
                continue;
            }

            if (frame.Info.Result is
                PanelDisplayFrameResult.NoFrame or
                PanelDisplayFrameResult.NoNewFrame)
            {
                return null;
            }

            ValidateAvailableFrame(frame.Info);
            if (frame.Info.Generation != info.Generation ||
                frame.Info.Sequence != info.Sequence)
            {
                throw new InvalidDataException(
                    "Driver returned a different frame without FrameReplaced.");
            }

            if (frame.Pixels.Length != checked((int)frame.Info.PayloadBytes))
            {
                throw new InvalidDataException(
                    "Driver frame payload length does not match its header.");
            }

            return new FrameSnapshot(
                frame.Info.Generation,
                frame.Info.Sequence,
                ToMicroseconds(
                    frame.Info.CaptureTimestampQpc,
                    frame.Info.QpcFrequency),
                checked((int)frame.Info.Width),
                checked((int)frame.Info.Height),
                checked((int)frame.Info.Stride),
                frame.Info.Format,
                frame.Pixels);
        }

        // A producer replacing the frame on every retry is benign. The next
        // bounded poll will try again without delaying the swapchain.
        return null;
    }

    private static void ValidateAvailableFrame(PanelDisplayFrameInfo info)
    {
        if (info.Result != PanelDisplayFrameResult.Ok ||
            info.Generation == 0 || info.Sequence == 0 ||
            info.Width == 0 || info.Height == 0 ||
            info.Width > 16_384 || info.Height > 16_384 ||
            info.Format is not (
                PanelDisplayFrameFormat.Bgra8888 or
                PanelDisplayFrameFormat.Rgba8888) ||
            info.QpcFrequency == 0)
        {
            throw new InvalidDataException("Driver returned invalid frame metadata.");
        }

        var minimumStride = checked((ulong)info.Width * 4);
        var expectedBytes = checked((ulong)info.Stride * info.Height);
        if (info.Stride < minimumStride ||
            info.PayloadBytes != expectedBytes ||
            expectedBytes > PanelDisplayDriverContract.MaximumFramePayloadBytes)
        {
            throw new InvalidDataException(
                "Driver returned an invalid frame stride or payload size.");
        }
    }

    private static ulong ToMicroseconds(ulong ticks, ulong frequency)
    {
        var value = (UInt128)ticks * 1_000_000 / frequency;
        if (value > ulong.MaxValue)
        {
            throw new InvalidDataException("Driver frame timestamp overflowed.");
        }

        return (ulong)value;
    }
}
