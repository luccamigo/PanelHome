using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace PanelHost.Video;

internal sealed class MediaFoundationH264EncoderFactory : IH264EncoderFactory
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TextWriter _log;
    private bool _started;
    private bool _disposed;
    private bool _softwareFallbackLogged;

    internal MediaFoundationH264EncoderFactory(TextWriter? log = null)
    {
        _log = log ?? Console.Out;
    }

    public H264EncoderDescriptor? SelectedEncoder { get; private set; }

    public async Task<H264EncoderDescriptor?> ProbeAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStarted();
            SelectedEncoder ??= SelectUsableEncoder();
            return SelectedEncoder;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IH264Encoder> CreateAsync(
        H264EncoderSettings settings,
        CancellationToken cancellationToken)
    {
        settings.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureStarted();
            var candidates = H264EncoderSelection.Order(
                    EnumerateDescriptors().Distinct())
                .OrderByDescending(candidate => candidate == SelectedEncoder)
                .ToList();
            if (candidates.Count == 0)
            {
                throw new NotSupportedException(
                    "Windows has no Media Foundation H.264 encoder for NV12 input.");
            }

            Exception? lastFailure = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    var activated = ActivateEncoder(candidate);
                    try
                    {
                        var encoder = new MediaFoundationH264Encoder(
                            activated.Transform,
                            activated.Activation,
                            candidate,
                            settings,
                            _log);
                        SelectedEncoder = candidate;
                        if (!candidate.IsHardware)
                        {
                            LogSoftwareFallback();
                        }

                        return encoder;
                    }
                    catch
                    {
                        ReleaseActivatedEncoder(activated);
                        throw;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                    _log.WriteLine(
                        $"[Video] H.264 encoder rejected: {candidate.Name} " +
                        $"({exception.GetType().Name}: {exception.Message})");
                }
            }

            throw new NotSupportedException(
                "Windows has no usable Media Foundation H.264 encoder for NV12 input.",
                lastFailure);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_started)
            {
                MediaFactory.MFShutdown().CheckError();
                _started = false;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static RegisterTypeInfo InputType => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.NV12,
    };

    private static RegisterTypeInfo OutputType => new()
    {
        GuidMajorType = MediaTypeGuids.Video,
        GuidSubtype = VideoFormatGuids.H264,
    };

    private IEnumerable<H264EncoderDescriptor> FindEncoderDescriptors(bool hardware)
    {
        using var activations = Enumerate(hardware);
        foreach (var activation in activations)
        {
            var name = activation.GetAllocatedString(
                TransformAttributeKeys.MftFriendlyNameAttribute);
            yield return new H264EncoderDescriptor(
                string.IsNullOrWhiteSpace(name) ? "Unnamed Media Foundation encoder" : name,
                hardware);
        }
    }

    private IEnumerable<H264EncoderDescriptor> EnumerateDescriptors()
    {
        foreach (var hardware in FindEncoderDescriptors(hardware: true))
        {
            yield return hardware;
        }

        foreach (var software in FindEncoderDescriptors(hardware: false))
        {
            yield return software;
        }
    }

    private H264EncoderDescriptor? SelectUsableEncoder()
    {
        foreach (var candidate in H264EncoderSelection.Order(
                     EnumerateDescriptors().Distinct()))
        {
            try
            {
                var activated = ActivateEncoder(candidate);
                ReleaseActivatedEncoder(activated);
                if (!candidate.IsHardware)
                {
                    LogSoftwareFallback();
                }

                return candidate;
            }
            catch (Exception exception)
            {
                _log.WriteLine(
                    $"[Video] H.264 encoder activation failed: {candidate.Name} " +
                    $"({exception.GetType().Name}: {exception.Message})");
            }
        }

        return null;
    }

    private void LogSoftwareFallback()
    {
        if (_softwareFallbackLogged)
        {
            return;
        }

        _softwareFallbackLogged = true;
        _log.WriteLine(
            "[Video] H.264 hardware encoder unavailable; using software encoder.");
    }

    private ActivatedEncoder ActivateEncoder(H264EncoderDescriptor selected)
    {
        var hardware = selected.IsHardware;
        using var activations = Enumerate(hardware);
        foreach (var activation in activations)
        {
            var name = activation.GetAllocatedString(
                TransformAttributeKeys.MftFriendlyNameAttribute);
            if (!string.Equals(name, selected.Name, StringComparison.Ordinal))
            {
                continue;
            }

            // The collection owns its entries. Activate through a fresh
            // enumeration and retain this activation until the transform ends.
            var transform = activation.ActivateObject<IMFTransform>();
            var activationPointer = activation.NativePointer;
            Marshal.AddRef(activationPointer);
            return new ActivatedEncoder(
                transform,
                new IMFActivate(activationPointer));
        }

        throw new NotSupportedException(
            $"Media Foundation encoder '{selected.Name}' is no longer available.");
    }

    private static IMFActivateCollection Enumerate(bool hardware)
    {
        var flags = hardware
            ? EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter
            : EnumFlag.EnumFlagSyncmft |
                EnumFlag.EnumFlagAsyncmft |
                EnumFlag.EnumFlagLocalmft |
                EnumFlag.EnumFlagSortandfilter;
        return MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            (uint)flags,
            InputType,
            OutputType);
    }

    private void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        MediaFactory.MFStartup().CheckError();
        _started = true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static void ReleaseActivatedEncoder(ActivatedEncoder activated)
    {
        try
        {
            activated.Activation.ShutdownObject();
        }
        finally
        {
            activated.Transform.Dispose();
            activated.Activation.Dispose();
        }
    }

    private sealed record ActivatedEncoder(
        IMFTransform Transform,
        IMFActivate Activation);
}

internal sealed class MediaFoundationH264Encoder : IH264Encoder
{
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(2);
    private const int EventFlagNoWait = 1;
    private const int LifetimeDiagnosticCallLimit = 12;

    private readonly IMFTransform _transform;
    private readonly IMFActivate _activation;
    private readonly IMFMediaEventGenerator? _eventGenerator;
    private readonly TextWriter _log;
    private readonly int _inputStreamId;
    private readonly int _outputStreamId;
    private readonly OutputStreamInfo _outputStreamInfo;
    private readonly bool _mftProvidesOutputSamples;
    private readonly bool _isAsync;
    private readonly Dictionary<long, FrameIdentity> _framesByTimestamp = [];
    private byte[]? _decoderConfiguration;
    private long _nextSampleTime;
    private int _lifetimeDiagnosticCalls;
    private bool _asyncNeedsInput;
    private bool _disposed;

    internal MediaFoundationH264Encoder(
        IMFTransform transform,
        IMFActivate activation,
        H264EncoderDescriptor descriptor,
        H264EncoderSettings settings,
        TextWriter log)
    {
        _transform = transform;
        _activation = activation;
        Descriptor = descriptor;
        Settings = settings;
        _log = log;
        (_inputStreamId, _outputStreamId) = GetStreamIds(transform);

        using var attributes = transform.Attributes;
        _isAsync = MediaFactory.MFGetAttributeUInt32(
            attributes,
            TransformAttributeKeys.TransformAsync) != 0;
        if (_isAsync)
        {
            attributes.Set(
                TransformAttributeKeys.TransformAsyncUnlock,
                1u).CheckError();
            _eventGenerator = transform.QueryInterface<IMFMediaEventGenerator>();
        }

        // Microsoft H.264 MFT static codec properties (notably B-picture
        // count) must be set before SetOutputType. The output type still has
        // to precede the input type.
        ConfigureLowLatency();
        ConfigureMediaTypes();
        _outputStreamInfo = _transform.GetOutputStreamInfo(_outputStreamId);
        var outputFlags = (OutputStreamInfoFlags)_outputStreamInfo.Flags;
        _mftProvidesOutputSamples =
            (outputFlags & OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        var allocationModel = _mftProvidesOutputSamples
            ? "mft-provided"
            : (outputFlags & OutputStreamInfoFlags.OutputStreamCanProvideSamples) != 0
                ? "caller-provided (CAN_PROVIDE_SAMPLES)"
                : "caller-provided";
        _log.WriteLine(
            $"[Video] H.264 output stream: dwFlags=0x{_outputStreamInfo.Flags:X8} " +
            $"({outputFlags}) cbSize={_outputStreamInfo.Size} " +
            $"cbAlignment={_outputStreamInfo.Alignment} model={allocationModel}.");
        _transform.ProcessMessage(
            TMessageType.MessageNotifyBeginStreaming,
            UIntPtr.Zero);
        _transform.ProcessMessage(
            TMessageType.MessageNotifyStartOfStream,
            UIntPtr.Zero);
    }

    public H264EncoderDescriptor Descriptor { get; }

    public H264EncoderSettings Settings { get; }

    public async Task<IReadOnlyList<H264EncodedAccessUnit>> EncodeAsync(
        Nv12VideoFrame frame,
        bool forceKeyFrame,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateFrame(frame);
        cancellationToken.ThrowIfCancellationRequested();

        if (forceKeyFrame)
        {
            CodecApi.TrySetBoolean(
                _transform.NativePointer,
                CodecApi.ForceKeyFrame,
                value: true);
        }

        var started = Stopwatch.GetTimestamp();
        var outputs = new List<H264EncodedAccessUnit>(1);
        if (_isAsync)
        {
            await WaitForInputAsync(outputs, started, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            DrainSynchronousOutput(outputs, started);
        }

        var sampleTime = _nextSampleTime;
        _nextSampleTime = checked(
            _nextSampleTime + TimeSpan.TicksPerSecond / Settings.Fps);
        var inputSampleStarted = Stopwatch.GetTimestamp();
        using (var sample = CreateInputSample(frame, sampleTime))
        {
            var inputSampleCopyMicros =
                Stopwatch.GetElapsedTime(inputSampleStarted).Ticks / 10;
            var processInputStarted = Stopwatch.GetTimestamp();
            _transform.ProcessInput(_inputStreamId, sample, 0);
            var processInputMicros =
                Stopwatch.GetElapsedTime(processInputStarted).Ticks / 10;
            _framesByTimestamp[sampleTime] = new FrameIdentity(
                frame.Sequence,
                frame.CaptureTimestampMicros,
                inputSampleCopyMicros,
                processInputMicros);
        }

        if (_isAsync)
        {
            _asyncNeedsInput = false;
            await WaitForOutputAsync(outputs, started, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            DrainSynchronousOutput(outputs, started);
        }

        return outputs;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        try
        {
            try
            {
                _transform.ProcessMessage(
                    TMessageType.MessageNotifyEndOfStream,
                    UIntPtr.Zero);
            }
            finally
            {
                _transform.ProcessMessage(
                    TMessageType.MessageNotifyEndStreaming,
                    UIntPtr.Zero);
            }
        }
        finally
        {
            try
            {
                _activation.ShutdownObject();
            }
            finally
            {
                _eventGenerator?.Dispose();
                _transform.Dispose();
                _activation.Dispose();
                _framesByTimestamp.Clear();
            }
        }

        return ValueTask.CompletedTask;
    }

    private static (int Input, int Output) GetStreamIds(IMFTransform transform)
    {
        var input = new int[1];
        var output = new int[1];
        try
        {
            transform.GetStreamIDs(1, input, 1, output);
            return (input[0], output[0]);
        }
        catch (SharpGenException exception)
            when (exception.HResult == unchecked((int)0x80004001))
        {
            return (0, 0);
        }
    }

    private void ConfigureMediaTypes()
    {
        using var output = MediaFactory.MFCreateMediaType();
        output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        output.Set(MediaTypeAttributeKeys.AvgBitrate, checked((uint)Settings.Bitrate))
            .CheckError();
        output.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            (uint)VideoInterlaceMode.Progressive).CheckError();
        output.Set(
            MediaTypeAttributeKeys.FrameSize,
            MediaFactory.PackSize(
                checked((uint)Settings.Width),
                checked((uint)Settings.Height))).CheckError();
        output.Set(
            MediaTypeAttributeKeys.FrameRate,
            MediaFactory.PackRatio(Settings.Fps, 1)).CheckError();
        output.Set(
            MediaTypeAttributeKeys.PixelAspectRatio,
            MediaFactory.PackRatio(1, 1)).CheckError();
        _transform.SetOutputType(_outputStreamId, output, 0);

        using var input = MediaFactory.MFCreateMediaType();
        input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        input.Set(
            MediaTypeAttributeKeys.InterlaceMode,
            (uint)VideoInterlaceMode.Progressive).CheckError();
        input.Set(
            MediaTypeAttributeKeys.FrameSize,
            MediaFactory.PackSize(
                checked((uint)Settings.Width),
                checked((uint)Settings.Height))).CheckError();
        input.Set(
            MediaTypeAttributeKeys.FrameRate,
            MediaFactory.PackRatio(Settings.Fps, 1)).CheckError();
        input.Set(
            MediaTypeAttributeKeys.PixelAspectRatio,
            MediaFactory.PackRatio(1, 1)).CheckError();
        input.Set(
            MediaTypeAttributeKeys.DefaultStride,
            checked((uint)Settings.Width)).CheckError();
        _transform.SetInputType(_inputStreamId, input, 0);
    }

    private void ConfigureLowLatency()
    {
        var nativePointer = _transform.NativePointer;
        var lowLatency = CodecApi.TrySetBoolean(
            nativePointer,
            CodecApi.LowLatencyMode,
            value: true);
        var noBFrames = CodecApi.TrySetUInt32(
            nativePointer,
            CodecApi.BPictureCount,
            0);
        var gop = CodecApi.TrySetUInt32(
            nativePointer,
            CodecApi.GopSize,
            checked((uint)Settings.KeyFrameInterval));
        _log.WriteLine(
            $"[Video] H.264 low-latency controls: lowLatency={lowLatency} " +
            $"bFrames0={noBFrames} gop={gop}.");
    }

    private IMFSample CreateInputSample(Nv12VideoFrame frame, long sampleTime)
    {
        // MFCreateMemoryBuffer returns one caller-owned reference. AddBuffer
        // gives the sample its own reference, so the local buffer owner can be
        // released after construction while the returned sample remains valid.
        var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Pixels.Length);
        try
        {
            buffer.Lock(out var pointer, out _, out _);
            try
            {
                Marshal.Copy(frame.Pixels, 0, pointer, frame.Pixels.Length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = frame.Pixels.Length;
            var sample = MediaFactory.MFCreateSample();
            try
            {
                sample.AddBuffer(buffer);
                sample.SampleTime = sampleTime;
                sample.SampleDuration = TimeSpan.TicksPerSecond / Settings.Fps;
                return sample;
            }
            catch
            {
                sample.Dispose();
                throw;
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private async Task WaitForInputAsync(
        List<H264EncodedAccessUnit> outputs,
        long encodeStarted,
        CancellationToken cancellationToken)
    {
        if (_asyncNeedsInput)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() +
            (long)(OutputTimeout.TotalSeconds * Stopwatch.Frequency);
        while (!_asyncNeedsInput)
        {
            await PumpAsyncEventAsync(outputs, encodeStarted, deadline, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task WaitForOutputAsync(
        List<H264EncodedAccessUnit> outputs,
        long encodeStarted,
        CancellationToken cancellationToken)
    {
        var initialCount = outputs.Count;
        var deadline = Stopwatch.GetTimestamp() +
            (long)(OutputTimeout.TotalSeconds * Stopwatch.Frequency);
        while (outputs.Count == initialCount && !_asyncNeedsInput)
        {
            await PumpAsyncEventAsync(outputs, encodeStarted, deadline, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task PumpAsyncEventAsync(
        List<H264EncodedAccessUnit> outputs,
        long encodeStarted,
        long deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Stopwatch.GetTimestamp() >= deadline)
        {
            throw new TimeoutException("Media Foundation H.264 encoder timed out.");
        }

        IMFMediaEvent? mediaEvent = null;
        try
        {
            mediaEvent = _eventGenerator!.GetEvent(EventFlagNoWait);
        }
        catch (SharpGenException exception)
            when (exception.ResultCode == ResultCode.NoEventsAvailable)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (mediaEvent)
        {
            mediaEvent.Status.CheckError();
            switch ((MediaEventTypes)mediaEvent.EventType)
            {
                case MediaEventTypes.TransformNeedInput:
                    _asyncNeedsInput = true;
                    break;
                case MediaEventTypes.TransformHaveOutput:
                    var outputResult = ReadOneOutput(outputs, encodeStarted);
                    if (outputResult != ResultCode.TransformNeedMoreInput)
                    {
                        outputResult.CheckError();
                    }
                    break;
                case MediaEventTypes.TransformDrainComplete:
                case MediaEventTypes.TransformMarker:
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unexpected Media Foundation event {mediaEvent.EventType}.");
            }
        }
    }

    private void DrainSynchronousOutput(
        List<H264EncodedAccessUnit> outputs,
        long encodeStarted)
    {
        while (true)
        {
            var result = ReadOneOutput(outputs, encodeStarted);
            if (result == ResultCode.TransformNeedMoreInput)
            {
                return;
            }

            result.CheckError();
        }
    }

    private Result ReadOneOutput(
        List<H264EncodedAccessUnit> outputs,
        long encodeStarted)
    {
        // Ownership model for each call:
        // - callerSample/callerBuffer: caller-owned when the MFT does not set
        //   PROVIDES_SAMPLES (CAN_PROVIDE deliberately uses this same model).
        // - output.Sample: returned MFT owner only in the MFT-provided model;
        //   in the caller model Vortice 3.8.3 rematerializes a non-AddRef alias.
        // - output.Events: returned by the MFT and owned exactly once here.
        // - the contiguous buffer in CreateAccessUnit: an independent returned
        //   COM reference, released before either output sample owner.
        IMFMediaBuffer? callerBuffer = null;
        IMFSample? callerSample = null;
        if (!_mftProvidesOutputSamples)
        {
            var capacity = _outputStreamInfo.Size;
            if (capacity <= 0)
            {
                throw new InvalidDataException(
                    "H.264 MFT requires caller output samples but reported cbSize=0.");
            }

            callerBuffer = _outputStreamInfo.Alignment > 0
                ? MediaFactory.MFCreateAlignedMemoryBuffer(
                    capacity,
                    _outputStreamInfo.Alignment)
                : MediaFactory.MFCreateMemoryBuffer(capacity);
            try
            {
                callerSample = MediaFactory.MFCreateSample();
                callerSample.AddBuffer(callerBuffer);
            }
            catch
            {
                callerSample?.Dispose();
                callerBuffer.Dispose();
                throw;
            }
        }

        var output = new OutputDataBuffer
        {
            StreamID = _outputStreamId,
            Sample = callerSample,
        };
        Result? processOutputResult = null;
        nint contiguousBufferPointer = 0;
        try
        {
            var processOutputStarted = Stopwatch.GetTimestamp();
            var result = _transform.ProcessOutput(
                ProcessOutputFlags.None,
                1,
                ref output,
                out _);
            processOutputResult = result;
            var processOutputMicros =
                Stopwatch.GetElapsedTime(processOutputStarted).Ticks / 10;
            if (result.Failure)
            {
                return result;
            }

            var sample = output.Sample ?? callerSample ??
                throw new InvalidDataException("H.264 encoder returned no output sample.");
            var outputCopyStarted = Stopwatch.GetTimestamp();
            var accessUnit = CreateAccessUnit(
                sample,
                encodeStarted,
                processOutputMicros,
                out contiguousBufferPointer);
            outputs.Add(accessUnit with
            {
                OutputSampleCopyDurationMicros =
                    Stopwatch.GetElapsedTime(outputCopyStarted).Ticks / 10,
            });
            return result;
        }
        finally
        {
            var returnedSample = output.Sample;
            var callerSamplePointer = callerSample?.NativePointer ?? 0;
            var returnedSamplePointer = returnedSample?.NativePointer ?? 0;
            var sameSample = callerSamplePointer != 0 &&
                callerSamplePointer == returnedSamplePointer;
            try
            {
                LogLifetimeDiagnostic(
                    processOutputResult,
                    callerSamplePointer,
                    returnedSamplePointer,
                    callerBuffer?.NativePointer ?? 0,
                    contiguousBufferPointer,
                    sameSample);
            }
            finally
            {
                try
                {
                    output.Events?.Dispose();
                }
                finally
                {
                    try
                    {
                        if (returnedSample is not null &&
                            !ReferenceEquals(returnedSample, callerSample) &&
                            !MediaFoundationComLifetime.DetachNonOwningAlias(
                                returnedSample,
                                callerSample))
                        {
                            // MFT-provided samples, and the unexpected case
                            // where a caller sample is replaced, own their ref.
                            returnedSample.Dispose();
                        }
                    }
                    finally
                    {
                        try
                        {
                            // AddBuffer keeps its own buffer ref. Release the
                            // sample before the caller's buffer reference.
                            callerSample?.Dispose();
                        }
                        finally
                        {
                            callerBuffer?.Dispose();
                        }
                    }
                }
            }
        }
    }

    private H264EncodedAccessUnit CreateAccessUnit(
        IMFSample sample,
        long encodeStarted,
        long processOutputMicros,
        out nint contiguousBufferPointer)
    {
        using var buffer = sample.ConvertToContiguousBuffer();
        contiguousBufferPointer = buffer.NativePointer;
        buffer.Lock(out var pointer, out _, out var currentLength);
        byte[] payload;
        try
        {
            payload = new byte[currentLength];
            Marshal.Copy(pointer, payload, 0, currentLength);
        }
        finally
        {
            buffer.Unlock();
        }

        var annexB = H264AnnexB.NormalizeAccessUnit(payload);
        FrameIdentity identity;
        try
        {
            var sampleTime = sample.SampleTime;
            identity = _framesByTimestamp.Remove(sampleTime, out var found)
                ? found
                : _framesByTimestamp.Count > 0
                    ? RemoveOldestIdentity()
                    : throw new InvalidDataException(
                        "H.264 output does not match an input frame timestamp.");
        }
        catch (SharpGenException) when (_framesByTimestamp.Count > 0)
        {
            // A few hardware MFTs omit MFSampleExtension timestamps even though
            // they preserve output order when frame reordering is disabled.
            identity = RemoveOldestIdentity();
        }
        var cleanPoint = MediaFactory.MFGetAttributeUInt32(
            sample,
            SampleAttributeKeys.CleanPoint) != 0;
        var keyFrame = cleanPoint || H264AnnexB.ContainsIdr(annexB);
        var parameterSets = H264AnnexB.ExtractParameterSets(annexB);
        if (parameterSets is not null)
        {
            _decoderConfiguration = parameterSets;
        }
        else if (_decoderConfiguration is null)
        {
            using var outputType = _transform.GetOutputCurrentType(_outputStreamId);
            try
            {
                var sequenceHeader = outputType.GetBlob(
                    MediaTypeAttributeKeys.MpegSequenceHeader);
                if (sequenceHeader.Length > 0)
                {
                    _decoderConfiguration = H264AnnexB.NormalizeSequenceHeader(
                        sequenceHeader);
                }
            }
            catch (SharpGenException)
            {
                // Some MFTs put SPS/PPS directly in the first IDR instead of
                // exposing MF_MT_MPEG_SEQUENCE_HEADER.
            }
        }

        return new H264EncodedAccessUnit(
            identity.Sequence,
            identity.CaptureTimestampMicros,
            annexB,
            keyFrame,
            Stopwatch.GetElapsedTime(encodeStarted).Ticks / 10,
            keyFrame ? _decoderConfiguration : null,
            identity.InputSampleCopyDurationMicros,
            identity.ProcessInputDurationMicros,
            processOutputMicros);
    }

    private void LogLifetimeDiagnostic(
        Result? result,
        nint callerSamplePointer,
        nint returnedSamplePointer,
        nint callerBufferPointer,
        nint contiguousBufferPointer,
        bool sameSample)
    {
        if (Interlocked.Increment(ref _lifetimeDiagnosticCalls) >
            LifetimeDiagnosticCallLimit)
        {
            return;
        }

        var resultText = result is null
            ? "not-called"
            : result.Value == ResultCode.TransformNeedMoreInput
                ? $"MF_E_TRANSFORM_NEED_MORE_INPUT (0x{unchecked((uint)result.Value.Code):X8})"
                : $"0x{unchecked((uint)result.Value.Code):X8}";
        _log.WriteLine(
            $"[H264Lifetime] ProcessOutput={resultText} " +
            $"callerSample={FormatPointer(callerSamplePointer)} " +
            $"returnedSample={FormatPointer(returnedSamplePointer)} " +
            $"sameSample={sameSample.ToString().ToLowerInvariant()} " +
            $"callerBuffer={FormatPointer(callerBufferPointer)} " +
            $"contiguousBuffer={FormatPointer(contiguousBufferPointer)}");
    }

    private static string FormatPointer(nint pointer) =>
        $"0x{unchecked((nuint)pointer):X}";

    private FrameIdentity RemoveOldestIdentity()
    {
        var oldest = _framesByTimestamp.MinBy(entry => entry.Key);
        _framesByTimestamp.Remove(oldest.Key);
        return oldest.Value;
    }

    private void ValidateFrame(Nv12VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Sequence == 0 || frame.Width != Settings.Width ||
            frame.Height != Settings.Height ||
            frame.Pixels.Length != frame.ExpectedByteCount)
        {
            throw new InvalidDataException("NV12 frame does not match the encoder session.");
        }
    }

    private readonly record struct FrameIdentity(
        ulong Sequence,
        ulong CaptureTimestampMicros,
        long InputSampleCopyDurationMicros,
        long ProcessInputDurationMicros);
}

internal static class MediaFoundationComLifetime
{
    /// <summary>
    /// Vortice 3.8.3 marshals an in/out OutputDataBuffer.Sample by constructing
    /// a second wrapper around the native pointer without calling AddRef. When
    /// the pointer is still the caller-owned sample, the new wrapper is only an
    /// alias and must be detached so neither Dispose nor finalization releases
    /// a reference it does not own.
    /// </summary>
    internal static bool DetachNonOwningAlias(
        ComObject candidate,
        ComObject? owner)
    {
        if (owner is null || ReferenceEquals(candidate, owner) ||
            candidate.NativePointer == 0 ||
            candidate.NativePointer != owner.NativePointer)
        {
            return false;
        }

        candidate.NativePointer = 0;
        return true;
    }
}

internal static unsafe class CodecApi
{
    internal static readonly Guid ForceKeyFrame =
        new("398c1b98-8353-475a-9ef2-8f265d260345");
    internal static readonly Guid GopSize =
        new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    internal static readonly Guid BPictureCount =
        new("8d390aac-dc5c-4200-b57f-814d04babab2");
    internal static readonly Guid LowLatencyMode =
        new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private static readonly Guid InterfaceId =
        new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    internal static bool TrySetBoolean(nint transform, Guid property, bool value) =>
        TrySet(transform, property, value);

    internal static bool TrySetUInt32(nint transform, Guid property, uint value) =>
        TrySet(transform, property, value);

    private static bool TrySet(nint transform, Guid property, object value)
    {
        var interfaceId = InterfaceId;
        var result = Marshal.QueryInterface(transform, ref interfaceId, out var codecApi);
        if (result < 0)
        {
            return false;
        }

        var variant = Marshal.AllocCoTaskMem(24);
        try
        {
            Marshal.GetNativeVariantForObject(value, variant);
            var vtable = *(nint**)codecApi;
            var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)vtable[9];
            var propertyCopy = property;
            return setValue(codecApi, &propertyCopy, variant) >= 0;
        }
        finally
        {
            VariantClear(variant);
            Marshal.FreeCoTaskMem(variant);
            Marshal.Release(codecApi);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(nint variant);
}
