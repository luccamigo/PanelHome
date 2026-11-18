using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PanelHost.Transport;

/// <summary>
/// Discovers WinUSB interfaces, performs the standard Android Open Accessory
/// transition, and exposes the accessory bulk endpoints as a duplex stream.
/// </summary>
internal static class WindowsUsbAccessory
{
    private static readonly Guid AndroidUsbInterface =
        new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B");
    private static readonly Guid PanelOsAccessoryInterface =
        new("4F9B4A5C-7A77-4B21-9E4A-5C4C54D98E9D");
    private static readonly TimeSpan ReenumerationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    internal static async ValueTask<Stream> OpenAsync(
        string? explicitPath,
        TextWriter log,
        CancellationToken cancellationToken,
        NativeUsbTransportOptions? options = null,
        TransportIoProfile? profile = null)
    {
        options ??= NativeUsbTransportOptions.FromEnvironment();
        profile ??= new TransportIoProfile();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitStream = WinUsbPipeStream.Open(explicitPath, options, profile, out var explicitDevice);
            LogEndpointDiagnostics(log, explicitDevice, explicitPath);
            log.WriteLine($"[NativeUSB] {options}");
            return explicitStream;
        }

        if (TryOpenAccessory(log, options, profile, out var accessory, out var accessoryPath))
        {
            log.WriteLine($"[USB] Opened AOA bulk interface {Redact(accessoryPath)}.");
            return accessory;
        }

        var androidCandidates = DeviceInterfaceDiscovery.Enumerate(AndroidUsbInterface).ToArray();
        log.WriteLine($"[USB] Discovered {androidCandidates.Length} Android WinUSB interface path(s).");
        string? transitionPath = null;
        foreach (var candidate in androidCandidates)
        {
            try
            {
                using var device = WinUsbDevice.Open(candidate);
                log.WriteLine(
                    $"[USB] Candidate {Redact(candidate)} VID={device.Descriptor.VendorId:X4} " +
                    $"PID={device.Descriptor.ProductId:X4} interface=" +
                    $"{device.InterfaceClass:X2}/{device.InterfaceSubClass:X2}/{device.InterfaceProtocol:X2}.");
                if (!device.Descriptor.IsSupportedAccessory)
                {
                    transitionPath = candidate;
                    break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"[USB] Cannot open candidate {Redact(candidate)}: {exception.Message}");
            }
        }
        if (transitionPath is null)
        {
            throw new IOException(
                "No accessible Android WinUSB interface was found for AOA transition. " +
                "Close adb (adb kill-server) and verify the OEM USB driver exposes WinUSB.");
        }

        log.WriteLine($"[USB] Starting Android Open Accessory transition through {Redact(transitionPath)}.");
        using (var control = WinUsbDevice.Open(transitionPath))
        {
            AndroidOpenAccessory.Start(control);
        }

        var deadline = DateTime.UtcNow + ReenumerationTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryOpenAccessory(log, options, profile, out accessory, out accessoryPath))
            {
                log.WriteLine($"[USB] AOA re-enumeration complete on {Redact(accessoryPath)}.");
                return accessory;
            }
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        // "Device re-enumerated but MI_00 is not WinUSB-bound" and "device never
        // re-enumerated at all" are different failures with different fixes, and
        // a single message cannot serve both. A successful START_ACCESSORY only
        // means the control transfer was accepted - it is not evidence that the
        // device switched. Any VID_18D1&PID_2D01 interface appearing is that
        // evidence, so look for one before blaming driver binding: on
        // 2026-08-19 the binding message sent the operator after a non-existent
        // driver problem for hours while the tablet's USB stack was wedged.
        var accessoryDeviceAppeared = DeviceInterfaceDiscovery
            .Enumerate(AndroidUsbInterface)
            .Any(path => path.Contains("vid_18d1&pid_2d01", StringComparison.OrdinalIgnoreCase));

        throw new IOException(accessoryDeviceAppeared
            ? "Android re-enumerated into AOA mode but its accessory data interface is not WinUSB-bound. " +
              "Bind the AOA accessory interface (USB\\VID_18D1&PID_2D01&MI_00, class FF/FF/00) to WinUSB " +
              $"with interface GUID {{{PanelOsAccessoryInterface}}}; do not bind the ADB FF/42/01 interface."
            : "Android accepted START_ACCESSORY but never re-enumerated into AOA mode within 15 seconds: " +
              "no VID_18D1&PID_2D01 interface appeared. The device did not perform the transition, so this " +
              "is not a host driver-binding problem. Check that the tablet is unlocked and that PanelHome " +
              "is installed, and note that this device's USB stack can wedge after the cable is pulled " +
              "mid-session - 'adb shell dumpsys usb' then reports an empty current_functions, and only " +
              "rebooting the tablet clears it.");
    }

    private static bool TryOpenAccessory(
        TextWriter log,
        NativeUsbTransportOptions options,
        TransportIoProfile profile,
        out Stream stream,
        out string path)
    {
        var candidates = DeviceInterfaceDiscovery.Enumerate(PanelOsAccessoryInterface)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length > 0)
            log.WriteLine($"[NativeUSB] PanelOS interface GUID discovered {candidates.Length} path(s).");
        foreach (var candidate in candidates)
        {
            try
            {
                var device = WinUsbDevice.Open(candidate);
                if (!device.IsSupportedAccessoryDataInterface)
                {
                    log.WriteLine(
                        $"[NativeUSB] Rejected {Redact(candidate)}: VID={device.Descriptor.VendorId:X4}, " +
                        $"PID={device.Descriptor.ProductId:X4}, interface=MI_{device.InterfaceNumber:X2} " +
                        $"{device.InterfaceClass:X2}/{device.InterfaceSubClass:X2}/{device.InterfaceProtocol:X2}.");
                    device.Dispose();
                    continue;
                }
                LogEndpointDiagnostics(log, device, candidate);
                log.WriteLine($"[NativeUSB] {options}");
                stream = new WinUsbPipeStream(device, options, profile);
                path = candidate;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"[NativeUSB] Cannot open {Redact(candidate)}: {exception.Message}");
            }
        }
        stream = Stream.Null;
        path = string.Empty;
        return false;
    }

    private static void LogEndpointDiagnostics(TextWriter log, WinUsbDevice device, string path) =>
        log.WriteLine(
            $"[NativeUSB] interface=MI_{device.InterfaceNumber:X2} driver=WinUSB " +
            $"bulkIn=0x{device.ReadPipe!.Value:X2} bulkOut=0x{device.WritePipe!.Value:X2} " +
            $"maxPacketSize={Math.Min(device.ReadMaximumPacketSize!.Value, device.WriteMaximumPacketSize!.Value)} " +
            $"path={Redact(path)}.");

    private static string Redact(string path) =>
        path.Length <= 48 ? path : $"{path[..40]}...{path[^8..]}";
}

internal static class AndroidOpenAccessory
{
    private const byte VendorIn = 0xC0;
    private const byte VendorOut = 0x40;
    private const byte GetProtocol = 51;
    private const byte SendString = 52;
    private const byte StartAccessory = 53;

    internal static void Start(WinUsbDevice device)
    {
        var protocolBytes = new byte[2];
        device.ControlTransfer(VendorIn, GetProtocol, 0, 0, protocolBytes);
        var protocol = BinaryPrimitives.ReadUInt16LittleEndian(protocolBytes);
        if (protocol == 0)
        {
            throw new IOException("The connected Android device reported no AOA support.");
        }

        Send(device, 0, "PanelOS");
        Send(device, 1, "PanelHost");
        Send(device, 2, "PanelOS native USB transport");
        Send(device, 3, "1");
        Send(device, 4, "https://panelos.local/");
        Send(device, 5, "PanelOS");
        device.ControlTransfer(VendorOut, StartAccessory, 0, 0, []);
    }

    private static void Send(WinUsbDevice device, ushort index, string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + '\0');
        if (bytes.Length > 256) throw new InvalidOperationException("AOA identity string exceeds 256 bytes.");
        device.ControlTransfer(VendorOut, SendString, 0, index, bytes);
    }
}

internal readonly record struct UsbDeviceDescriptor(ushort VendorId, ushort ProductId)
{
    internal bool IsSupportedAccessory => VendorId == 0x18D1 && ProductId == 0x2D01;
}

internal static class AoaInterfaceMatcher
{
    private const string RequiredHardwareId = "USB\\VID_18D1&PID_2D01&MI_00";

    internal static bool IsSupportedHardwareId(IEnumerable<string> hardwareIds) =>
        hardwareIds.Any(id => string.Equals(id, RequiredHardwareId, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSupportedDescriptor(
        UsbDeviceDescriptor device,
        UsbInterfaceDescriptor usbInterface) =>
        device.IsSupportedAccessory &&
        usbInterface.InterfaceNumber == 0 &&
        usbInterface.InterfaceClass == 0xFF &&
        usbInterface.InterfaceSubClass == 0xFF &&
        usbInterface.InterfaceProtocol == 0;
}

internal readonly record struct WinUsbEndpoint(byte PipeId, UsbdPipeType PipeType, ushort MaximumPacketSize);

internal readonly record struct WinUsbEndpointSelection(WinUsbEndpoint Read, WinUsbEndpoint Write)
{
    internal static WinUsbEndpointSelection Select(IEnumerable<WinUsbEndpoint> endpoints)
    {
        WinUsbEndpoint? read = null;
        WinUsbEndpoint? write = null;
        foreach (var endpoint in endpoints)
        {
            if (endpoint.PipeType != UsbdPipeType.Bulk) continue;
            if ((endpoint.PipeId & 0x80) != 0) read ??= endpoint;
            else write ??= endpoint;
        }
        if (read is null || write is null)
        {
            throw new IOException(
                $"AOA MI_00 requires bulk IN and bulk OUT endpoints; " +
                $"found bulk IN={read.HasValue}, bulk OUT={write.HasValue}.");
        }
        return new WinUsbEndpointSelection(read.Value, write.Value);
    }
}

internal static class Win32Error
{
    internal static string Describe(int error)
    {
        var name = error switch
        {
            2 => "ERROR_FILE_NOT_FOUND",
            5 => "ERROR_ACCESS_DENIED",
            31 => "ERROR_GEN_FAILURE",
            _ => "WIN32_ERROR",
        };
        return $"{name}={error}: {new Win32Exception(error).Message}";
    }
}

internal sealed class WinUsbDevice : IDisposable
{
    private readonly SafeFileHandle _file;
    private IntPtr _interfaceHandle;
    private bool _disposed;

    private WinUsbDevice(SafeFileHandle file, IntPtr interfaceHandle)
    {
        _file = file;
        _interfaceHandle = interfaceHandle;
        Descriptor = ReadDeviceDescriptor();
        var descriptor = QueryInterfaceSettings();
        InterfaceNumber = descriptor.InterfaceNumber;
        InterfaceClass = descriptor.InterfaceClass;
        InterfaceSubClass = descriptor.InterfaceSubClass;
        InterfaceProtocol = descriptor.InterfaceProtocol;
        var endpoints = QueryEndpoints(descriptor.NumberOfEndpoints);
        if (AoaInterfaceMatcher.IsSupportedDescriptor(Descriptor, descriptor))
        {
            var selection = WinUsbEndpointSelection.Select(endpoints);
            ReadPipe = selection.Read.PipeId;
            WritePipe = selection.Write.PipeId;
            ReadMaximumPacketSize = selection.Read.MaximumPacketSize;
            WriteMaximumPacketSize = selection.Write.MaximumPacketSize;
        }
    }

    internal UsbDeviceDescriptor Descriptor { get; }
    internal byte InterfaceNumber { get; }
    internal byte InterfaceClass { get; }
    internal byte InterfaceSubClass { get; }
    internal byte InterfaceProtocol { get; }
    internal byte? ReadPipe { get; }
    internal byte? WritePipe { get; }
    internal ushort? ReadMaximumPacketSize { get; }
    internal ushort? WriteMaximumPacketSize { get; }
    internal bool IsSupportedAccessoryDataInterface =>
        Descriptor.IsSupportedAccessory && InterfaceNumber == 0 &&
        InterfaceClass == 0xFF && InterfaceSubClass == 0xFF && InterfaceProtocol == 0 &&
        ReadPipe.HasValue && WritePipe.HasValue;

    internal static WinUsbDevice Open(string path)
    {
        var file = NativeMethods.CreateFile(
            path,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagOverlapped,
            IntPtr.Zero);
        if (file.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"Cannot open WinUSB device interface ({Win32Error.Describe(error)}).");
        }
        if (!NativeMethods.WinUsbInitialize(file, out var interfaceHandle))
        {
            var error = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new IOException($"WinUsb_Initialize failed ({Win32Error.Describe(error)}).");
        }
        try
        {
            return new WinUsbDevice(file, interfaceHandle);
        }
        catch
        {
            NativeMethods.WinUsbFree(interfaceHandle);
            file.Dispose();
            throw;
        }
    }

    internal void ControlTransfer(byte requestType, byte request, ushort value, ushort index, byte[] buffer)
    {
        var setup = new WinUsbSetupPacket(requestType, request, value, index, checked((ushort)buffer.Length));
        if (!NativeMethods.WinUsbControlTransfer(
                _interfaceHandle,
                setup,
                buffer,
                checked((uint)buffer.Length),
                out var transferred,
                IntPtr.Zero) || transferred != buffer.Length)
        {
            throw new IOException(
                $"WinUSB control request {request} failed (error {Marshal.GetLastWin32Error()}, " +
                $"transferred {transferred}/{buffer.Length}).");
        }
    }

    internal SafeFileHandle FileHandle => _file;
    internal IntPtr InterfaceHandle => _interfaceHandle;

    /// <summary>
    /// Synchronous bulk read straight into the caller's span. No staging array
    /// is allocated: the span is pinned for the duration of the call only.
    /// </summary>
    internal unsafe int Read(Span<byte> target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (target.IsEmpty) return 0;
        var pipe = ReadPipe ?? throw new IOException("WinUSB interface has no bulk IN endpoint.");
        while (true)
        {
            uint transferred;
            bool completed;
            fixed (byte* buffer = target)
            {
                completed = NativeMethods.WinUsbReadPipe(
                    _interfaceHandle,
                    pipe,
                    buffer,
                    checked((uint)target.Length),
                    &transferred,
                    null);
            }
            if (!completed)
            {
                var error = Marshal.GetLastWin32Error();
                throw new IOException($"WinUSB bulk read failed ({Win32Error.Describe(error)}).");
            }
            // A USB zero-length packet terminates a transfer whose byte count
            // is an exact multiple of maxPacketSize. It is not stream EOF.
            if (transferred == 0) continue;
            return checked((int)transferred);
        }
    }

    /// <summary>
    /// Synchronous bulk write straight out of the caller's span. No staging
    /// array is allocated.
    /// </summary>
    internal unsafe void Write(ReadOnlySpan<byte> source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (source.IsEmpty) return;
        var pipe = WritePipe ?? throw new IOException("WinUSB interface has no bulk OUT endpoint.");
        uint transferred;
        bool completed;
        fixed (byte* buffer = source)
        {
            completed = NativeMethods.WinUsbWritePipe(
                _interfaceHandle,
                pipe,
                buffer,
                checked((uint)source.Length),
                &transferred,
                null);
        }
        if (!completed || transferred != source.Length)
        {
            throw new IOException(
                $"WinUSB bulk write failed (error {Marshal.GetLastWin32Error()}, " +
                $"transferred {transferred}/{source.Length}).");
        }
    }

    /// <summary>
    /// Bounds how long a bulk OUT transfer may sit unacknowledged. Failure is
    /// non-fatal: an older WinUSB that rejects the policy simply keeps the
    /// default infinite wait.
    /// </summary>
    internal void SetWriteTimeout(int milliseconds)
    {
        if (milliseconds <= 0 || WritePipe is not { } pipe) return;
        var value = checked((uint)milliseconds);
        NativeMethods.WinUsbSetPipePolicy(
            _interfaceHandle,
            pipe,
            NativeMethods.PipeTransferTimeout,
            sizeof(uint),
            in value);
    }

    /// <summary>
    /// Brings both bulk pipes back to a known state when a session opens.
    ///
    /// A previous PanelHost that exited without a bus reset leaves the endpoint
    /// data toggles out of step and can leave data cached in the host stack.
    /// WinUsb_ResetPipe issues CLEAR_FEATURE(ENDPOINT_HALT), which resynchronizes
    /// the toggle on both ends, and WinUsb_FlushPipe drops whatever the host had
    /// already buffered for the previous session. Neither replaces PLMX session
    /// identity - the peer can still send stale frames after this point, and the
    /// resync handles those - but without it the accessory never even sees the
    /// new HELLO.
    /// </summary>
    internal void ResetPipes(TextWriter? log = null)
    {
        if (ReadPipe is { } readPipe)
        {
            var reset = NativeMethods.WinUsbResetPipe(_interfaceHandle, readPipe);
            var flushed = NativeMethods.WinUsbFlushPipe(_interfaceHandle, readPipe);
            log?.WriteLine($"[NativeUSB] bulkIn 0x{readPipe:X2} reset={reset} flush={flushed}");
        }
        if (WritePipe is { } writePipe)
        {
            var reset = NativeMethods.WinUsbResetPipe(_interfaceHandle, writePipe);
            log?.WriteLine($"[NativeUSB] bulkOut 0x{writePipe:X2} reset={reset}");
        }
    }

    internal void AbortPipes()
    {
        if (_interfaceHandle == IntPtr.Zero) return;
        if (ReadPipe is { } readPipe) NativeMethods.WinUsbAbortPipe(_interfaceHandle, readPipe);
        if (WritePipe is { } writePipe) NativeMethods.WinUsbAbortPipe(_interfaceHandle, writePipe);
    }

    private UsbDeviceDescriptor ReadDeviceDescriptor()
    {
        var bytes = new byte[18];
        if (!NativeMethods.WinUsbGetDescriptor(
                _interfaceHandle,
                1,
                0,
                0,
                bytes,
                checked((uint)bytes.Length),
                out var transferred) || transferred != bytes.Length)
        {
            throw new IOException($"Cannot read USB device descriptor (error {Marshal.GetLastWin32Error()}).");
        }
        return new UsbDeviceDescriptor(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10)));
    }

    private UsbInterfaceDescriptor QueryInterfaceSettings()
    {
        if (!NativeMethods.WinUsbQueryInterfaceSettings(_interfaceHandle, 0, out var descriptor))
        {
            throw new IOException($"Cannot query WinUSB interface (error {Marshal.GetLastWin32Error()}).");
        }
        return descriptor;
    }

    private IReadOnlyList<WinUsbEndpoint> QueryEndpoints(byte count)
    {
        var endpoints = new List<WinUsbEndpoint>(count);
        for (byte index = 0; index < count; index++)
        {
            if (!NativeMethods.WinUsbQueryPipe(_interfaceHandle, 0, index, out var pipe))
            {
                throw new IOException($"Cannot query WinUSB pipe {index} (error {Marshal.GetLastWin32Error()}).");
            }
            endpoints.Add(new WinUsbEndpoint(pipe.PipeId, pipe.PipeType, pipe.MaximumPacketSize));
        }
        return endpoints;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AbortPipes();
        if (_interfaceHandle != IntPtr.Zero)
        {
            NativeMethods.WinUsbFree(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
        }
        _file.Dispose();
    }
}

/// <summary>
/// Tunables for the native carrier. They are options rather than constants so a
/// buffer/fragment sweep can be run against the same binary.
/// </summary>
internal sealed record NativeUsbTransportOptions(
    int ReadBufferBytes,
    int WriteChunkBytes,
    int WriteTimeoutMilliseconds = NativeUsbTransportOptions.DefaultWriteTimeoutMilliseconds)
{
    internal const int MinimumBufferBytes = 512;
    internal const int MaximumBufferBytes = 4 * 1024 * 1024;
    internal const int DefaultReadBufferBytes = 64 * 1024;
    internal const int DefaultWriteChunkBytes = 256 * 1024;

    /// <summary>
    /// Bulk OUT transfers only stall when the peer has stopped reading, which
    /// on this carrier means the accessory session is gone. Bounding the wait
    /// turns that into a disconnect the reconnect path can act on instead of a
    /// writer thread parked forever. Bulk IN keeps its infinite timeout because
    /// an idle link legitimately has nothing to deliver.
    /// </summary>
    internal const int DefaultWriteTimeoutMilliseconds = 5_000;

    internal static NativeUsbTransportOptions Default { get; } =
        new(DefaultReadBufferBytes, DefaultWriteChunkBytes);

    internal static NativeUsbTransportOptions FromEnvironment() => new(
        Sanitize(Environment.GetEnvironmentVariable("PANELOS_USB_READ_BUFFER"), DefaultReadBufferBytes),
        Sanitize(Environment.GetEnvironmentVariable("PANELOS_USB_WRITE_CHUNK"), DefaultWriteChunkBytes));

    /// <summary>
    /// Staging sizes must stay a multiple of the 512-byte bulk max packet so a
    /// chunked write never injects a short packet in the middle of a frame and
    /// a read request is never smaller than a packet the device may send.
    /// </summary>
    internal static int Sanitize(string? text, int fallback)
    {
        if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value))
            return fallback;
        value = Math.Clamp(value, MinimumBufferBytes, MaximumBufferBytes);
        return value - (value % MinimumBufferBytes);
    }

    public override string ToString() =>
        $"readBuffer={ReadBufferBytes}B writeChunk={WriteChunkBytes}B writeTimeout={WriteTimeoutMilliseconds}ms";
}

/// <summary>
/// Buffered, overlapped duplex stream over the AOA bulk endpoints.
///
/// Two properties matter for CPU. First, every transfer uses real OVERLAPPED
/// I/O completed on the thread pool's completion port, so no worker thread is
/// parked inside a blocking WinUSB call and no work item is queued per
/// operation. Second, reads are staged through one pinned buffer and writes
/// through another, so steady-state traffic performs no per-operation
/// allocation and no per-operation pinning.
/// </summary>
internal sealed class WinUsbPipeStream : Stream
{
    private readonly WinUsbDevice? _device;
    private readonly TransportIoProfile _profile;
    private readonly ThreadPoolBoundHandle? _boundHandle;
    private readonly IBulkPipe _reader;
    private readonly IBulkPipe _writer;
    private readonly byte[] _readBuffer;
    private int _readOffset;
    private int _readLength;
    private int _disposed;

    internal WinUsbPipeStream(
        WinUsbDevice device,
        NativeUsbTransportOptions? options = null,
        TransportIoProfile? profile = null)
    {
        options ??= NativeUsbTransportOptions.Default;
        _device = device;
        _profile = profile ?? new TransportIoProfile();
        var readPipe = device.ReadPipe ?? throw new IOException("WinUSB interface has no bulk IN endpoint.");
        var writePipe = device.WritePipe ?? throw new IOException("WinUSB interface has no bulk OUT endpoint.");
        _boundHandle = ThreadPoolBoundHandle.BindHandle(device.FileHandle);
        device.SetWriteTimeout(options.WriteTimeoutMilliseconds);
        device.ResetPipes();
        // Pinned-object-heap staging keeps the native pointers handed to WinUSB
        // valid for the whole lifetime of an overlapped transfer.
        _readBuffer = GC.AllocateUninitializedArray<byte>(options.ReadBufferBytes, pinned: true);
        _reader = new WinUsbPipeOperation(_boundHandle, device, readPipe, _readBuffer, isRead: true);
        _writer = new WinUsbPipeOperation(
            _boundHandle,
            device,
            writePipe,
            GC.AllocateUninitializedArray<byte>(options.WriteChunkBytes, pinned: true),
            isRead: false);
    }

    /// <summary>
    /// Test seam. The buffering, zero-length-packet and chunking rules live in
    /// this stream rather than in the P/Invoke layer, so they can be exercised
    /// against a fake pipe instead of requiring an attached tablet.
    /// </summary>
    internal WinUsbPipeStream(IBulkPipe reader, IBulkPipe writer, TransportIoProfile? profile = null)
    {
        _reader = reader;
        _writer = writer;
        _readBuffer = reader.Buffer;
        _profile = profile ?? new TransportIoProfile();
    }

    internal static Stream Open(
        string path,
        NativeUsbTransportOptions options,
        TransportIoProfile profile,
        out WinUsbDevice device)
    {
        var opened = WinUsbDevice.Open(path);
        if (!opened.IsSupportedAccessoryDataInterface)
        {
            opened.Dispose();
            throw new IOException(
                "The selected WinUSB path is not USB\\VID_18D1&PID_2D01&MI_00 " +
                "with an FF/FF/00 interface and bulk IN/OUT endpoints.");
        }
        device = opened;
        return new WinUsbPipeStream(opened, options, profile);
    }

    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanSeek => false;
    public override bool CanWrite => Volatile.Read(ref _disposed) == 0;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_readLength == 0)
        {
            // The staging request is always the full buffer, so the device may
            // answer with any transfer size it likes. A short packet - which is
            // how every PLMX frame ends - still completes the read immediately,
            // so buffering costs no latency.
            var started = Stopwatch.GetTimestamp();
            var (transferred, completedInline) = await _reader
                .TransferAsync(_readBuffer.Length, cancellationToken)
                .ConfigureAwait(false);
            _profile.PipeRead(transferred, started, completedInline);
            // A zero-length packet terminates a transfer that is an exact
            // multiple of maxPacketSize. It is not stream EOF, so read again.
            _readOffset = 0;
            _readLength = transferred;
        }
        var count = Math.Min(buffer.Length, _readLength);
        _readBuffer.AsSpan(_readOffset, count).CopyTo(buffer.Span);
        _readOffset += count;
        _readLength -= count;
        _profile.BufferedReadHit(count);
        return count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var chunk = _writer.Buffer.Length;
        for (var offset = 0; offset < buffer.Length; offset += chunk)
        {
            var count = Math.Min(chunk, buffer.Length - offset);
            buffer.Span.Slice(offset, count).CopyTo(_writer.Buffer);
            var started = Stopwatch.GetTimestamp();
            var (transferred, completedInline) = await _writer
                .TransferAsync(count, cancellationToken)
                .ConfigureAwait(false);
            _profile.PipeWrite(transferred, started, completedInline);
            if (transferred != count)
                throw new IOException($"WinUSB bulk write moved {transferred} of {count} bytes.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Aborting first completes any in-flight overlapped transfer with
            // ERROR_OPERATION_ABORTED, so a cable pull, a cancelled session or
            // host shutdown can never leave an I/O pending. Each direction then
            // drains before its overlapped state is released.
            _device?.AbortPipes();
            _reader.Dispose();
            _writer.Dispose();
            if (_reader.Drained && _writer.Drained) _boundHandle?.Dispose();
            _device?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One direction of the bulk pipe. At most one transfer is in flight at a time
/// because the multiplexed session owns exactly one reader and one writer loop;
/// that invariant is what lets the pinned staging buffer and the overlapped
/// structure be reused instead of reallocated per operation.
/// </summary>
/// <summary>
/// One direction of a bulk pipe, expressed as a transfer against a fixed
/// staging buffer.
/// </summary>
internal interface IBulkPipe : IDisposable
{
    byte[] Buffer { get; }
    bool Drained { get; }
    ValueTask<(int Transferred, bool CompletedInline)> TransferAsync(int count, CancellationToken cancellationToken);
}

internal sealed class WinUsbPipeOperation : IBulkPipe
{
    private const int ErrorIoPending = 997;
    private const int ErrorOperationAborted = 995;
    private const int DrainTimeoutMilliseconds = 2_000;

    private readonly ThreadPoolBoundHandle _boundHandle;
    private readonly WinUsbDevice _device;
    private readonly byte _pipeId;
    private readonly bool _isRead;
    private readonly PreAllocatedOverlapped _preAllocated;
    private readonly byte[] _buffer;
    private readonly unsafe byte* _bufferPointer;
    private TaskCompletionSource<int>? _pending;
    private nint _overlapped;
    private int _disposed;

    internal unsafe WinUsbPipeOperation(
        ThreadPoolBoundHandle boundHandle,
        WinUsbDevice device,
        byte pipeId,
        byte[] buffer,
        bool isRead)
    {
        _boundHandle = boundHandle;
        _device = device;
        _pipeId = pipeId;
        _isRead = isRead;
        _buffer = buffer;
        // The buffer lives on the pinned object heap, so its address is stable
        // for the whole session and can be handed to WinUSB for an overlapped
        // transfer that outlives any single managed statement.
        _bufferPointer = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
            ref MemoryMarshal.GetArrayDataReference(buffer));
        _preAllocated = new PreAllocatedOverlapped(OnCompleted, state: null, pinData: null);
    }

    public byte[] Buffer => _buffer;

    public async ValueTask<(int Transferred, bool CompletedInline)> TransferAsync(
        int count,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (count == 0) return (0, true);

        var pending = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _pending, pending, null) is not null)
            throw new InvalidOperationException($"A WinUSB {Direction} is already in flight.");

        var completedInline = Begin(count);
        // The handle is bound to the completion port and FILE_SKIP_COMPLETION_
        // PORT_ON_SUCCESS is deliberately not set, so a completion packet is
        // queued for the inline case too and the callback is the only place
        // that finishes the task.
        using var registration = cancellationToken.Register(
            static state => ((WinUsbPipeOperation)state!).CancelInFlight(),
            this);
        var transferred = await pending.Task.ConfigureAwait(false);
        return (transferred, completedInline);
    }

    private unsafe bool Begin(int count)
    {
        var overlapped = _boundHandle.AllocateNativeOverlapped(_preAllocated);
        Volatile.Write(ref _overlapped, (nint)overlapped);
        var completedInline = _isRead
            ? NativeMethods.WinUsbReadPipe(_device.InterfaceHandle, _pipeId, _bufferPointer, checked((uint)count), null, overlapped)
            : NativeMethods.WinUsbWritePipe(_device.InterfaceHandle, _pipeId, _bufferPointer, checked((uint)count), null, overlapped);
        if (completedInline) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == ErrorIoPending) return false;
        Release();
        throw new IOException($"WinUSB bulk {Direction} failed ({Win32Error.Describe(error)}).");
    }

    private string Direction => _isRead ? "read" : "write";

    private unsafe void CancelInFlight()
    {
        var overlapped = Volatile.Read(ref _overlapped);
        if (overlapped == 0) return;
        NativeMethods.CancelIoEx(_device.FileHandle, (NativeOverlapped*)overlapped);
    }

    private unsafe void OnCompleted(uint errorCode, uint numberOfBytes, NativeOverlapped* overlapped)
    {
        var pending = Release();
        if (pending is null) return;
        if (errorCode == 0) pending.TrySetResult(checked((int)numberOfBytes));
        else if (errorCode == ErrorOperationAborted)
            pending.TrySetException(new OperationCanceledException($"The WinUSB {Direction} was cancelled."));
        else
            pending.TrySetException(new IOException(
                $"WinUSB bulk {Direction} failed ({Win32Error.Describe(checked((int)errorCode))})."));
    }

    private unsafe TaskCompletionSource<int>? Release()
    {
        var overlapped = Interlocked.Exchange(ref _overlapped, 0);
        if (overlapped != 0) _boundHandle.FreeNativeOverlapped((NativeOverlapped*)overlapped);
        return Interlocked.Exchange(ref _pending, null);
    }

    /// <summary>True once no overlapped transfer is outstanding.</summary>
    public bool Drained => Volatile.Read(ref _overlapped) == 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // WinUsb_AbortPipe has already been issued by the owning stream, so any
        // in-flight transfer completes as ERROR_OPERATION_ABORTED and the
        // callback frees the overlapped. CancelIoEx covers the case where the
        // driver never reported the abort.
        CancelInFlight();
        var deadline = Environment.TickCount64 + DrainTimeoutMilliseconds;
        while (!Drained && Environment.TickCount64 < deadline) Thread.Sleep(1);
        Interlocked.Exchange(ref _pending, null)?.TrySetException(
            new ObjectDisposedException(nameof(WinUsbPipeOperation)));
        // Releasing the overlapped bookkeeping while the driver still owns the
        // structure would corrupt the completion port, so a transfer that never
        // drained keeps its state instead.
        if (Drained) _preAllocated.Dispose();
    }
}

internal static class DeviceInterfaceDiscovery
{
    internal static IEnumerable<string> Enumerate(Guid interfaceClass)
    {
        var deviceInfo = NativeMethods.SetupDiGetClassDevs(
            ref interfaceClass,
            null,
            IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
        if (deviceInfo == NativeMethods.InvalidHandleValue) yield break;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(
                        deviceInfo,
                        IntPtr.Zero,
                        ref interfaceClass,
                        index,
                        ref data))
                {
                    if (Marshal.GetLastWin32Error() == NativeMethods.ErrorNoMoreItems) yield break;
                    continue;
                }
                NativeMethods.SetupDiGetDeviceInterfaceDetail(
                    deviceInfo,
                    ref data,
                    IntPtr.Zero,
                    0,
                    out var required,
                    IntPtr.Zero);
                if (required == 0) continue;
                var detail = Marshal.AllocHGlobal(checked((int)required));
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            deviceInfo,
                            ref data,
                            detail,
                            required,
                            out _,
                            IntPtr.Zero)) continue;
                    var path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                    if (!string.IsNullOrWhiteSpace(path)) yield return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfo);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SpDeviceInterfaceData
{
    internal int Size;
    internal Guid InterfaceClassGuid;
    internal int Flags;
    internal UIntPtr Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal readonly struct WinUsbSetupPacket(
    byte requestType,
    byte request,
    ushort value,
    ushort index,
    ushort length)
{
    internal readonly byte RequestType = requestType;
    internal readonly byte Request = request;
    internal readonly ushort Value = value;
    internal readonly ushort Index = index;
    internal readonly ushort Length = length;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UsbInterfaceDescriptor
{
    internal byte Length;
    internal byte DescriptorType;
    internal byte InterfaceNumber;
    internal byte AlternateSetting;
    internal byte NumberOfEndpoints;
    internal byte InterfaceClass;
    internal byte InterfaceSubClass;
    internal byte InterfaceProtocol;
    internal byte Interface;
}

internal enum UsbdPipeType
{
    Control,
    Isochronous,
    Bulk,
    Interrupt,
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinUsbPipeInformation
{
    internal UsbdPipeType PipeType;
    internal byte PipeId;
    internal ushort MaximumPacketSize;
    internal byte Interval;
}

internal static partial class NativeMethods
{
    internal static readonly IntPtr InvalidHandleValue = new(-1);
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint FileFlagOverlapped = 0x40000000;
    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const int ErrorNoMoreItems = 259;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parent,
        uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfo,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfo,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr detailData,
        uint detailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfo);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Initialize", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbInitialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_Free", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbFree(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ControlTransfer", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbControlTransfer(
        IntPtr interfaceHandle,
        WinUsbSetupPacket setupPacket,
        [In, Out] byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred,
        IntPtr overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_GetDescriptor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbGetDescriptor(
        IntPtr interfaceHandle,
        byte descriptorType,
        byte index,
        ushort languageId,
        [Out] byte[] buffer,
        uint bufferLength,
        out uint lengthTransferred);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryInterfaceSettings", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbQueryInterfaceSettings(
        IntPtr interfaceHandle,
        byte alternateSettingNumber,
        out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_QueryPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbQueryPipe(
        IntPtr interfaceHandle,
        byte alternateInterfaceNumber,
        byte pipeIndex,
        out WinUsbPipeInformation pipeInformation);

    // Pointer-shaped so a transfer can run against pinned staging memory with
    // no marshalling copy, and so a NULL OVERLAPPED selects the synchronous
    // form while a real one selects completion-port delivery.
    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ReadPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinUsbReadPipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        uint* lengthTransferred,
        NativeOverlapped* overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_WritePipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WinUsbWritePipe(
        IntPtr interfaceHandle,
        byte pipeId,
        byte* buffer,
        uint bufferLength,
        uint* lengthTransferred,
        NativeOverlapped* overlapped);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_AbortPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbAbortPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_ResetPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbResetPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_FlushPipe", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbFlushPipe(IntPtr interfaceHandle, byte pipeId);

    internal const uint PipeTransferTimeout = 0x03;

    [LibraryImport("winusb.dll", EntryPoint = "WinUsb_SetPipePolicy", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WinUsbSetPipePolicy(
        IntPtr interfaceHandle,
        byte pipeId,
        uint policyType,
        uint valueLength,
        in uint value);

    [LibraryImport("kernel32.dll", EntryPoint = "CancelIoEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);
}
