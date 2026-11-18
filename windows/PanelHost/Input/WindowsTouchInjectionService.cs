using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PanelHost.Services;

namespace PanelHost.Input;

internal sealed class WindowsTouchInjectionService : ITouchInjectionService
{
    private const int DiagnosticEventLimit = 8;
    private const int ErrorLogLimit = 5;
    private static readonly TimeSpan DefaultHoldUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MetricsInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly IWindowsTouchNativeApi _native;
    private readonly IPanelDisplayLocator _displayLocator;
    private readonly TextWriter _log;
    private readonly TimeSpan _holdUpdateInterval;
    private readonly TouchFeedbackMode _feedbackMode;
    private readonly bool _initialized;
    private bool _desktopRunning;
    private bool _contactActive;
    private bool _disposed;
    private long _nextGeneration;
    private long _activeGeneration;
    private PanelDisplayBounds _activeMonitor;
    private ScreenTouchPoint _lastPoint;
    private Timer? _holdTimer;
    private long _received;
    private long _injected;
    private long _errors;
    private long _metricsStarted = Environment.TickCount64;
    private int _diagnosticEvents;
    private int _errorLogs;
    private int _win32MoveLogs;
    private bool _contactStructureLogged;

    internal WindowsTouchInjectionService(TextWriter? log = null)
        : this(
            WindowsTouchNativeApi.Instance,
            new WindowsPanelDisplayLocator(),
            log,
            null,
            TouchFeedbackMode.None)
    {
    }

    internal WindowsTouchInjectionService(
        IWindowsTouchNativeApi native,
        IPanelDisplayLocator displayLocator,
        TextWriter? log = null,
        TimeSpan? holdUpdateInterval = null,
        TouchFeedbackMode feedbackMode = TouchFeedbackMode.None)
    {
        _native = native;
        _displayLocator = displayLocator;
        _log = log ?? Console.Out;
        _holdUpdateInterval = holdUpdateInterval ?? DefaultHoldUpdateInterval;
        _feedbackMode = feedbackMode;
        if (_holdUpdateInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(holdUpdateInterval));
        }
        try
        {
            var process = System.Diagnostics.Process.GetCurrentProcess();
            _log.WriteLine(
                $"[TouchDebug] ProcessId={Environment.ProcessId} " +
                $"SessionId={process.SessionId} UserInteractive={Environment.UserInteractive}");
            using (var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorV2())
            {
                var virtualScreen = ThreadDpiAwarenessScope.GetVirtualScreen();
                _log.WriteLine(
                    $"[TouchDebug] Touch DPI scope " +
                    $"perMonitorV2={dpiScope.IsActive.ToString().ToLowerInvariant()} " +
                    $"error={dpiScope.Error} " +
                    $"virtual=({virtualScreen.Left},{virtualScreen.Top} " +
                    $"{virtualScreen.Width}x{virtualScreen.Height})");
            }
            _initialized = _native.InitializeTouchInjection(
                1,
                _feedbackMode);
            _log.WriteLine(
                $"[TouchWin32] InitializeTouchInjection success={_initialized.ToString().ToLowerInvariant()} " +
                $"maxCount=1 feedback={_feedbackMode}({(uint)_feedbackMode})");
            if (!_initialized)
            {
                var error = _native.GetLastError();
                _log.WriteLine(
                    $"[Touch] InitializeTouchInjection unavailable: " +
                    $"Win32={error} (0x{error:X8}).");
            }
        }
        catch (Exception exception)
        {
            _log.WriteLine(
                $"[Touch] Native touch injection unavailable: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public bool IsAvailable => _initialized && !_disposed;

    public long ActiveGeneration
    {
        get
        {
            lock (_gate)
            {
                return _desktopRunning ? _activeGeneration : 0;
            }
        }
    }

    public TouchInjectionMetrics Metrics => new(
        Interlocked.Read(ref _received),
        Interlocked.Read(ref _injected),
        Interlocked.Read(ref _errors));

    public void SetDesktopState(DesktopServiceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (snapshot.State == DesktopServiceState.Running)
            {
                if (!_desktopRunning)
                {
                    _desktopRunning = true;
                    _activeGeneration = checked(++_nextGeneration);
                    _log.WriteLine(
                        $"[TouchDebug] HOST desktopRunning=true " +
                        $"generation={_activeGeneration}");
                }
                return;
            }

            CancelActiveContactCore($"Desktop {snapshot.State}");
            _desktopRunning = false;
            _activeGeneration = 0;
        }
    }

    public TouchInjectionResult Process(NormalizedTouchCommand command)
    {
        Interlocked.Increment(ref _received);
        lock (_gate)
        {
            if (!IsAvailable)
            {
                return TouchInjectionResult.Ignore("touch_unavailable");
            }
            if (!_desktopRunning)
            {
                return TouchInjectionResult.Ignore("desktop_not_running");
            }
            if (command.Generation != _activeGeneration)
            {
                return TouchInjectionResult.Ignore("invalid_generation");
            }
            if (command.PointerId != 0)
            {
                return TouchInjectionResult.Ignore("unsupported_pointer_id");
            }

            var result = command.Action switch
            {
                DesktopTouchAction.Down => ProcessDown(command),
                DesktopTouchAction.Move => ProcessMove(command),
                DesktopTouchAction.Up => ProcessUp(command),
                DesktopTouchAction.Cancel => ProcessCancel(),
                _ => TouchInjectionResult.Ignore("invalid_touch_action"),
            };
            MaybeLogMetrics();
            return result;
        }
    }

    public void CancelActiveContact(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            CancelActiveContactCore(reason);
        }
    }

    public void DeactivateDesktop(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_gate)
        {
            CancelActiveContactCore(reason);
            _desktopRunning = false;
            _activeGeneration = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CancelActiveContactCore("service dispose");
            _disposed = true;
            StopHoldUpdates();
        }
    }

    private TouchInjectionResult ProcessDown(NormalizedTouchCommand command)
    {
        if (_contactActive)
        {
            return TouchInjectionResult.Ignore("duplicate_touch_down");
        }
        if (!TryGetPoint(command, out var point, out var error))
        {
            return TouchInjectionResult.Ignore(error);
        }
        var normalized = FormatNormalized(command.X, command.Y);
        if (!_displayLocator.TryLocate(out _activeMonitor, out var identity))
        {
            LogError(
                $"display_not_found action=DOWN detail={identity} " +
                $"norm={normalized}");
            return TouchInjectionResult.Ignore("display_not_found");
        }

        point = PanelDisplayCoordinateMapper.Map(
            _activeMonitor,
            command.X!.Value,
            command.Y!.Value);
        _log.WriteLine(
            $"[TouchDebug] Panel display: {identity} " +
            $"left={_activeMonitor.Left} top={_activeMonitor.Top} " +
            $"width={_activeMonitor.Width} height={_activeMonitor.Height} " +
            $"norm={normalized} mapped=({point.X},{point.Y})");
        if (!Inject(point, PointerFlags.InRange | PointerFlags.InContact |
                PointerFlags.Down, "DOWN", command))
        {
            return TouchInjectionResult.Ignore("touch_injection_failed");
        }

        _contactActive = true;
        _lastPoint = point;
        StartHoldUpdates();
        LogEvent(
            $"DOWN norm={normalized} " +
            $"mapped monitor=({_activeMonitor.Left},{_activeMonitor.Top} " +
            $"{_activeMonitor.Width}x{_activeMonitor.Height}) " +
            $"screen=({point.X},{point.Y}) target={identity}");
        return TouchInjectionResult.Success;
    }

    private TouchInjectionResult ProcessMove(NormalizedTouchCommand command)
    {
        if (!_contactActive)
        {
            return TouchInjectionResult.Ignore("touch_move_without_down");
        }
        if (!TryGetPoint(command, out var point, out var error))
        {
            return TouchInjectionResult.Ignore(error);
        }

        point = PanelDisplayCoordinateMapper.Map(
            _activeMonitor,
            command.X!.Value,
            command.Y!.Value);
        if (!Inject(point, PointerFlags.InRange | PointerFlags.InContact |
                PointerFlags.Update, "MOVE", command))
        {
            EndFailedContact("MOVE_FAILURE_CANCEL");
            return TouchInjectionResult.Ignore("touch_injection_failed");
        }

        _lastPoint = point;
        return TouchInjectionResult.Success;
    }

    private TouchInjectionResult ProcessUp(NormalizedTouchCommand command)
    {
        if (!_contactActive)
        {
            return TouchInjectionResult.Ignore("touch_up_without_down");
        }
        if (!TryGetPoint(command, out var point, out var error))
        {
            return TouchInjectionResult.Ignore(error);
        }

        point = PanelDisplayCoordinateMapper.Map(
            _activeMonitor,
            command.X!.Value,
            command.Y!.Value);
        if (point != _lastPoint &&
            !Inject(point, PointerFlags.InRange | PointerFlags.InContact |
                PointerFlags.Update, "UP_FINAL_UPDATE", command))
        {
            EndFailedContact("UP_UPDATE_FAILURE_CANCEL");
            return TouchInjectionResult.Ignore("touch_injection_failed");
        }

        _lastPoint = point;
        var succeeded = Inject(point, PointerFlags.Up, "UP", command);
        if (!succeeded)
        {
            _ = Inject(
                point,
                PointerFlags.Canceled | PointerFlags.Up,
                "UP_FAILURE_CANCEL",
                command);
        }
        _contactActive = false;
        StopHoldUpdates();
        LogEvent($"UP screen=({point.X},{point.Y})");
        return succeeded
            ? TouchInjectionResult.Success
            : TouchInjectionResult.Ignore("touch_injection_failed");
    }

    private TouchInjectionResult ProcessCancel()
    {
        if (!_contactActive)
        {
            return TouchInjectionResult.Ignore("touch_cancel_without_down");
        }

        CancelActiveContactCore("Android ACTION_CANCEL");
        return TouchInjectionResult.Success;
    }

    private static bool TryGetPoint(
        NormalizedTouchCommand command,
        out ScreenTouchPoint point,
        out string error)
    {
        point = default;
        error = string.Empty;
        if (command.X is not double x || command.Y is not double y ||
            !double.IsFinite(x) || !double.IsFinite(y))
        {
            error = "invalid_touch_coordinates";
            return false;
        }

        return true;
    }

    private static string FormatNormalized(double? x, double? y) =>
        FormattableString.Invariant($"({x:F3},{y:F3})");

    private bool Inject(
        ScreenTouchPoint point,
        PointerFlags flags,
        string eventType,
        NormalizedTouchCommand? command = null)
    {
        var contact = WindowsTouchContact.Create(point, flags, _activeMonitor);
        if (!_contactStructureLogged)
        {
            _contactStructureLogged = true;
            var info = contact.PointerInfo;
            _log.WriteLine(
                $"[TouchWin32] POINTER_TOUCH_INFO pointerType={info.PointerType}({(uint)info.PointerType}) " +
                $"pointerId={info.PointerId} flags=0x{(uint)info.PointerFlags:X8} " +
                $"pixel=({info.PixelLocation.X},{info.PixelLocation.Y}) " +
                $"raw=({info.PixelLocationRaw.X},{info.PixelLocationRaw.Y}) " +
                $"dwTime={info.Time} performanceCount={info.PerformanceCount} " +
                $"touchFlags=0x{(uint)contact.TouchFlags:X8} " +
                $"touchMask=0x{(uint)contact.TouchMask:X8} " +
                $"contact=({contact.Contact.Left},{contact.Contact.Top}.." +
                $"{contact.Contact.Right},{contact.Contact.Bottom})");
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                if (_native.InjectTouchInput(1, [contact]))
                {
                    Interlocked.Increment(ref _injected);
                    LogWin32Call(eventType, flags, attempt, success: true, error: 0);
                    return true;
                }
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _errors);
                LogError(
                    $"InjectTouchInput threw {exception.GetType().Name} " +
                    $"event={eventType} screen=({point.X},{point.Y}): " +
                    exception.Message);
                return false;
            }

            var error = _native.GetLastError();
            LogWin32Call(eventType, flags, attempt, success: false, error);
            if (error == NativeErrors.NotReady && attempt == 1)
            {
                continue;
            }

            Interlocked.Increment(ref _errors);
            var normalized = command is { } value
                ? $" norm={FormatNormalized(value.X, value.Y)}"
                : string.Empty;
            LogError(
                $"InjectTouchInput failed Win32={error} (0x{error:X8} " +
                $"{new Win32Exception(error).Message}) " +
                $"event={eventType}{normalized} " +
                $"monitor=({_activeMonitor.Left},{_activeMonitor.Top} " +
                $"{_activeMonitor.Width}x{_activeMonitor.Height}) " +
                $"screen=({point.X},{point.Y})");
            return false;
        }

        return false;
    }

    private void CancelActiveContactCore(string reason)
    {
        if (!_contactActive)
        {
            StopHoldUpdates();
            return;
        }

        _ = Inject(
            _lastPoint,
            PointerFlags.Canceled | PointerFlags.Up,
            "CANCEL");
        _contactActive = false;
        StopHoldUpdates();
        LogEvent($"CANCEL reason={reason}");
    }

    private void EndFailedContact(string eventType)
    {
        if (_contactActive)
        {
            _ = Inject(
                _lastPoint,
                PointerFlags.Canceled | PointerFlags.Up,
                eventType);
        }
        _contactActive = false;
        StopHoldUpdates();
    }

    private void StartHoldUpdates()
    {
        StopHoldUpdates();
        _holdTimer = new Timer(
            static state => ((WindowsTouchInjectionService)state!).SendHoldUpdate(),
            this,
            _holdUpdateInterval,
            _holdUpdateInterval);
    }

    private void StopHoldUpdates()
    {
        _holdTimer?.Dispose();
        _holdTimer = null;
    }

    private void SendHoldUpdate()
    {
        lock (_gate)
        {
            if (_disposed || !_contactActive)
            {
                return;
            }

            if (!Inject(
                    _lastPoint,
                    PointerFlags.InRange | PointerFlags.InContact |
                    PointerFlags.Update,
                    "HOLD_UPDATE"))
            {
                EndFailedContact("HOLD_FAILURE_CANCEL");
            }
        }
    }

    private void LogWin32Call(
        string eventType,
        PointerFlags flags,
        int attempt,
        bool success,
        int error)
    {
        var moveLike = eventType.Contains("MOVE", StringComparison.Ordinal) ||
            eventType.Contains("UPDATE", StringComparison.Ordinal);
        if (moveLike && _win32MoveLogs++ >= 5 && success)
        {
            return;
        }

        var errorText = success
            ? string.Empty
            : $" error={error} (0x{error:X8} {new Win32Exception(error).Message})";
        _log.WriteLine(
            $"[TouchWin32] {eventType} success={success.ToString().ToLowerInvariant()} " +
            $"attempt={attempt} flags=0x{(uint)flags:X8}{errorText}");
    }

    private void LogEvent(string message)
    {
        if (_diagnosticEvents++ < DiagnosticEventLimit)
        {
            _log.WriteLine($"[Touch] {message}");
        }
    }

    private void LogError(string message)
    {
        if (_errorLogs++ < ErrorLogLimit)
        {
            _log.WriteLine($"[Touch] {message}");
        }
        else if (_errorLogs == ErrorLogLimit + 1)
        {
            _log.WriteLine("[Touch] Further touch errors are suppressed.");
        }
    }

    private void MaybeLogMetrics()
    {
        var now = Environment.TickCount64;
        if (now - _metricsStarted < MetricsInterval.TotalMilliseconds)
        {
            return;
        }

        var metrics = Metrics;
        _log.WriteLine(
            $"[Touch] metrics touchReceive={metrics.Received} " +
            $"touchInject={metrics.Injected} touchErrors={metrics.Errors}");
        _metricsStarted = now;
    }
}

internal interface IWindowsTouchNativeApi
{
    bool InitializeTouchInjection(uint maxCount, TouchFeedbackMode feedbackMode);

    bool InjectTouchInput(uint count, PointerTouchInfo[] contacts);

    int GetLastError();
}

internal sealed class WindowsTouchNativeApi : IWindowsTouchNativeApi
{
    internal static WindowsTouchNativeApi Instance { get; } = new();

    public bool InitializeTouchInjection(
        uint maxCount,
        TouchFeedbackMode feedbackMode) =>
        NativeMethods.InitializeTouchInjection(maxCount, feedbackMode);

    public bool InjectTouchInput(uint count, PointerTouchInfo[] contacts)
    {
        using var dpiScope = ThreadDpiAwarenessScope.EnterPerMonitorV2();
        return NativeMethods.InjectTouchInput(count, contacts);
    }

    public int GetLastError() => Marshal.GetLastPInvokeError();
}

internal enum TouchFeedbackMode : uint
{
    Default = 1,
    Indirect = 2,
    None = 3,
}

internal enum PointerInputType : uint
{
    Touch = 2,
}

[Flags]
internal enum PointerFlags : uint
{
    InRange = 0x00000002,
    InContact = 0x00000004,
    FirstButton = 0x00000010,
    Primary = 0x00002000,
    Confidence = 0x00004000,
    Canceled = 0x00008000,
    Down = 0x00010000,
    Update = 0x00020000,
    Up = 0x00040000,
}

[Flags]
internal enum TouchMask : uint
{
    ContactArea = 0x00000001,
}

[Flags]
internal enum TouchFlags : uint
{
    None = 0x00000000,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

[StructLayout(LayoutKind.Sequential)]
internal struct PointerInfo
{
    internal PointerInputType PointerType;
    internal uint PointerId;
    internal uint FrameId;
    internal PointerFlags PointerFlags;
    internal IntPtr SourceDevice;
    internal IntPtr WindowTarget;
    internal NativePoint PixelLocation;
    internal NativePoint HimetricLocation;
    internal NativePoint PixelLocationRaw;
    internal NativePoint HimetricLocationRaw;
    internal uint Time;
    internal uint HistoryCount;
    internal int InputData;
    internal uint KeyStates;
    internal ulong PerformanceCount;
    internal uint ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointerTouchInfo
{
    internal PointerInfo PointerInfo;
    internal TouchFlags TouchFlags;
    internal TouchMask TouchMask;
    internal NativeRect Contact;
    internal NativeRect ContactRaw;
    internal uint Orientation;
    internal uint Pressure;
}

internal static partial class NativeMethods
{
    [DllImport(
        "user32.dll",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InitializeTouchInjection(
        uint maxCount,
        TouchFeedbackMode feedbackMode);

    [DllImport(
        "user32.dll",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool InjectTouchInput(
        uint count,
        [In] PointerTouchInfo[] contacts);

    [DllImport(
        "user32.dll",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    internal static extern IntPtr SetThreadDpiAwarenessContext(
        IntPtr dpiContext);

    [DllImport(
        "user32.dll",
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    internal static extern int GetSystemMetrics(int index);
}

internal readonly struct ThreadDpiAwarenessScope : IDisposable
{
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private readonly IntPtr _previousContext;

    private ThreadDpiAwarenessScope(IntPtr previousContext, int error)
    {
        _previousContext = previousContext;
        Error = error;
    }

    internal bool IsActive => _previousContext != IntPtr.Zero;

    internal int Error { get; }

    internal static ThreadDpiAwarenessScope EnterPerMonitorV2()
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(
            PerMonitorAwareV2);
        var error = previous == IntPtr.Zero
            ? Marshal.GetLastPInvokeError()
            : 0;
        return new ThreadDpiAwarenessScope(previous, error);
    }

    internal static PanelDisplayBounds GetVirtualScreen() => new(
        NativeMethods.GetSystemMetrics(76),
        NativeMethods.GetSystemMetrics(77),
        NativeMethods.GetSystemMetrics(78),
        NativeMethods.GetSystemMetrics(79));

    public void Dispose()
    {
        if (_previousContext != IntPtr.Zero)
        {
            _ = NativeMethods.SetThreadDpiAwarenessContext(_previousContext);
        }
    }
}

internal static class WindowsTouchContact
{
    // InjectTouchInput treats the single initialized contact as slot zero. Using
    // pointerId=1 with maxCount=1 is rejected by Windows with ERROR_INVALID_PARAMETER.
    private const uint NativePointerId = 0;

    internal static PointerTouchInfo Create(
        ScreenTouchPoint point,
        PointerFlags flags,
        PanelDisplayBounds monitor)
    {
        const int radius = 2;
        var contact = new NativeRect(
            Math.Clamp(point.X - radius, monitor.Left, monitor.Right - 1),
            Math.Clamp(point.Y - radius, monitor.Top, monitor.Bottom - 1),
            Math.Clamp(point.X + radius + 1, monitor.Left + 1, monitor.Right),
            Math.Clamp(point.Y + radius + 1, monitor.Top + 1, monitor.Bottom));
        return new PointerTouchInfo
        {
            PointerInfo = new PointerInfo
            {
                PointerType = PointerInputType.Touch,
                PointerId = NativePointerId,
                PointerFlags = flags,
                PixelLocation = new NativePoint(point.X, point.Y),
                // Raw device-space fields are not synthesized by this API.
                // Fabricating them from screen coordinates produces Win32 87.
                Time = 0,
                PerformanceCount = 0,
            },
            TouchFlags = TouchFlags.None,
            TouchMask = TouchMask.ContactArea,
            Contact = contact,
        };
    }
}
