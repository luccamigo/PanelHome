using System.Runtime.InteropServices;

namespace PanelHost.Video;

/// <summary>
/// The Win32 multimedia timer period, behind an interface so the scope's
/// begin/end balance can be tested without touching the real system clock.
/// </summary>
internal interface ITimerResolutionApi
{
    bool TryBegin(uint periodMilliseconds);

    bool TryEnd(uint periodMilliseconds);
}

internal sealed partial class Win32TimerResolutionApi : ITimerResolutionApi
{
    internal static readonly Win32TimerResolutionApi Instance = new();

    private const uint TimerrNoError = 0;

    public bool TryBegin(uint periodMilliseconds) =>
        TimeBeginPeriod(periodMilliseconds) == TimerrNoError;

    public bool TryEnd(uint periodMilliseconds) =>
        TimeEndPeriod(periodMilliseconds) == TimerrNoError;

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}

/// <summary>
/// Raises this process's timer resolution while a video session is polling and
/// releases it when the session ends.
/// </summary>
/// <remarks>
/// <para>
/// Windows resolves timer resolution <em>per process</em> since version 2004,
/// so a browser or media player holding the system at 1 ms does nothing for
/// PanelHost. Without this scope a <see cref="PeriodicTimer"/> falls back to
/// the default ~15.6 ms granularity and the frame poll runs near 64 Hz however
/// small an interval it was handed.
/// </para>
/// <para>
/// Measured on 2026-08-26 against the 8.2645 ms interval
/// <see cref="VideoFramePollingCadence"/> asks for: 63.9 Hz with the system
/// already at 1 ms, and 125.0 Hz once this process requested the period for
/// itself. At 64 Hz the loop undersamples a ~58 FPS publisher, and a
/// latest-only reader drops every frame it laps - 25 to 41 per five-second
/// window in the bench session that produced this fix.
/// </para>
/// <para>
/// A raised resolution costs power process-wide, so it is scoped to the video
/// session instead of being taken for the lifetime of the host. Both pipelines
/// poll at or below the default granularity - H.264 at 8.26 ms, zlib at
/// 16.67 ms - so both want it.
/// </para>
/// </remarks>
internal sealed class TimerResolutionScope : IDisposable
{
    /// <summary>
    /// The finest period Windows accepts on a normal desktop. A coarser one
    /// leaves the poll interval quantized onto the wrong side of a tick, which
    /// is the whole defect being fixed.
    /// </summary>
    internal const uint RequestedPeriodMilliseconds = 1;

    private readonly ITimerResolutionApi _api;
    private readonly TextWriter _log;
    private bool _released;

    private TimerResolutionScope(ITimerResolutionApi api, TextWriter log, bool raised)
    {
        _api = api;
        _log = log;
        IsRaised = raised;
    }

    /// <summary>
    /// Whether the request succeeded. A refusal is reported and survivable: the
    /// session still runs, it just cannot reach its design cadence.
    /// </summary>
    internal bool IsRaised { get; }

    internal static TimerResolutionScope Acquire(
        int pollsPerSecond,
        TextWriter log,
        ITimerResolutionApi? api = null)
    {
        api ??= Win32TimerResolutionApi.Instance;
        var raised = api.TryBegin(RequestedPeriodMilliseconds);
        log.WriteLine(raised
            ? $"[VideoPacing] Timer resolution raised to " +
              $"{RequestedPeriodMilliseconds}ms for this session; " +
              $"poll target {pollsPerSecond} Hz."
            : $"[VideoPacing] Timer resolution request of " +
              $"{RequestedPeriodMilliseconds}ms was refused; the poll loop will " +
              $"be held near 64 Hz by the default granularity instead of " +
              $"{pollsPerSecond} Hz.");
        return new TimerResolutionScope(api, log, raised);
    }

    public void Dispose()
    {
        // Never end a period that was not begun: timeEndPeriod is refcounted
        // process-wide, and an unbalanced call would lower the resolution out
        // from under whatever else in this process had raised it.
        if (_released || !IsRaised)
        {
            return;
        }

        _released = true;
        if (!_api.TryEnd(RequestedPeriodMilliseconds))
        {
            _log.WriteLine(
                $"[VideoPacing] Releasing the {RequestedPeriodMilliseconds}ms " +
                $"timer period failed; it stays raised for this process.");
            return;
        }

        _log.WriteLine(
            $"[VideoPacing] Timer resolution released back to the default.");
    }
}
