using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PanelHost.Input;

internal static class TouchSelfTest
{
    internal static async Task<int> RunAsync(
        bool usePrimaryMonitor,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        using var process = Process.GetCurrentProcess();
        log.WriteLine("PanelHost touch self-test");
        log.WriteLine("=========================");
        log.WriteLine(
            $"[TouchDebug] ProcessId={Environment.ProcessId} " +
            $"SessionId={process.SessionId} UserInteractive={Environment.UserInteractive} " +
            $"Architecture={RuntimeInformation.ProcessArchitecture}");
        log.WriteLine(
            "[TouchDebug] SDK flags " +
            $"INRANGE=0x{(uint)PointerFlags.InRange:X8} " +
            $"INCONTACT=0x{(uint)PointerFlags.InContact:X8} " +
            $"FIRSTBUTTON=0x{(uint)PointerFlags.FirstButton:X8} " +
            $"PRIMARY=0x{(uint)PointerFlags.Primary:X8} " +
            $"CONFIDENCE=0x{(uint)PointerFlags.Confidence:X8} " +
            $"CANCELED=0x{(uint)PointerFlags.Canceled:X8} " +
            $"DOWN=0x{(uint)PointerFlags.Down:X8} " +
            $"UPDATE=0x{(uint)PointerFlags.Update:X8} " +
            $"UP=0x{(uint)PointerFlags.Up:X8}");

        IPanelDisplayLocator locator = usePrimaryMonitor
            ? new WindowsPrimaryDisplayLocator()
            : new WindowsPanelDisplayLocator();
        if (!locator.TryLocate(out var monitor, out var identity))
        {
            log.WriteLine($"[TouchDebug] DISPLAY_REJECT reason={identity}");
            return 3;
        }

        var point = PanelDisplayCoordinateMapper.Map(monitor, 0.5, 0.5);
        log.WriteLine(
            $"[TouchDebug] Self-test target={(usePrimaryMonitor ? "primary" : "PanelOS")} " +
            $"{identity} left={monitor.Left} top={monitor.Top} " +
            $"width={monitor.Width} height={monitor.Height} " +
            $"norm=(0.500,0.500) mapped=({point.X},{point.Y})");

        var native = WindowsTouchNativeApi.Instance;
        bool initialized;
        try
        {
            initialized = native.InitializeTouchInjection(1, TouchFeedbackMode.Indirect);
        }
        catch (Exception exception)
        {
            log.WriteLine(
                $"[TouchWin32] InitializeTouchInjection threw " +
                $"{exception.GetType().Name}: {exception.Message}");
            return 4;
        }

        if (!initialized)
        {
            var error = native.GetLastError();
            log.WriteLine(
                $"[TouchWin32] InitializeTouchInjection success=false " +
                $"error={DescribeError(error)} feedback=Indirect(2)");
            return 4;
        }

        log.WriteLine(
            "[TouchWin32] InitializeTouchInjection success=true " +
            "maxCount=1 feedback=Indirect(2)");

        var basicSucceeded = await InjectTapAsync(
            native,
            monitor,
            point,
            label: "BASIC",
            downFlags: PointerFlags.InRange | PointerFlags.InContact | PointerFlags.Down,
            upFlags: PointerFlags.Up,
            log,
            cancellationToken);

        log.WriteLine(
            $"[TouchDebug] SELF_TEST_RESULT " +
            $"success={basicSucceeded.ToString().ToLowerInvariant()}");
        return basicSucceeded ? 0 : 5;
    }

    private static async Task<bool> InjectTapAsync(
        IWindowsTouchNativeApi native,
        PanelDisplayBounds monitor,
        ScreenTouchPoint point,
        string label,
        PointerFlags downFlags,
        PointerFlags upFlags,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var down = WindowsTouchContact.Create(point, downFlags, monitor);
        LogContact(label, down, log);
        if (!InjectFrame(native, down, $"{label}_DOWN", log))
        {
            return false;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        var up = WindowsTouchContact.Create(point, upFlags, monitor);
        if (InjectFrame(native, up, $"{label}_UP", log))
        {
            return true;
        }

        var cancel = WindowsTouchContact.Create(
            point,
            PointerFlags.Canceled | PointerFlags.Up,
            monitor);
        _ = InjectFrame(native, cancel, $"{label}_CANCEL_AFTER_FAILURE", log);
        return false;
    }

    private static bool InjectFrame(
        IWindowsTouchNativeApi native,
        PointerTouchInfo contact,
        string label,
        TextWriter log)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            bool succeeded;
            try
            {
                succeeded = native.InjectTouchInput(1, [contact]);
            }
            catch (Exception exception)
            {
                log.WriteLine(
                    $"[TouchWin32] {label} threw {exception.GetType().Name}: " +
                    exception.Message);
                return false;
            }

            if (succeeded)
            {
                log.WriteLine(
                    $"[TouchWin32] {label} success=true attempt={attempt} " +
                    $"flags=0x{(uint)contact.PointerInfo.PointerFlags:X8}");
                return true;
            }

            var error = native.GetLastError();
            log.WriteLine(
                $"[TouchWin32] {label} success=false attempt={attempt} " +
                $"flags=0x{(uint)contact.PointerInfo.PointerFlags:X8} " +
                $"error={DescribeError(error)}");
            if (error != NativeErrors.NotReady || attempt != 1)
            {
                return false;
            }
        }

        return false;
    }

    private static void LogContact(
        string label,
        PointerTouchInfo contact,
        TextWriter log)
    {
        var info = contact.PointerInfo;
        log.WriteLine(
            $"[TouchWin32] {label} structure pointerType={info.PointerType}({(uint)info.PointerType}) " +
            $"pointerId={info.PointerId} pixel=({info.PixelLocation.X},{info.PixelLocation.Y}) " +
            $"raw=({info.PixelLocationRaw.X},{info.PixelLocationRaw.Y}) " +
            $"dwTime={info.Time} performanceCount={info.PerformanceCount} " +
            $"touchFlags=0x{(uint)contact.TouchFlags:X8} " +
            $"touchMask=0x{(uint)contact.TouchMask:X8} " +
            $"contact=({contact.Contact.Left},{contact.Contact.Top}.." +
            $"{contact.Contact.Right},{contact.Contact.Bottom})");
    }

    private static string DescribeError(int error) =>
        $"{error} (0x{error:X8} {new Win32Exception(error).Message})";
}

internal sealed class WindowsPrimaryDisplayLocator : IPanelDisplayLocator
{
    public bool TryLocate(out PanelDisplayBounds bounds, out string detail)
    {
        var width = TouchSelfTestNativeMethods.GetSystemMetrics(0);
        var height = TouchSelfTestNativeMethods.GetSystemMetrics(1);
        bounds = new PanelDisplayBounds(0, 0, width, height);
        detail = "friendlyName=\"Primary monitor\" devicePath=\"GetSystemMetrics\"";
        return bounds.IsValid;
    }
}

internal static class TouchSelfTestNativeMethods
{
    [DllImport(
        "user32.dll",
        CallingConvention = CallingConvention.Winapi,
        ExactSpelling = true)]
    internal static extern int GetSystemMetrics(int index);
}
