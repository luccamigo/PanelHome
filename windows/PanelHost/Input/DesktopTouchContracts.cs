using PanelHost.Services;

namespace PanelHost.Input;

internal enum DesktopTouchAction
{
    Down,
    Move,
    Up,
    Cancel,
}

internal readonly record struct NormalizedTouchCommand(
    DesktopTouchAction Action,
    int PointerId,
    double? X,
    double? Y,
    long Generation);

internal readonly record struct PanelDisplayBounds(
    int Left,
    int Top,
    int Width,
    int Height)
{
    internal int Right => checked(Left + Width);

    internal int Bottom => checked(Top + Height);

    internal bool IsValid => Width > 0 && Height > 0;
}

internal readonly record struct ScreenTouchPoint(int X, int Y);

internal readonly record struct TouchInjectionResult(
    bool Accepted,
    string? ErrorCode = null)
{
    internal static TouchInjectionResult Success => new(true);

    internal static TouchInjectionResult Ignore(string errorCode) =>
        new(false, errorCode);
}

internal readonly record struct TouchInjectionMetrics(
    long Received,
    long Injected,
    long Errors);

internal interface ITouchInjectionService : IDisposable
{
    bool IsAvailable { get; }

    long ActiveGeneration { get; }

    TouchInjectionMetrics Metrics { get; }

    void SetDesktopState(DesktopServiceSnapshot snapshot);

    TouchInjectionResult Process(NormalizedTouchCommand command);

    void CancelActiveContact(string reason);

    void DeactivateDesktop(string reason);
}

internal interface IPanelDisplayLocator
{
    bool TryLocate(out PanelDisplayBounds bounds, out string detail);
}

internal sealed class UnavailableTouchInjectionService : ITouchInjectionService
{
    internal static UnavailableTouchInjectionService Instance { get; } = new();

    public bool IsAvailable => false;

    public long ActiveGeneration => 0;

    public TouchInjectionMetrics Metrics => default;

    public void SetDesktopState(DesktopServiceSnapshot snapshot)
    {
    }

    public TouchInjectionResult Process(NormalizedTouchCommand command) =>
        TouchInjectionResult.Ignore("touch_unavailable");

    public void CancelActiveContact(string reason)
    {
    }

    public void DeactivateDesktop(string reason)
    {
    }

    public void Dispose()
    {
    }
}

internal static class PanelDisplayCoordinateMapper
{
    internal static ScreenTouchPoint Map(
        PanelDisplayBounds monitor,
        double normalizedX,
        double normalizedY)
    {
        if (!monitor.IsValid || !double.IsFinite(normalizedX) ||
            !double.IsFinite(normalizedY))
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizedX),
                "Touch coordinates and monitor bounds must be finite and valid.");
        }

        var x = Math.Clamp(normalizedX, 0d, 1d);
        var y = Math.Clamp(normalizedY, 0d, 1d);
        var screenX = checked(
            monitor.Left +
            (int)Math.Round(
                x * (monitor.Width - 1L),
                MidpointRounding.AwayFromZero));
        var screenY = checked(
            monitor.Top +
            (int)Math.Round(
                y * (monitor.Height - 1L),
                MidpointRounding.AwayFromZero));
        return new ScreenTouchPoint(
            Math.Clamp(screenX, monitor.Left, monitor.Right - 1),
            Math.Clamp(screenY, monitor.Top, monitor.Bottom - 1));
    }
}
