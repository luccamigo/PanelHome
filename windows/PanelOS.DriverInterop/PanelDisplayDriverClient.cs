using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PanelOS.DriverInterop;

/// <summary>
/// Direct Win32 client for the installed PanelDisplayDriver. Normal callers
/// rediscover and reopen the interface for every operation. The privileged
/// display broker opts into a persistent read/write handle so it owns one
/// driver lease for the complete Desktop session.
/// </summary>
public sealed class PanelDisplayDriverClient :
    IPanelDisplayDriverClient,
    IPanelDisplayDriverFrameClient,
    IPanelDisplayDriverCaptureMetricsClient,
    IPanelDisplayDriverCaptureConfigurationClient,
    IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;

    private const uint CmGetDeviceInterfaceListPresent = 0x00000000;
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const uint CrNoSuchValue = 0x00000025;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidData = 13;
    private const int ErrorGenFailure = 31;
    private const int ErrorOperationAborted = 995;
    private const int ErrorIoPending = 997;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorNotFound = 1168;

    private string? _interfacePath;
    private readonly bool _keepHandleOpen;
    private readonly object _handleLock = new();
    private SafeFileHandle? _persistentHandle;
    private int _disposed;

    public PanelDisplayDriverClient(bool keepHandleOpen = false)
    {
        _keepHandleOpen = keepHandleOpen;
    }

    public string? InterfacePath => Volatile.Read(ref _interfacePath);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_handleLock)
        {
            _persistentHandle?.Dispose();
            _persistentHandle = null;
        }
    }

    public async Task<PanelDisplayDriverStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        var output = await SendIoctlAsync<PanelDisplayDriverContract.StatusOutputNative>(
            operation: "GET_STATUS",
            desiredAccess: GenericRead,
            ioControlCode: PanelDisplayDriverContract.IoctlGetStatus,
            expectedOutputSize: PanelDisplayDriverContract.StatusOutputSize,
            cancellationToken).ConfigureAwait(false);

        ValidateStatus(output);

        var nativeModes = output.Modes!;
        var modes = new PanelDisplayMode[checked((int)output.ModeCount)];
        for (var index = 0; index < modes.Length; index++)
        {
            modes[index] = nativeModes[index].ToManaged();
        }

        return new PanelDisplayDriverStatus(
            output.ProtocolVersion,
            output.DriverState,
            output.MonitorState,
            output.ActiveMode.ToManaged(),
            output.FramesProcessed,
            output.ConnectCount,
            modes);
    }

    public async Task<PanelDisplayCommandResponse> ConnectAsync(
        CancellationToken cancellationToken)
    {
        // A zero-length input explicitly selects the driver's validated default
        // mode. DesktopRequest negotiation is intentionally not part of v1.
        var output = await SendIoctlAsync<PanelDisplayDriverContract.CommandOutputNative>(
            operation: "CONNECT_DISPLAY",
            desiredAccess: GenericWrite,
            ioControlCode: PanelDisplayDriverContract.IoctlConnectDisplay,
            expectedOutputSize: PanelDisplayDriverContract.CommandOutputSize,
            cancellationToken).ConfigureAwait(false);

        ValidateCommand(output, "CONNECT_DISPLAY");
        return new PanelDisplayCommandResponse(output.Result, output.MonitorState);
    }

    public async Task<PanelDisplayCommandResponse> DisconnectAsync(
        CancellationToken cancellationToken)
    {
        var output = await SendIoctlAsync<PanelDisplayDriverContract.CommandOutputNative>(
            operation: "DISCONNECT_DISPLAY",
            desiredAccess: GenericWrite,
            ioControlCode: PanelDisplayDriverContract.IoctlDisconnectDisplay,
            expectedOutputSize: PanelDisplayDriverContract.CommandOutputSize,
            cancellationToken).ConfigureAwait(false);

        ValidateCommand(output, "DISCONNECT_DISPLAY");
        return new PanelDisplayCommandResponse(output.Result, output.MonitorState);
    }

    public async Task<PanelDisplayFrameInfo> GetFrameInfoAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        var query = new PanelDisplayDriverContract.FrameQueryNative
        {
            Size = PanelDisplayDriverContract.FrameQuerySize,
            LastSequence = lastSequence,
        };
        var bytes = await SendIoctlBytesAsync(
            operation: "GET_FRAME_INFO",
            desiredAccess: GenericRead,
            ioControlCode: PanelDisplayDriverContract.IoctlGetFrameInfo,
            input: StructureToBytes(query),
            outputCapacity: PanelDisplayDriverContract.FrameInfoSize,
            exactOutputSize: PanelDisplayDriverContract.FrameInfoSize,
            cancellationToken).ConfigureAwait(false);
        var native = MemoryMarshal.Read<PanelDisplayDriverContract.FrameInfoNative>(
            bytes);
        ValidateFrameInfo(native, "GET_FRAME_INFO");
        return native.ToManaged();
    }

    public async Task<PanelDisplayDriverFrame> ReadFrameAsync(
        PanelDisplayFrameInfo requestedFrame,
        CancellationToken cancellationToken)
    {
        if (requestedFrame.Result != PanelDisplayFrameResult.Ok)
        {
            throw new ArgumentException(
                "Only an available frame can be read.",
                nameof(requestedFrame));
        }

        var outputCapacity = checked(
            PanelDisplayDriverContract.FrameInfoSize +
            checked((int)requestedFrame.PayloadBytes));
        var input = new PanelDisplayDriverContract.FrameReadInputNative
        {
            Size = PanelDisplayDriverContract.FrameReadInputSize,
            Generation = requestedFrame.Generation,
            Sequence = requestedFrame.Sequence,
        };
        var bytes = await SendIoctlBytesAsync(
            operation: "READ_LATEST_FRAME",
            desiredAccess: GenericRead,
            ioControlCode: PanelDisplayDriverContract.IoctlReadLatestFrame,
            input: StructureToBytes(input),
            outputCapacity,
            exactOutputSize: null,
            cancellationToken).ConfigureAwait(false);

        if (bytes.Length < PanelDisplayDriverContract.FrameInfoSize)
        {
            throw CreateInvalidDataException(
                "READ_LATEST_FRAME",
                $"returned {bytes.Length} bytes; expected at least " +
                PanelDisplayDriverContract.FrameInfoSize);
        }

        var native = MemoryMarshal.Read<PanelDisplayDriverContract.FrameInfoNative>(
            bytes);
        ValidateFrameInfo(native, "READ_LATEST_FRAME");
        var info = native.ToManaged();
        if (info.Result != PanelDisplayFrameResult.Ok)
        {
            return new PanelDisplayDriverFrame(info, []);
        }

        if (info.Generation != requestedFrame.Generation ||
            info.Sequence != requestedFrame.Sequence)
        {
            throw CreateInvalidDataException(
                "READ_LATEST_FRAME",
                "returned a different generation or sequence without " +
                "FrameReplaced");
        }

        var expectedBytes = checked(
            PanelDisplayDriverContract.FrameInfoSize +
            checked((int)info.PayloadBytes));
        if (bytes.Length != expectedBytes)
        {
            throw CreateInvalidDataException(
                "READ_LATEST_FRAME",
                $"returned {bytes.Length} bytes; expected {expectedBytes}");
        }

        return new PanelDisplayDriverFrame(
            info,
            bytes[PanelDisplayDriverContract.FrameInfoSize..]);
    }

    public async Task<PanelDisplayCaptureMetrics> GetCaptureMetricsAsync(
        CancellationToken cancellationToken)
    {
        var output = await SendIoctlAsync<
            PanelDisplayDriverContract.CaptureMetricsNative>(
            operation: "GET_CAPTURE_METRICS",
            desiredAccess: GenericRead,
            ioControlCode: PanelDisplayDriverContract.IoctlGetCaptureMetrics,
            expectedOutputSize: PanelDisplayDriverContract.CaptureMetricsSize,
            cancellationToken).ConfigureAwait(false);
        if (output.Size != PanelDisplayDriverContract.CaptureMetricsSize ||
            output.ProtocolVersion != PanelDisplayDriverContract.ProtocolVersion ||
            output.Generation != 0 && output.QpcFrequency == 0 ||
            output.TargetFramesPerSecond is not (30 or 45 or 60) ||
            output.Reserved != 0)
        {
            throw CreateInvalidDataException(
                "GET_CAPTURE_METRICS",
                "returned an invalid size, protocol, target, reserved field, " +
                "or QPC frequency");
        }

        var pacing = await SendIoctlAsync<
            PanelDisplayDriverContract.CapturePacingMetricsNative>(
            operation: "GET_CAPTURE_PACING_METRICS",
            desiredAccess: GenericRead,
            ioControlCode:
                PanelDisplayDriverContract.IoctlGetCapturePacingMetrics,
            expectedOutputSize:
                PanelDisplayDriverContract.CapturePacingMetricsSize,
            cancellationToken).ConfigureAwait(false);
        if (pacing.Size !=
                PanelDisplayDriverContract.CapturePacingMetricsSize ||
            pacing.ProtocolVersion !=
                PanelDisplayDriverContract.ProtocolVersion ||
            pacing.Generation != output.Generation ||
            pacing.QpcFrequency != output.QpcFrequency ||
            pacing.TargetFramesPerSecond != output.TargetFramesPerSecond ||
            pacing.Generation != 0 &&
                pacing.SchedulerTargetFramesPerSecond is not (30 or 45 or 60) ||
            pacing.HistogramBucketCount !=
                PanelDisplayDriverContract.CaptureLatenessHistogramBuckets ||
            pacing.Reserved != 0 ||
            pacing.DeadlineLatenessHistogram is null ||
            pacing.DeadlineLatenessHistogram.Length !=
                PanelDisplayDriverContract.CaptureLatenessHistogramBuckets)
        {
            throw CreateInvalidDataException(
                "GET_CAPTURE_PACING_METRICS",
                "returned an invalid size, protocol, generation, target, " +
                "QPC frequency, or histogram");
        }

        return output.ToManaged(pacing);
    }

    public async Task<uint> SetCaptureTargetFpsAsync(
        uint targetFramesPerSecond,
        CancellationToken cancellationToken)
    {
        var input = new PanelDisplayDriverContract.CaptureConfigInputNative
        {
            Size = PanelDisplayDriverContract.CaptureConfigInputSize,
            TargetFramesPerSecond = targetFramesPerSecond,
        };
        var bytes = await SendIoctlBytesAsync(
            operation: "SET_CAPTURE_CONFIG",
            desiredAccess: GenericWrite,
            ioControlCode: PanelDisplayDriverContract.IoctlSetCaptureConfig,
            input: StructureToBytes(input),
            outputCapacity: PanelDisplayDriverContract.CaptureConfigOutputSize,
            exactOutputSize: PanelDisplayDriverContract.CaptureConfigOutputSize,
            cancellationToken).ConfigureAwait(false);
        var output = MemoryMarshal.Read<
            PanelDisplayDriverContract.CaptureConfigOutputNative>(bytes);
        if (output.Size != PanelDisplayDriverContract.CaptureConfigOutputSize ||
            output.ProtocolVersion != PanelDisplayDriverContract.ProtocolVersion ||
            output.Reserved != 0 ||
            output.TargetFramesPerSecond != targetFramesPerSecond)
        {
            throw CreateInvalidDataException(
                "SET_CAPTURE_CONFIG",
                "returned an invalid size, protocol version, target, or reserved field");
        }

        return output.TargetFramesPerSecond;
    }

    private async Task<T> SendIoctlAsync<T>(
        string operation,
        uint desiredAccess,
        uint ioControlCode,
        int expectedOutputSize,
        CancellationToken cancellationToken)
        where T : struct
    {
        var bytes = await SendIoctlBytesAsync(
            operation,
            desiredAccess,
            ioControlCode,
            input: [],
            outputCapacity: expectedOutputSize,
            exactOutputSize: expectedOutputSize,
            cancellationToken).ConfigureAwait(false);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject());
        }
        finally
        {
            pinned.Free();
        }
    }

    private async Task<byte[]> SendIoctlBytesAsync(
        string operation,
        uint desiredAccess,
        uint ioControlCode,
        byte[] input,
        int outputCapacity,
        int? exactOutputSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputCapacity);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Move discovery and native I/O off the caller's synchronous path.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        SafeFileHandle? operationHandle = null;
        var handle = _keepHandleOpen
            ? GetPersistentHandle(cancellationToken)
            : operationHandle = OpenHandle(desiredAccess, cancellationToken);
        var interfacePath = InterfacePath;

        var inputPointer = IntPtr.Zero;
        var outputPointer = IntPtr.Zero;
        var nativeOverlapped = IntPtr.Zero;
        EventWaitHandle? completionEvent = null;
        try
        {
            if (input.Length > 0)
            {
                inputPointer = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inputPointer, input.Length);
            }

            outputPointer = Marshal.AllocHGlobal(outputCapacity);
            nativeOverlapped = Marshal.AllocHGlobal(
                Marshal.SizeOf<NativeOverlappedData>());
            completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            Marshal.StructureToPtr(
                new NativeOverlappedData
                {
                    EventHandle = completionEvent.SafeWaitHandle.DangerousGetHandle(),
                },
                nativeOverlapped,
                fDeleteOld: false);

            var pendingIo = new PendingIoCancellation(handle, nativeOverlapped);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((PendingIoCancellation)state!).Cancel(),
                pendingIo);
            cancellationToken.ThrowIfCancellationRequested();

            var completedSynchronously = NativeMethods.DeviceIoControl(
                handle,
                ioControlCode,
                inputPointer,
                checked((uint)input.Length),
                outputPointer,
                checked((uint)outputCapacity),
                IntPtr.Zero,
                nativeOverlapped);
            if (!completedSynchronously)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorIoPending)
                {
                    if (error == ErrorOperationAborted &&
                        cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw CreateWin32Exception(operation, error, interfacePath);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    pendingIo.Cancel();
                }

                completionEvent.WaitOne();
            }

            if (!NativeMethods.GetOverlappedResult(
                    handle,
                    nativeOverlapped,
                    out var bytesReturned,
                    wait: false))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == ErrorOperationAborted &&
                    cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw CreateWin32Exception(operation, error, interfacePath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (bytesReturned > outputCapacity)
            {
                throw CreateInvalidDataException(
                    operation,
                    $"reported {bytesReturned} bytes for a {outputCapacity}-byte buffer");
            }

            if (exactOutputSize is int expected && bytesReturned != expected)
            {
                throw CreateInvalidDataException(
                    operation,
                    $"returned {bytesReturned} bytes; expected {expected}");
            }

            var result = new byte[checked((int)bytesReturned)];
            Marshal.Copy(outputPointer, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (nativeOverlapped != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(nativeOverlapped);
            }

            if (outputPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(outputPointer);
            }

            if (inputPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inputPointer);
            }

            completionEvent?.Dispose();
            operationHandle?.Dispose();
        }
    }

    private SafeFileHandle GetPersistentHandle(CancellationToken cancellationToken)
    {
        lock (_handleLock)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_persistentHandle is { IsClosed: false, IsInvalid: false })
            {
                return _persistentHandle;
            }

            _persistentHandle?.Dispose();
            _persistentHandle = OpenHandle(
                GenericRead | GenericWrite,
                cancellationToken);
            return _persistentHandle;
        }
    }

    private SafeFileHandle OpenHandle(
        uint desiredAccess,
        CancellationToken cancellationToken)
    {
        Volatile.Write(ref _interfacePath, null);
        var interfacePath = FindInterfacePath(cancellationToken);
        Volatile.Write(ref _interfacePath, interfacePath);
        var handle = NativeMethods.CreateFileW(
            interfacePath,
            desiredAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw CreateWin32Exception("open interface", error, interfacePath);
    }

    private void ValidateFrameInfo(
        PanelDisplayDriverContract.FrameInfoNative output,
        string operation)
    {
        if (output.Size != PanelDisplayDriverContract.FrameInfoSize)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported structure size {output.Size}; expected " +
                PanelDisplayDriverContract.FrameInfoSize);
        }

        if (!Enum.IsDefined(output.Result))
        {
            throw CreateInvalidDataException(
                operation,
                $"reported unknown frame result {(uint)output.Result}");
        }

        if (output.Result != PanelDisplayFrameResult.Ok)
        {
            return;
        }

        if (output.Generation == 0 || output.Sequence == 0)
        {
            throw CreateInvalidDataException(
                operation,
                "reported an available frame without generation/sequence");
        }

        if (output.Width == 0 || output.Height == 0 ||
            output.Width > 16_384 || output.Height > 16_384)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported invalid dimensions {output.Width}x{output.Height}");
        }

        if (output.Format is not (
                PanelDisplayFrameFormat.Bgra8888 or
                PanelDisplayFrameFormat.Rgba8888))
        {
            throw CreateInvalidDataException(
                operation,
                $"reported unsupported frame format {(uint)output.Format}");
        }

        var minimumStride = checked(output.Width * 4);
        var expectedPayload = checked((ulong)output.Stride * output.Height);
        if (output.Stride < minimumStride ||
            output.PayloadBytes != expectedPayload ||
            output.PayloadBytes >
                PanelDisplayDriverContract.MaximumFramePayloadBytes)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported invalid stride/payload {output.Stride}/" +
                output.PayloadBytes);
        }

        if (output.QpcFrequency == 0)
        {
            throw CreateInvalidDataException(
                operation,
                "reported a zero QPC frequency");
        }
    }

    private static byte[] StructureToBytes<T>(T value)
        where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }

    private string FindInterfacePath(CancellationToken cancellationToken)
    {
        var interfaceGuid = PanelDisplayDriverContract.DeviceInterfaceGuid;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = NativeMethods.CM_Get_Device_Interface_List_SizeW(
                out var characterCount,
                ref interfaceGuid,
                null,
                CmGetDeviceInterfaceListPresent);

            if (result == CrNoSuchValue ||
                (result == CrSuccess && characterCount < 2))
            {
                throw CreateWin32Exception(
                    "locate interface",
                    ErrorNotFound,
                    interfacePath: null);
            }

            if (result != CrSuccess)
            {
                throw CreateConfigurationManagerException(
                    "query interface list size",
                    result);
            }

            var buffer = new StringBuilder(checked((int)characterCount));
            result = NativeMethods.CM_Get_Device_Interface_ListW(
                ref interfaceGuid,
                null,
                buffer,
                characterCount,
                CmGetDeviceInterfaceListPresent);

            if (result == CrBufferSmall)
            {
                continue;
            }

            if (result == CrNoSuchValue)
            {
                throw CreateWin32Exception(
                    "locate interface",
                    ErrorNotFound,
                    interfacePath: null);
            }

            if (result != CrSuccess)
            {
                throw CreateConfigurationManagerException(
                    "enumerate interfaces",
                    result);
            }

            var path = buffer.ToString();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw CreateWin32Exception(
                    "locate interface",
                    ErrorNotFound,
                    interfacePath: null);
            }

            return path;
        }

        throw CreateWin32Exception(
            "enumerate interfaces",
            ErrorInvalidData,
            interfacePath: null);
    }

    private void ValidateCommand(
        PanelDisplayDriverContract.CommandOutputNative output,
        string operation)
    {
        if (output.Size != PanelDisplayDriverContract.CommandOutputSize)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported structure size {output.Size}; expected " +
                PanelDisplayDriverContract.CommandOutputSize);
        }

        if (!Enum.IsDefined(output.Result))
        {
            throw CreateInvalidDataException(
                operation,
                $"reported unknown command result {(int)output.Result}");
        }

        ValidateMonitorState(output.MonitorState, operation);
    }

    private void ValidateStatus(
        PanelDisplayDriverContract.StatusOutputNative output)
    {
        const string operation = "GET_STATUS";

        if (output.Size != PanelDisplayDriverContract.StatusOutputSize)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported structure size {output.Size}; expected " +
                PanelDisplayDriverContract.StatusOutputSize);
        }

        if (output.ProtocolVersion != PanelDisplayDriverContract.ProtocolVersion)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported protocol v{output.ProtocolVersion}; expected v" +
                PanelDisplayDriverContract.ProtocolVersion);
        }

        if (!Enum.IsDefined(output.DriverState))
        {
            throw CreateInvalidDataException(
                operation,
                $"reported unknown driver state {(int)output.DriverState}");
        }

        ValidateMonitorState(output.MonitorState, operation);

        if (output.ModeCount > PanelDisplayDriverContract.MaxReportedModes ||
            output.Modes is null ||
            output.Modes.Length != PanelDisplayDriverContract.MaxReportedModes)
        {
            throw CreateInvalidDataException(
                operation,
                $"reported invalid mode count/layout ({output.ModeCount})");
        }
    }

    private void ValidateMonitorState(
        PanelDisplayMonitorState monitorState,
        string operation)
    {
        if (!Enum.IsDefined(monitorState))
        {
            throw CreateInvalidDataException(
                operation,
                $"reported unknown monitor state {(int)monitorState}");
        }
    }

    private PanelDisplayDriverException CreateInvalidDataException(
        string operation,
        string detail) =>
        new(
            PanelDisplayDriverFailureKind.IoError,
            operation,
            $"PanelDisplayDriver {operation} returned invalid data: {detail}.",
            ErrorInvalidData,
            InterfacePath);

    private PanelDisplayDriverException CreateConfigurationManagerException(
        string operation,
        uint configurationManagerResult)
    {
        var error = checked((int)NativeMethods.CM_MapCrToWin32Err(
            configurationManagerResult,
            ErrorGenFailure));
        return CreateWin32Exception(operation, error, interfacePath: null);
    }

    private static PanelDisplayDriverException CreateWin32Exception(
        string operation,
        int error,
        string? interfacePath)
    {
        var kind = error switch
        {
            ErrorFileNotFound or
            ErrorPathNotFound or
            ErrorNoSuchDevice or
            ErrorDeviceNotConnected or
            ErrorNotFound => PanelDisplayDriverFailureKind.NotFound,
            ErrorAccessDenied => PanelDisplayDriverFailureKind.AccessDenied,
            _ => PanelDisplayDriverFailureKind.IoError,
        };

        var systemMessage = new Win32Exception(error).Message;
        var pathDetail = interfacePath is null
            ? string.Empty
            : $" Interface: {interfacePath}.";

        return new PanelDisplayDriverException(
            kind,
            operation,
            $"PanelDisplayDriver {operation} failed with Win32 error " +
            $"{error}: {systemMessage}.{pathDetail}",
            error,
            interfacePath);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData
    {
        internal IntPtr Internal;
        internal IntPtr InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal IntPtr EventHandle;
    }

    private sealed class PendingIoCancellation
    {
        private readonly SafeFileHandle _handle;
        private readonly IntPtr _nativeOverlapped;

        internal PendingIoCancellation(
            SafeFileHandle handle,
            IntPtr nativeOverlapped)
        {
            _handle = handle;
            _nativeOverlapped = nativeOverlapped;
        }

        internal void Cancel()
        {
            if (_handle.IsClosed || _handle.IsInvalid)
            {
                return;
            }

            // ERROR_NOT_FOUND is a benign completion race: the request won the
            // race with cancellation and its event will still be observed.
            _ = NativeMethods.CancelIoEx(_handle, _nativeOverlapped);
        }
    }

    private static class NativeMethods
    {
        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        internal static extern uint CM_Get_Device_Interface_List_SizeW(
            out uint bufferLength,
            ref Guid interfaceClassGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string? deviceId,
            uint flags);

        [DllImport(
            "cfgmgr32.dll",
            CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        internal static extern uint CM_Get_Device_Interface_ListW(
            ref Guid interfaceClassGuid,
            [MarshalAs(UnmanagedType.LPWStr)] string? deviceId,
            StringBuilder deviceInterfaceList,
            uint deviceInterfaceListLength,
            uint flags);

        [DllImport("cfgmgr32.dll", ExactSpelling = true)]
        internal static extern uint CM_MapCrToWin32Err(
            uint configurationManagerResult,
            uint defaultError);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport(
            "kernel32.dll",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            IntPtr bytesReturned,
            IntPtr overlapped);

        [DllImport(
            "kernel32.dll",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelIoEx(
            SafeFileHandle device,
            IntPtr overlapped);

        [DllImport(
            "kernel32.dll",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetOverlappedResult(
            SafeFileHandle device,
            IntPtr overlapped,
            out uint bytesTransferred,
            [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
