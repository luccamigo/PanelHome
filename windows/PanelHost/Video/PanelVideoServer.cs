using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelOS.DriverInterop;
using PanelHost.Services;
using PanelHost.Transport;

namespace PanelHost.Video;

internal sealed class PanelVideoServer
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly IPanelConnectionListener _listener;
    private readonly IDesktopService _desktopService;
    private readonly IFrameSource _frameSource;
    private readonly VideoPipelineOptions _options;
    private readonly IH264EncoderFactory _h264EncoderFactory;
    private readonly IVideoFrameConverter _frameConverter;
    private readonly VideoSessionLifetime? _sessionLifetime;
    private readonly IPanelVideoRuntimeObserver _runtimeObserver;
    private readonly TextWriter _log;
    private readonly ILogger<PanelVideoServer> _logger;
    private Task? _activeTask;
    private int _activeClient;

    internal PanelVideoServer(
        IDesktopService desktopService,
        IFrameSource frameSource,
        TextWriter? log = null,
        int port = PanelVideoProtocol.Port,
        VideoPipelineOptions? options = null,
        IH264EncoderFactory? h264EncoderFactory = null,
        IVideoFrameConverter? frameConverter = null,
        VideoSessionLifetime? videoSessionLifetime = null,
        IPanelConnectionListener? listener = null,
        IPanelVideoRuntimeObserver? runtimeObserver = null,
        ILogger<PanelVideoServer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(desktopService);
        ArgumentNullException.ThrowIfNull(frameSource);
        if (port is < 0 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _desktopService = desktopService;
        _frameSource = frameSource;
        _options = options ?? VideoPipelineOptions.Default;
        _h264EncoderFactory = h264EncoderFactory ??
            UnavailableH264EncoderFactory.Instance;
        _frameConverter = frameConverter ?? new BgraToNv12FrameConverter();
        _sessionLifetime = videoSessionLifetime;
        _runtimeObserver = runtimeObserver ?? NullPanelVideoRuntimeObserver.Instance;
        _log = log ?? Console.Out;
        _logger = logger ?? NullLogger<PanelVideoServer>.Instance;
        _listener = listener ?? new TcpPanelConnectionListener(PanelChannelId.Video, port);
        _log.WriteLine(
            $"[VideoTarget] hostPreferred={_options.PreferredSessionFps} " +
            "clientMax=awaiting_handshake effective=awaiting_handshake " +
            $"requiredDriverProtocol=v{PanelDisplayDriverContract.ProtocolVersion}");
    }

    internal IPEndPoint BoundEndpoint => new(IPAddress.Loopback, BoundPort);

    internal int BoundPort => _listener.BoundPort ?? throw new InvalidOperationException("Listener has not started.");

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
        _log.WriteLine(
            _listener.BoundPort is int port
                ? $"[Video] Listening only on 127.0.0.1:{port} (development transport: adb-tcp)."
                : "[Video] Waiting for native USB video channel.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IPanelConnection client;
                try
                {
                    client = await _listener.AcceptAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (Interlocked.CompareExchange(ref _activeClient, 1, 0) != 0)
                {
                    await RejectBusyAsync(client, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var activeTask = HandleReservedClientAsync(
                    client,
                    cancellationToken);
                Volatile.Write(ref _activeTask, activeTask);
            }
        }
        finally
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
            var activeTask = Volatile.Read(ref _activeTask);
            if (activeTask is not null)
            {
                try
                {
                    await activeTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            _log.WriteLine("[Video] Server stopped.");
        }
    }

    private async Task HandleReservedClientAsync(
        IPanelConnection client,
        CancellationToken serverCancellationToken)
    {
        try
        {
            await using (client)
            {
                await HandleClientAsync(client, serverCancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (serverCancellationToken.IsCancellationRequested ||
                _desktopService.State != DesktopServiceState.Running)
        {
        }
        catch (IOException exception)
        {
            _log.WriteLine($"[Video] Connection closed: {exception.Message}");
        }
        catch (PanelOS.DriverInterop.PanelDisplayDriverException exception)
        {
            _log.WriteLine(
                $"[Video] Driver frame read failed: {exception.Message}");
        }
        catch (Exception exception)
        {
            ProcessCrashDiagnostics.WriteException(
                _log,
                "PanelVideoServer session worker",
                exception);
        }
        finally
        {
            Interlocked.Exchange(ref _activeClient, 0);
            _log.WriteLine("[Video] Client disconnected.");
        }
    }

    private async Task HandleClientAsync(
        IPanelConnection client,
        CancellationToken serverCancellationToken)
    {
        var stream = client.Stream;
        var closeReason = new VideoSessionCloseReasonTracker();
        var runtimeSessionGeneration = 0L;
        using var serverCancellationRegistration = serverCancellationToken.Register(
            () => closeReason.TrySet(VideoSessionCloseReason.ServerShutdown));
        try
        {
        if (_desktopService.State != DesktopServiceState.Running)
        {
            closeReason.TrySet(VideoSessionCloseReason.DesktopStopped);
            await PanelVideoProtocol.WriteAsync(
                stream,
                PanelVideoProtocol.EncodeReject(
                    VideoRejectCode.DesktopNotRunning),
                serverCancellationToken).ConfigureAwait(false);
            return;
        }

        VideoHello hello;
        using (var handshakeCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(serverCancellationToken))
        {
            handshakeCancellation.CancelAfter(HandshakeTimeout);
            try
            {
                hello = await PanelVideoProtocol.ReadHelloAsync(
                    stream,
                    handshakeCancellation.Token).ConfigureAwait(false);
            }
            catch (VideoProtocolException exception)
            {
                closeReason.TrySet(VideoSessionCloseReason.ProtocolError);
                await PanelVideoProtocol.WriteAsync(
                    stream,
                    PanelVideoProtocol.EncodeReject(exception.RejectCode),
                    serverCancellationToken).ConfigureAwait(false);
                return;
            }
        }

        VideoAck ack;
        int effectiveSessionFps;
        VideoFrameRateNegotiation? h264FrameRate = null;
        try
        {
            ack = await NegotiateAsync(hello, serverCancellationToken)
                .ConfigureAwait(false);
            effectiveSessionFps = checked((int)ack.Fps);
            if (ack.SelectedCodec == VideoCodec.H264)
            {
                h264FrameRate = VideoFrameRatePolicy.Negotiate(
                    _options.PreferredSessionFps,
                    hello.MaxFps);
            }
        }
        catch (VideoProtocolException exception)
        {
            closeReason.TrySet(VideoSessionCloseReason.ProtocolError);
            await PanelVideoProtocol.WriteAsync(
                stream,
                PanelVideoProtocol.EncodeReject(exception.RejectCode),
                serverCancellationToken).ConfigureAwait(false);
            return;
        }

        using var sessionCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(serverCancellationToken);
        void StateChanged(object? sender, DesktopStateChangedEventArgs eventArgs)
        {
            if (eventArgs.State.State != DesktopServiceState.Running)
            {
                closeReason.TrySet(VideoSessionCloseReason.DesktopStopped);
                sessionCancellation.Cancel();
            }
        }

        _desktopService.StateChanged += StateChanged;
        void ControlDisconnected(object? sender, EventArgs eventArgs)
        {
            closeReason.TrySet(VideoSessionCloseReason.ControlDisconnected);
            sessionCancellation.Cancel();
        }
        if (_sessionLifetime is not null)
        {
            _sessionLifetime.ControlDisconnected += ControlDisconnected;
        }
        try
        {
            if (_desktopService.State != DesktopServiceState.Running)
            {
                closeReason.TrySet(VideoSessionCloseReason.DesktopStopped);
                return;
            }

            await PanelVideoProtocol.WriteAsync(
                stream,
                PanelVideoProtocol.EncodeAck(ack),
                sessionCancellation.Token).ConfigureAwait(false);
            _log.WriteLine(
                $"[Video] Client ready: {ack.MaxWidth}x{ack.MaxHeight} " +
                $"at up to {ack.Fps} FPS, {CodecName(ack.SelectedCodec)}.");
            var configuredFps = ack.SelectedCodec == VideoCodec.H264
                ? _options.PreferredSessionFps
                : _options.ZlibFps;
            runtimeSessionGeneration = _runtimeObserver.SessionConnected(
                ack,
                configuredFps);
            _logger.LogInformation(
                "Video session={Session} connected codec={Codec} width={Width} height={Height} fps={Fps}",
                runtimeSessionGeneration,
                CodecName(ack.SelectedCodec),
                ack.MaxWidth,
                ack.MaxHeight,
                ack.Fps);

            if (ack.SelectedCodec == VideoCodec.H264 &&
                effectiveSessionFps is 30 or 45 or 60 &&
                _frameSource is IFrameSourceCaptureConfiguration captureConfiguration)
            {
                await captureConfiguration.ConfigureCaptureTargetFpsAsync(
                    effectiveSessionFps,
                    sessionCancellation.Token).ConfigureAwait(false);
            }

            if (h264FrameRate is { } frameRate)
            {
                _logger.LogInformation(
                    "Video frame rate negotiated for session={Session}: fps={Fps} clientMax={ClientMax} effectiveFps={EffectiveFps}",
                    runtimeSessionGeneration,
                    frameRate.HostPreferredFps,
                    frameRate.ClientMaxFps,
                    frameRate.EffectiveSessionFps);
                _log.WriteLine(
                    $"[VideoTarget] hostPreferred={frameRate.HostPreferredFps} " +
                    $"clientMax={frameRate.ClientMaxFps} " +
                    $"effective={frameRate.EffectiveSessionFps} " +
                    $"driverTarget={frameRate.EffectiveSessionFps} " +
                    $"encoderFps={frameRate.EffectiveSessionFps} " +
                    $"clamp={(frameRate.IsClientLimited ? "client_max" : "none")}");
            }

            var metrics = new VideoMetrics(
                _log,
                configuredFps,
                effectiveSessionFps,
                _runtimeObserver,
                runtimeSessionGeneration);
            await metrics.InitializeCaptureAsync(
                _frameSource,
                sessionCancellation.Token).ConfigureAwait(false);
            var frameBoundary = await BeginFrameSessionAsync(
                _frameSource,
                sessionCancellation.Token).ConfigureAwait(false);
            if (frameBoundary.LastSequence != 0)
            {
                _log.WriteLine(
                    $"[Video] Session frame boundary: " +
                    $"generation={frameBoundary.Generation} " +
                    $"sequence={frameBoundary.LastSequence}.");
            }
            var diagnostics = new VideoFrameDiagnostics(_log);

            // Both pipelines poll, and the poll cadence depends on this
            // process's timer resolution rather than the system's. Held for the
            // session and released with it, because the raised resolution costs
            // power process-wide.
            using var timerResolution = TimerResolutionScope.Acquire(
                VideoFramePollingCadence.PollsPerSecond(effectiveSessionFps),
                _log);
            var videoPipeline = ack.SelectedCodec == VideoCodec.H264
                ? RunH264PipelineAsync(
                    stream,
                    ack,
                    effectiveSessionFps,
                    metrics,
                    diagnostics,
                    frameBoundary.LastSequence,
                    closeReason,
                    sessionCancellation.Token)
                : RunZlibPipelineAsync(
                    stream,
                    ack,
                    effectiveSessionFps,
                    metrics,
                    diagnostics,
                    frameBoundary.LastSequence,
                    closeReason,
                    sessionCancellation.Token);
            var disconnectWatcher = WatchDisconnectAsync(
                stream,
                closeReason,
                sessionCancellation.Token);

            await Task.WhenAny(videoPipeline, disconnectWatcher)
                .ConfigureAwait(false);
            sessionCancellation.Cancel();
            try
            {
                await Task.WhenAll(videoPipeline, disconnectWatcher)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (sessionCancellation.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _desktopService.StateChanged -= StateChanged;
            if (_sessionLifetime is not null)
            {
                _sessionLifetime.ControlDisconnected -= ControlDisconnected;
            }
        }
        }
        catch (OperationCanceledException)
            when (serverCancellationToken.IsCancellationRequested)
        {
            closeReason.TrySet(VideoSessionCloseReason.ServerShutdown);
            throw;
        }
        catch (OperationCanceledException)
        {
            closeReason.TrySet(VideoSessionCloseReason.Cancellation);
            throw;
        }
        catch (VideoProtocolException)
        {
            closeReason.TrySet(VideoSessionCloseReason.ProtocolError);
            throw;
        }
        catch (Exception exception)
            when (exception is IOException)
        {
            closeReason.TrySet(VideoSessionCloseReason.TransportFailure);
            throw;
        }
        finally
        {
            if (runtimeSessionGeneration != 0)
            {
                _runtimeObserver.SessionDisconnected(runtimeSessionGeneration);
            }
            _log.WriteLine(
                $"[Video] Session closing: reason={closeReason.Reason}");
            _logger.LogInformation(
                "Video session={Session} closing with errorCode={ErrorCode}",
                runtimeSessionGeneration,
                closeReason.Reason);
        }
    }

    private async Task<VideoAck> NegotiateAsync(
        VideoHello hello,
        CancellationToken cancellationToken)
    {
        var supportsH264 =
            (hello.SupportedCodecs & VideoCodecMask.H264) != 0;
        var supportsZlib =
            (hello.SupportedCodecs & VideoCodecMask.ZlibBgra8888) != 0;

        var h264Descriptor = supportsH264 &&
            _options.CodecPreference != VideoCodecPreference.Zlib
                ? await _h264EncoderFactory.ProbeAsync(cancellationToken)
                    .ConfigureAwait(false)
                : null;

        var selectedCodec = _options.CodecPreference switch
        {
            VideoCodecPreference.H264 when supportsH264 && h264Descriptor is not null =>
                VideoCodec.H264,
            VideoCodecPreference.H264 => throw new VideoProtocolException(
                VideoRejectCode.UnsupportedCodec,
                "H.264 was required but is unavailable for this client/session."),
            VideoCodecPreference.Zlib when supportsZlib => VideoCodec.ZlibBgra8888,
            VideoCodecPreference.Zlib => throw new VideoProtocolException(
                VideoRejectCode.UnsupportedCodec,
                "ZLIB_BGRA8888 was required but is unavailable for this client."),
            _ when supportsH264 && h264Descriptor is not null => VideoCodec.H264,
            _ when supportsZlib => VideoCodec.ZlibBgra8888,
            _ => throw new VideoProtocolException(
                VideoRejectCode.UnsupportedCodec,
                "No mutually supported video codec is available."),
        };

        if (selectedCodec == VideoCodec.ZlibBgra8888 &&
            supportsH264 && h264Descriptor is null &&
            _options.CodecPreference == VideoCodecPreference.Auto)
        {
            _log.WriteLine(
                "[Video] H.264 encoder unavailable; negotiated ZLIB_BGRA8888 fallback.");
        }

        if (selectedCodec == VideoCodec.H264)
        {
            var width = Math.Min(hello.MaxWidth, checked((uint)_options.H264MaximumWidth));
            var height = Math.Min(hello.MaxHeight, checked((uint)_options.H264MaximumHeight));
            width &= ~1u;
            height &= ~1u;
            if (width < 2 || height < 2)
            {
                throw new VideoProtocolException(
                    VideoRejectCode.InvalidHello,
                    "H.264 requires even dimensions of at least 2x2.");
            }

            return new VideoAck(
                selectedCodec,
                width,
                height,
                checked((uint)VideoFrameRatePolicy.Negotiate(
                    _options.PreferredSessionFps,
                    hello.MaxFps).EffectiveSessionFps));
        }

        return new VideoAck(
            selectedCodec,
            Math.Min(hello.MaxWidth, checked((uint)_options.ZlibMaximumWidth)),
            Math.Min(hello.MaxHeight, checked((uint)_options.ZlibMaximumHeight)),
            Math.Min(hello.MaxFps, checked((uint)_options.ZlibFps)));
    }

    private async Task RunZlibPipelineAsync(
        Stream stream,
        VideoAck ack,
        int effectiveSessionFps,
        VideoMetrics metrics,
        VideoFrameDiagnostics diagnostics,
        ulong initialSequence,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        using var mailbox = new LatestFrameMailbox<EncodedVideoFrame>();
        using var pipelineCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceFramesAsync(
            mailbox,
            metrics,
            diagnostics,
            checked((int)ack.MaxWidth),
            checked((int)ack.MaxHeight),
            effectiveSessionFps,
            initialSequence,
            pipelineCancellation.Token);
        var consumer = SendFramesAsync(
            stream,
            mailbox,
            metrics,
            closeReason,
            pipelineCancellation.Token);
        var completed = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested)
        {
            closeReason.TrySet(
                completed == producer
                    ? VideoSessionCloseReason.ProducerCompleted
                    : VideoSessionCloseReason.ConsumerCompleted);
        }
        pipelineCancellation.Cancel();
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    private async Task RunH264PipelineAsync(
        Stream stream,
        VideoAck ack,
        int effectiveSessionFps,
        VideoMetrics metrics,
        VideoFrameDiagnostics diagnostics,
        ulong initialSequence,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        _log.WriteLine(
            $"[VideoPacing] targetFps={effectiveSessionFps} " +
            $"hostPollTargetFps={VideoFramePollingCadence.PollsPerSecond(
                effectiveSessionFps)} latestFrameWins=true");
        using var mailbox = new LatestFrameMailbox<FrameSnapshot>();
        using var pipelineCancellation = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceRawFramesAsync(
            mailbox,
            metrics,
            diagnostics,
            effectiveSessionFps,
            initialSequence,
            pipelineCancellation.Token);
        var consumer = EncodeAndSendH264Async(
            stream,
            mailbox,
            metrics,
            checked((int)ack.MaxWidth),
            checked((int)ack.MaxHeight),
            effectiveSessionFps,
            closeReason,
            pipelineCancellation.Token);
        var completed = await Task.WhenAny(producer, consumer).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested)
        {
            closeReason.TrySet(
                completed == producer
                    ? VideoSessionCloseReason.ProducerCompleted
                    : VideoSessionCloseReason.ConsumerCompleted);
        }
        pipelineCancellation.Cancel();
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    private async Task ProduceRawFramesAsync(
        LatestFrameMailbox<FrameSnapshot> mailbox,
        VideoMetrics metrics,
        VideoFrameDiagnostics diagnostics,
        int fps,
        ulong initialSequence,
        CancellationToken cancellationToken)
    {
        var lastSequence = initialSequence;
        using var timer = new PeriodicTimer(VideoFramePollingCadence.Interval(fps));
        while (!cancellationToken.IsCancellationRequested &&
            _desktopService.State == DesktopServiceState.Running)
        {
            var driverReadStarted = Stopwatch.GetTimestamp();
            var frame = await _frameSource.GetLatestFrameAsync(
                lastSequence,
                cancellationToken).ConfigureAwait(false);
            metrics.DriverPollCompleted(driverReadStarted, frame?.Sequence);
            if (frame is not null && frame.Sequence > lastSequence)
            {
                metrics.FrameReceived(
                    frame.Sequence,
                    lastSequence,
                    frame.CaptureTimestampMicros);
                lastSequence = frame.Sequence;
                LogRawDiagnostics(diagnostics, frame);
                var droppedBefore = mailbox.Dropped;
                mailbox.Publish(frame);
                metrics.MailboxDropped(mailbox.Dropped - droppedBefore);
            }
            else if (frame is not null)
            {
                metrics.MailboxDropped(1);
            }

            await metrics.MaybeLogAsync(
                _frameSource,
                cancellationToken).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task EncodeAndSendH264Async(
        Stream stream,
        LatestFrameMailbox<FrameSnapshot> mailbox,
        VideoMetrics metrics,
        int maximumWidth,
        int maximumHeight,
        int fps,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        IH264Encoder? encoder = null;
        var lastMailboxDropped = 0L;
        var droppedSinceKeyFrame = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await mailbox.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                var dimensions = VideoFrameProcessor.FitInside(
                    frame.Width,
                    frame.Height,
                    maximumWidth,
                    maximumHeight);
                var settings = _options.CreateH264Settings(
                    dimensions.Width,
                    dimensions.Height,
                    fps);
                if (encoder is null || encoder.Settings != settings)
                {
                    if (encoder is not null)
                    {
                        await encoder.DisposeAsync().ConfigureAwait(false);
                    }

                    try
                    {
                        encoder = await _h264EncoderFactory.CreateAsync(
                            settings,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        closeReason.TrySet(VideoSessionCloseReason.EncoderFailure);
                        throw;
                    }
                    metrics.ConfigureH264(encoder.Descriptor, settings);
                    _log.WriteLine(
                        $"[Video] H.264 session: encoder={encoder.Descriptor.Name} " +
                        $"hardware={encoder.Descriptor.IsHardware} " +
                        $"resolution={settings.Width}x{settings.Height} " +
                        $"fps={settings.Fps} bitrate={settings.Bitrate / 1_000_000d:F1} Mbps.");
                    droppedSinceKeyFrame = fps;
                }

                var dropped = mailbox.Dropped;
                if (dropped > lastMailboxDropped)
                {
                    droppedSinceKeyFrame += dropped - lastMailboxDropped;
                    lastMailboxDropped = dropped;
                }

                var forceKeyFrame = droppedSinceKeyFrame >= Math.Max(1, fps / 2);
                Nv12VideoFrame converted;
                try
                {
                    var conversionStarted = Stopwatch.GetTimestamp();
                    converted = _frameConverter.Convert(
                        frame,
                        settings.Width,
                        settings.Height);
                    metrics.Nv12Converted(conversionStarted);
                    metrics.FrameBeforeEncode(converted.CaptureTimestampMicros);
                }
                catch (Exception exception)
                {
                    LogFrameStageFailure("nv12", frame.Sequence, exception);
                    throw;
                }

                IReadOnlyList<H264EncodedAccessUnit> accessUnits;
                try
                {
                    accessUnits = await encoder.EncodeAsync(
                        converted,
                        forceKeyFrame,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    closeReason.TrySet(VideoSessionCloseReason.EncoderFailure);
                    throw;
                }
                foreach (var accessUnit in accessUnits)
                {
                    metrics.AccessUnitEncoded(accessUnit);
                    if (accessUnit.IsKeyFrame)
                    {
                        droppedSinceKeyFrame = 0;
                        if (accessUnit.DecoderConfiguration is { Length: > 0 } configuration)
                        {
                            await SendH264PacketAsync(
                                stream,
                                new H264VideoPacket(
                                    accessUnit.Sequence,
                                    accessUnit.CaptureTimestampMicros,
                                    H264PacketFlags.Config,
                                    configuration,
                                    accessUnit.EncodeDurationMicros),
                                metrics,
                                countFrame: false,
                                closeReason,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }

                    var flags = H264PacketFlags.EndOfAccessUnit;
                    if (accessUnit.IsKeyFrame)
                    {
                        flags |= H264PacketFlags.KeyFrame;
                    }

                    await SendH264PacketAsync(
                        stream,
                        new H264VideoPacket(
                            accessUnit.Sequence,
                            accessUnit.CaptureTimestampMicros,
                            flags,
                            accessUnit.AnnexB,
                            accessUnit.EncodeDurationMicros),
                        metrics,
                        countFrame: true,
                        closeReason,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (encoder is not null)
            {
                await encoder.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task SendH264PacketAsync(
        Stream stream,
        H264VideoPacket packet,
        VideoMetrics metrics,
        bool countFrame,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        var preparationStarted = Stopwatch.GetTimestamp();
        if (countFrame)
        {
            metrics.FrameBeforeSocket(packet.CaptureTimestampMicros);
        }
        var header = PanelVideoProtocol.EncodeH264PacketHeader(packet);
        metrics.PacketPrepared(preparationStarted);
        var socketWriteStarted = Stopwatch.GetTimestamp();
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is IOException)
        {
            closeReason.TrySet(VideoSessionCloseReason.TransportFailure);
            throw;
        }
        metrics.SocketWriteCompleted(socketWriteStarted);
        metrics.PacketSent(
            header.Length + packet.Payload.Length,
            countFrame,
            packet.EncodeDurationMicros,
            packet.CaptureTimestampMicros);
    }

    private static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H264_ANNEX_B",
        VideoCodec.ZlibBgra8888 => "ZLIB_BGRA8888",
        _ => $"UNKNOWN_{(uint)codec}",
    };

    private static async Task<FrameSourceSessionBoundary> BeginFrameSessionAsync(
        IFrameSource source,
        CancellationToken cancellationToken) =>
        source is IFrameSourceSessionBoundary sessionBoundary
            ? await sessionBoundary.BeginSessionAsync(cancellationToken)
                .ConfigureAwait(false)
            : default;

    private async Task ProduceFramesAsync(
        LatestFrameMailbox<EncodedVideoFrame> mailbox,
        VideoMetrics metrics,
        VideoFrameDiagnostics diagnostics,
        int maxWidth,
        int maxHeight,
        int fps,
        ulong initialSequence,
        CancellationToken cancellationToken)
    {
        var lastSequence = initialSequence;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / fps));
        while (!cancellationToken.IsCancellationRequested &&
            _desktopService.State == DesktopServiceState.Running)
        {
            var driverReadStarted = Stopwatch.GetTimestamp();
            var frame = await _frameSource.GetLatestFrameAsync(
                lastSequence,
                cancellationToken).ConfigureAwait(false);
            metrics.DriverPollCompleted(driverReadStarted, frame?.Sequence);
            if (frame is not null)
            {
                if (frame.Sequence > lastSequence)
                {
                    metrics.FrameReceived(
                        frame.Sequence,
                        lastSequence,
                        frame.CaptureTimestampMicros);
                    lastSequence = frame.Sequence;
                    LogRawDiagnostics(diagnostics, frame);
                    VideoPreviewFrame preview;
                    try
                    {
                        preview = VideoFrameProcessor.CreatePreview(
                            frame,
                            maxWidth,
                            maxHeight);
                    }
                    catch (Exception exception)
                    {
                        LogFrameStageFailure(
                            "normalize_resize",
                            frame.Sequence,
                            exception);
                        throw;
                    }

                    LogPreviewDiagnostics(diagnostics, preview);
                    EncodedVideoFrame encoded;
                    try
                    {
                        encoded = VideoFrameProcessor.EncodePreview(preview);
                    }
                    catch (Exception exception)
                    {
                        LogFrameStageFailure("zlib", frame.Sequence, exception);
                        throw;
                    }

                    var droppedBefore = mailbox.Dropped;
                    mailbox.Publish(encoded);
                    metrics.MailboxDropped(mailbox.Dropped - droppedBefore);
                }
                else
                {
                    metrics.MailboxDropped(1);
                }
            }

            await metrics.MaybeLogAsync(
                _frameSource,
                cancellationToken).ConfigureAwait(false);
            if (!await timer.WaitForNextTickAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private void LogRawDiagnostics(
        VideoFrameDiagnostics diagnostics,
        FrameSnapshot frame)
    {
        try
        {
            diagnostics.LogRaw(frame);
        }
        catch (Exception exception)
        {
            LogFrameStageFailure("host_raw_diagnostics", frame.Sequence, exception);
        }
    }

    private void LogPreviewDiagnostics(
        VideoFrameDiagnostics diagnostics,
        VideoPreviewFrame frame)
    {
        try
        {
            diagnostics.LogPreview(frame);
        }
        catch (Exception exception)
        {
            LogFrameStageFailure("host_preview_diagnostics", frame.Sequence, exception);
        }
    }

    private void LogFrameStageFailure(
        string stage,
        ulong sequence,
        Exception exception)
    {
        _log.WriteLine(
            $"[Video] Frame processing failed: sequence={sequence} " +
            $"stage={stage} type={exception.GetType().Name} " +
            $"message={exception.Message}");
    }

    private static async Task SendFramesAsync(
        Stream stream,
        LatestFrameMailbox<EncodedVideoFrame> mailbox,
        VideoMetrics metrics,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await mailbox.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            var preparationStarted = Stopwatch.GetTimestamp();
            var header = PanelVideoProtocol.EncodeFrameHeader(frame);
            metrics.PacketPrepared(preparationStarted);
            var socketWriteStarted = Stopwatch.GetTimestamp();
            try
            {
                await stream.WriteAsync(header, cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync(frame.CompressedPixels, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is IOException)
            {
                closeReason.TrySet(VideoSessionCloseReason.TransportFailure);
                throw;
            }
            metrics.SocketWriteCompleted(socketWriteStarted);
            metrics.FrameBeforeSocket(frame.CaptureTimestampMicros);
            metrics.FrameSent(
                header.Length + frame.CompressedPixels.Length,
                frame.CaptureTimestampMicros);
        }
    }

    private static async Task WatchDisconnectAsync(
        Stream stream,
        VideoSessionCloseReasonTracker closeReason,
        CancellationToken cancellationToken)
    {
        var unexpectedInput = new byte[1];
        try
        {
            while (await stream.ReadAsync(unexpectedInput, cancellationToken)
                    .ConfigureAwait(false) != 0)
            {
                // The v1 client has no messages after hello. Ignore stray bytes,
                // but keep watching so a quiet connection close releases the slot.
            }
            closeReason.TrySet(VideoSessionCloseReason.ClientFin);
        }
        catch (Exception exception)
            when (exception is IOException)
        {
            closeReason.TrySet(VideoSessionCloseReason.TransportFailure);
            throw;
        }
    }

    private static async Task RejectBusyAsync(
        IPanelConnection client,
        CancellationToken serverCancellationToken)
    {
        await using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            serverCancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await PanelVideoProtocol.WriteAsync(
                    client.Stream,
                    PanelVideoProtocol.EncodeReject(VideoRejectCode.Busy),
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException or OperationCanceledException)
            {
            }
        }
    }

    private sealed class VideoMetrics
    {
        private static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(5);

        private readonly TextWriter _log;
        private readonly ulong _sessionStartedMicros =
            VideoSessionLatency.GetCurrentTimestampMicros();
        private readonly Stopwatch _interval = Stopwatch.StartNew();
        private readonly PerformanceTimingWindow _driverRead = new();
        private readonly PerformanceTimingWindow _convertNv12 = new();
        private readonly PerformanceTimingWindow _encodeInputCopy = new();
        private readonly PerformanceTimingWindow _encodeInput = new();
        private readonly PerformanceTimingWindow _encodeOutput = new();
        private readonly PerformanceTimingWindow _encodeOutputCopy = new();
        private readonly PerformanceTimingWindow _packetPreparation = new();
        private readonly PerformanceTimingWindow _socketWrite = new();
        private readonly PerformanceTimingWindow _frameInterval = new();
        private readonly PerformanceTimingWindow _frameJitter = new();
        private readonly PerformanceTimingWindow _frameAgeBeforeEncode = new();
        private readonly PerformanceTimingWindow _frameAgeBeforeSocket = new();
        private readonly PerformanceTimingWindow _captureToSend = new();
        private readonly int _targetFps;
        private readonly int _configuredFps;
        private readonly IPanelVideoRuntimeObserver _runtimeObserver;
        private readonly long _runtimeSessionGeneration;
        private long _captured;
        private long _driverPolls;
        private long _noNewFramePolls;
        private long _received;
        private long _dropped;
        private long _sequenceGap;
        private long _sent;
        private long _bytes;
        private long _encoded;
        private long _encodeMicros;
        private ulong _lastCaptureTimestampMicros;
        private ulong _latestDriverPublishedSequence;
        private ulong _latestHostReadSequence;
        private string _codec = "ZLIB_BGRA8888";
        private string? _encoderKind;
        private string? _encoderName;
        private H264EncoderSettings? _settings;
        private PanelDisplayCaptureMetrics? _captureBaseline;

        internal VideoMetrics(
            TextWriter log,
            int configuredFps,
            int targetFps,
            IPanelVideoRuntimeObserver runtimeObserver,
            long runtimeSessionGeneration)
        {
            _log = log;
            _configuredFps = configuredFps;
            _targetFps = targetFps;
            _runtimeObserver = runtimeObserver;
            _runtimeSessionGeneration = runtimeSessionGeneration;
        }

        internal async Task InitializeCaptureAsync(
            IFrameSource source,
            CancellationToken cancellationToken)
        {
            _captureBaseline = await ReadCaptureMetricsAsync(
                source,
                cancellationToken).ConfigureAwait(false);
            if (_captureBaseline is { } capture)
            {
                _log.WriteLine(
                    $"[VideoTarget] effectiveSessionFps={_targetFps} " +
                    $"driverConfigured={capture.TargetFramesPerSecond} " +
                    $"scheduler={capture.SchedulerTargetFramesPerSecond}");
            }
        }

        internal void DriverPollCompleted(
            long startedTimestamp,
            ulong? driverPublishedSequence)
        {
            _driverRead.RecordElapsed(startedTimestamp);
            Interlocked.Increment(ref _driverPolls);
            if (driverPublishedSequence is { } sequence)
            {
                _latestDriverPublishedSequence = sequence;
            }
            else
            {
                Interlocked.Increment(ref _noNewFramePolls);
            }
        }

        internal void Nv12Converted(long startedTimestamp) =>
            _convertNv12.RecordElapsed(startedTimestamp);

        internal void AccessUnitEncoded(H264EncodedAccessUnit accessUnit)
        {
            if (accessUnit.InputSampleCopyDurationMicros > 0)
            {
                _encodeInputCopy.RecordMicroseconds(
                    accessUnit.InputSampleCopyDurationMicros);
            }
            if (accessUnit.ProcessInputDurationMicros > 0)
            {
                _encodeInput.RecordMicroseconds(
                    accessUnit.ProcessInputDurationMicros);
            }
            if (accessUnit.ProcessOutputDurationMicros > 0)
            {
                _encodeOutput.RecordMicroseconds(
                    accessUnit.ProcessOutputDurationMicros);
            }
            if (accessUnit.OutputSampleCopyDurationMicros > 0)
            {
                _encodeOutputCopy.RecordMicroseconds(
                    accessUnit.OutputSampleCopyDurationMicros);
            }
        }

        internal void PacketPrepared(long startedTimestamp) =>
            _packetPreparation.RecordElapsed(startedTimestamp);

        internal void SocketWriteCompleted(long startedTimestamp) =>
            _socketWrite.RecordElapsed(startedTimestamp);

        internal void FrameReceived(
            ulong sequence,
            ulong previousSequence,
            ulong captureTimestampMicros)
        {
            Interlocked.Increment(ref _received);
            _latestHostReadSequence = sequence;
            var captured = previousSequence == 0 || sequence <= previousSequence
                ? 1UL
                : sequence - previousSequence;
            Interlocked.Add(
                ref _captured,
                checked((long)Math.Min(captured, (ulong)long.MaxValue)));
            if (captured > 1)
            {
                Interlocked.Add(
                    ref _sequenceGap,
                    checked((long)Math.Min(captured - 1, (ulong)long.MaxValue)));
                Interlocked.Add(
                    ref _dropped,
                    checked((long)Math.Min(captured - 1, (ulong)long.MaxValue)));
            }

            if (_lastCaptureTimestampMicros != 0 &&
                captureTimestampMicros > _lastCaptureTimestampMicros)
            {
                var intervalMicros = captureTimestampMicros -
                    _lastCaptureTimestampMicros;
                if (intervalMicros <= 1_000_000)
                {
                    _frameInterval.RecordMicroseconds(
                        checked((long)intervalMicros));
                    var targetIntervalMicros = 1_000_000L / _targetFps;
                    _frameJitter.RecordMicroseconds(Math.Abs(
                        checked((long)intervalMicros) - targetIntervalMicros));
                }
            }
            _lastCaptureTimestampMicros = captureTimestampMicros;
        }

        internal void FrameBeforeEncode(ulong captureTimestampMicros) =>
            RecordFrameAge(_frameAgeBeforeEncode, captureTimestampMicros);

        internal void FrameBeforeSocket(ulong captureTimestampMicros) =>
            RecordFrameAge(_frameAgeBeforeSocket, captureTimestampMicros);

        internal void MailboxDropped(long count)
        {
            if (count > 0)
            {
                Interlocked.Add(ref _dropped, count);
            }
        }

        internal void FrameSent(int bytes, ulong captureTimestampMicros)
        {
            PacketSent(
                bytes,
                countFrame: true,
                encodeDurationMicros: 0,
                captureTimestampMicros);
        }

        internal void ConfigureH264(
            H264EncoderDescriptor descriptor,
            H264EncoderSettings settings)
        {
            _codec = "H264";
            _encoderKind = descriptor.IsHardware ? "hardware" : "software";
            _encoderName = descriptor.Name;
            _settings = settings;
        }

        internal void PacketSent(
            int bytes,
            bool countFrame,
            long encodeDurationMicros,
            ulong captureTimestampMicros)
        {
            if (countFrame)
            {
                Interlocked.Increment(ref _sent);
                if (encodeDurationMicros > 0)
                {
                    Interlocked.Increment(ref _encoded);
                    Interlocked.Add(ref _encodeMicros, encodeDurationMicros);
                }

                var sendTimestampMicros =
                    VideoSessionLatency.GetCurrentTimestampMicros();
                if (VideoSessionLatency.TryCalculate(
                        _sessionStartedMicros,
                        captureTimestampMicros,
                        sendTimestampMicros,
                        out var latencyMicros))
                {
                    _captureToSend.RecordMicroseconds(latencyMicros);
                }
            }

            Interlocked.Add(ref _bytes, bytes);
        }

        internal async Task MaybeLogAsync(
            IFrameSource source,
            CancellationToken cancellationToken)
        {
            if (_interval.Elapsed < LogInterval)
            {
                return;
            }

            var seconds = _interval.Elapsed.TotalSeconds;
            var captureMetrics = await ReadCaptureMetricsAsync(
                source,
                cancellationToken).ConfigureAwait(false);
            var captured = Interlocked.Exchange(ref _captured, 0);
            var driverPolls = Interlocked.Exchange(ref _driverPolls, 0);
            var noNewFramePolls = Interlocked.Exchange(ref _noNewFramePolls, 0);
            var received = Interlocked.Exchange(ref _received, 0);
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            var sequenceGap = Interlocked.Exchange(ref _sequenceGap, 0);
            var sent = Interlocked.Exchange(ref _sent, 0);
            var bytes = Interlocked.Exchange(ref _bytes, 0);
            var encoded = Interlocked.Exchange(ref _encoded, 0);
            var encodeMicros = Interlocked.Exchange(ref _encodeMicros, 0);
            var driverRead = _driverRead.Drain();
            var convertNv12 = _convertNv12.Drain();
            var encodeInputCopy = _encodeInputCopy.Drain();
            var encodeInput = _encodeInput.Drain();
            var encodeOutput = _encodeOutput.Drain();
            var encodeOutputCopy = _encodeOutputCopy.Drain();
            var packetPreparation = _packetPreparation.Drain();
            var socketWrite = _socketWrite.Drain();
            var frameInterval = _frameInterval.Drain();
            var frameJitter = _frameJitter.Drain();
            var frameAgeBeforeEncode = _frameAgeBeforeEncode.Drain();
            var frameAgeBeforeSocket = _frameAgeBeforeSocket.Drain();
            var captureToSend = _captureToSend.Drain();
            _interval.Restart();
            var prefix = _settings is null
                ? $"codec={_codec}"
                : $"codec={_codec} encoder={_encoderKind} " +
                    $"resolution={_settings.Width}x{_settings.Height} " +
                    $"fps={_settings.Fps} " +
                    $"bitrate={_settings.Bitrate / 1_000_000d:F1}Mbps";
            var encode = encoded == 0
                ? string.Empty
                : $" encode={encodeMicros / 1000d / encoded:F1}ms";
            _runtimeObserver.MetricsUpdated(
                _runtimeSessionGeneration,
                new PanelVideoRuntimeMetrics(
                    _codec,
                    _encoderName,
                    _settings?.Width,
                    _settings?.Height,
                    _configuredFps,
                    _targetFps,
                    captured / seconds,
                    received / seconds,
                    sent / seconds,
                    bytes / seconds / (1024 * 1024),
                    encoded == 0 ? null : encodeMicros / 1000d / encoded,
                    captureToSend.Count == 0
                        ? null
                        : captureToSend.AverageMilliseconds,
                    sequenceGap));
            _log.WriteLine(
                $"[VideoPerf] {prefix} driverCapture={captured / seconds:F1} fps " +
                $"hostReceive={received / seconds:F1} fps " +
                $"send={sent / seconds:F1} fps drop={dropped} " +
                $"bandwidth={bytes / seconds / (1024 * 1024):F2} MB/s" +
                encode);
            _log.WriteLine(
                $"[VideoPerf] {driverRead.Format("driverRead")} " +
                $"{convertNv12.Format("convertNV12")} " +
                $"{encodeInputCopy.Format("encodeInputCopy")} " +
                $"{encodeInput.Format("encodeInput")} " +
                $"{encodeOutput.Format("encodeOutput")} " +
                $"{encodeOutputCopy.Format("encodeOutputCopy")} " +
                $"{packetPreparation.Format("packetPrep")} " +
                socketWrite.Format("socketWrite"));
            _log.WriteLine(
                $"[VideoPacing] targetFps={_targetFps} " +
                $"actualHostReceiveFps={received / seconds:F1} " +
                $"hostPollFps={driverPolls / seconds:F1} " +
                $"noNewFramePolls={noNewFramePolls} " +
                $"{frameInterval.Format("frameInterval")} " +
                $"{frameJitter.Format("jitter")} " +
                $"{frameAgeBeforeEncode.Format("frameAgeBeforeEncode")} " +
                $"{frameAgeBeforeSocket.Format("frameAgeBeforeSocket")} " +
                captureToSend.Format("captureToSend"));
            _log.WriteLine(
                $"[VideoSequence] " +
                $"driverPublishedSequence={_latestDriverPublishedSequence} " +
                $"hostReadSequence={_latestHostReadSequence} " +
                $"sequenceGap={sequenceGap}");

            LogCaptureMetrics(captureMetrics, seconds);
            _captureBaseline = captureMetrics;
        }

        private async Task<PanelDisplayCaptureMetrics?> ReadCaptureMetricsAsync(
            IFrameSource source,
            CancellationToken cancellationToken)
        {
            if (source is not IFrameSourceCaptureMetrics metricsSource)
            {
                return null;
            }

            try
            {
                return await metricsSource.GetCaptureMetricsAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log.WriteLine(
                    $"[VideoCapture] Metrics unavailable: " +
                    $"{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private void RecordFrameAge(
            PerformanceTimingWindow window,
            ulong captureTimestampMicros)
        {
            var nowMicros = VideoSessionLatency.GetCurrentTimestampMicros();
            if (VideoSessionLatency.TryCalculate(
                    _sessionStartedMicros,
                    captureTimestampMicros,
                    nowMicros,
                    out var ageMicros))
            {
                window.RecordMicroseconds(ageMicros);
            }
        }

        private void LogCaptureMetrics(
            PanelDisplayCaptureMetrics? currentValue,
            double seconds)
        {
            if (_captureBaseline is not { } previous ||
                currentValue is not { } current ||
                current.Generation == 0 ||
                current.Generation != previous.Generation ||
                current.QpcFrequency == 0 ||
                !CountersAreMonotonic(previous, current))
            {
                return;
            }

            var acquired = current.SwapchainAcquired - previous.SwapchainAcquired;
            var throttle = current.CaptureThrottleSkipped -
                previous.CaptureThrottleSkipped;
            var copies = current.CopySubmitted - previous.CopySubmitted;
            var mapSuccess = current.MapSuccess - previous.MapSuccess;
            var mapBusy = current.MapBusy - previous.MapBusy;
            var mapFailure = current.MapFailure - previous.MapFailure;
            var published = current.Published - previous.Published;
            var publishDropped = current.PublishDropped - previous.PublishDropped;
            var scheduledFrames = current.ScheduledFrames -
                previous.ScheduledFrames;
            var missedDeadlines = current.MissedDeadlines -
                previous.MissedDeadlines;
            var latenessSamples = current.DeadlineLatenessSamples -
                previous.DeadlineLatenessSamples;
            var latenessQpc = current.DeadlineLatenessQpcTotal -
                previous.DeadlineLatenessQpcTotal;
            var mapAttempts = mapSuccess + mapBusy + mapFailure;
            var mapBusyPercent = mapAttempts == 0
                ? 0d
                : mapBusy * 100d / mapAttempts;
            _log.WriteLine(
                $"[VideoCapture] targetFps={current.TargetFramesPerSecond} " +
                $"actualPublishedFps={published / seconds:F1} " +
                $"swapAcquire={acquired / seconds:F1}fps " +
                $"throttleSkipped={throttle / seconds:F1}fps " +
                $"copySubmitted={copies / seconds:F1}fps " +
                $"mapSuccess={mapSuccess / seconds:F1}fps " +
                $"mapBusy={mapBusy / seconds:F1}fps ({mapBusyPercent:F1}%) " +
                $"mapFailure={mapFailure / seconds:F1}fps " +
                $"published={published / seconds:F1}fps " +
                $"publishDrop={publishDropped}");
            _log.WriteLine(
                $"[VideoCapture] scheduledFrames={scheduledFrames} " +
                $"publishedFrames={published} " +
                $"missedDeadlines={missedDeadlines} " +
                $"deadlineLateness={AverageQpcMilliseconds(
                    latenessQpc,
                    latenessSamples,
                    current.QpcFrequency):F2}/" +
                $"{DeadlineLatenessPercentileMilliseconds(
                    previous,
                    current,
                    0.50):F2}/" +
                $"{DeadlineLatenessPercentileMilliseconds(
                    previous,
                    current,
                    0.95):F2}ms avg/p50/p95");
            _log.WriteLine(
                $"[VideoCapture] " +
                $"acquire={AverageQpcMilliseconds(
                    current.AcquireQpcTotal - previous.AcquireQpcTotal,
                    acquired,
                    current.QpcFrequency):F3}ms " +
                $"copySubmit={AverageQpcMilliseconds(
                    current.CopyQpcTotal - previous.CopyQpcTotal,
                    copies,
                    current.QpcFrequency):F3}ms " +
                $"mapAttempt={AverageQpcMilliseconds(
                    current.MapAttemptQpcTotal - previous.MapAttemptQpcTotal,
                    mapAttempts,
                    current.QpcFrequency):F3}ms " +
                $"cpuPublish={AverageQpcMilliseconds(
                    current.CpuPublishQpcTotal - previous.CpuPublishQpcTotal,
                    mapSuccess,
                    current.QpcFrequency):F3}ms");
        }

        private static bool CountersAreMonotonic(
            PanelDisplayCaptureMetrics previous,
            PanelDisplayCaptureMetrics current) =>
            current.SwapchainAcquired >= previous.SwapchainAcquired &&
            current.CaptureThrottleSkipped >= previous.CaptureThrottleSkipped &&
            current.CopySubmitted >= previous.CopySubmitted &&
            current.MapSuccess >= previous.MapSuccess &&
            current.MapBusy >= previous.MapBusy &&
            current.MapFailure >= previous.MapFailure &&
            current.Published >= previous.Published &&
            current.PublishDropped >= previous.PublishDropped &&
            current.AcquireQpcTotal >= previous.AcquireQpcTotal &&
            current.CopyQpcTotal >= previous.CopyQpcTotal &&
            current.MapAttemptQpcTotal >= previous.MapAttemptQpcTotal &&
            current.CpuPublishQpcTotal >= previous.CpuPublishQpcTotal &&
            current.ScheduledFrames >= previous.ScheduledFrames &&
            current.MissedDeadlines >= previous.MissedDeadlines &&
            current.DeadlineLatenessSamples >=
                previous.DeadlineLatenessSamples &&
            current.DeadlineLatenessQpcTotal >=
                previous.DeadlineLatenessQpcTotal &&
            DeadlineLatenessHistogramIsMonotonic(previous, current);

        private static bool DeadlineLatenessHistogramIsMonotonic(
            PanelDisplayCaptureMetrics previous,
            PanelDisplayCaptureMetrics current)
        {
            if (previous.DeadlineLatenessHistogram.Count !=
                    PanelDisplayDriverContract.CaptureLatenessHistogramBuckets ||
                current.DeadlineLatenessHistogram.Count !=
                    PanelDisplayDriverContract.CaptureLatenessHistogramBuckets)
            {
                return false;
            }

            for (var index = 0;
                 index < PanelDisplayDriverContract.CaptureLatenessHistogramBuckets;
                 ++index)
            {
                if (current.DeadlineLatenessHistogram[index] <
                    previous.DeadlineLatenessHistogram[index])
                {
                    return false;
                }
            }
            return true;
        }

        private static double DeadlineLatenessPercentileMilliseconds(
            PanelDisplayCaptureMetrics previous,
            PanelDisplayCaptureMetrics current,
            double percentile)
        {
            if (previous.DeadlineLatenessHistogram.Count !=
                    PanelDisplayDriverContract.CaptureLatenessHistogramBuckets ||
                current.DeadlineLatenessHistogram.Count !=
                    PanelDisplayDriverContract.CaptureLatenessHistogramBuckets)
            {
                return 0d;
            }

            ulong samples = 0;
            for (var index = 0;
                 index < PanelDisplayDriverContract.CaptureLatenessHistogramBuckets;
                 ++index)
            {
                if (current.DeadlineLatenessHistogram[index] <
                    previous.DeadlineLatenessHistogram[index])
                {
                    return 0d;
                }
                samples += current.DeadlineLatenessHistogram[index] -
                    previous.DeadlineLatenessHistogram[index];
            }
            if (samples == 0)
            {
                return 0d;
            }

            var rank = checked((ulong)Math.Ceiling(samples * percentile));
            ulong cumulative = 0;
            for (var index = 0;
                 index < PanelDisplayDriverContract.CaptureLatenessHistogramBuckets;
                 ++index)
            {
                cumulative += current.DeadlineLatenessHistogram[index] -
                    previous.DeadlineLatenessHistogram[index];
                if (cumulative >= rank)
                {
                    return PanelDisplayDriverContract
                        .CaptureLatenessBucketUpperMicros[index] / 1000d;
                }
            }

            return PanelDisplayDriverContract
                .CaptureLatenessBucketUpperMicros[^1] / 1000d;
        }

        private static double AverageQpcMilliseconds(
            ulong elapsedTicks,
            ulong samples,
            ulong frequency) => samples == 0 || frequency == 0
                ? 0d
                : elapsedTicks * 1000d / frequency / samples;
    }
}
