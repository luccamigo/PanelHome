using System.Diagnostics;

namespace PanelDisplayBroker;

/// <summary>
/// PAN-3: ends a broker that cannot end itself. A driver call that never returns blocks a
/// thread in a synchronous wait, so the orderly path (cancel, DISCONNECT, exit) never runs
/// and the elevated process lingers with the driver handle, out of the host's reach (the host
/// is Medium IL and cannot terminate it). The watchdog runs on its own thread, so it does not
/// depend on the thread pool or on any of the stuck work, and it forces the process to exit.
/// Exiting closes the driver handle and the control pipe, which is the only release available.
/// </summary>
internal sealed class BrokerWatchdog
{
    internal const int RequestTimeoutExitCode = 7;
    internal const int ShutdownTimeoutExitCode = 6;

    private readonly Func<long> _now;
    private readonly Action<int, string> _expired;
    private readonly TimeSpan _poll;
    private long _requestDeadline;
    private long _shutdownDeadline;

    internal BrokerWatchdog(
        Action<int, string> expired,
        TimeSpan? poll = null,
        Func<long>? now = null)
    {
        _expired = expired;
        _poll = poll ?? TimeSpan.FromMilliseconds(250);
        _now = now ?? (() => Stopwatch.GetTimestamp());
        var thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "PanelDisplayBroker.Watchdog",
        };
        thread.Start();
    }

    /// <summary>Marks a driver operation in flight. Dispose when it returns.</summary>
    internal IDisposable Request(TimeSpan limit)
    {
        var deadline = Deadline(limit);
        Volatile.Write(ref _requestDeadline, deadline);
        return new Scope(this, deadline);
    }

    /// <summary>Once shutdown starts it must finish within the limit; this is never disarmed.</summary>
    internal void ArmShutdown(TimeSpan limit)
    {
        _ = Interlocked.CompareExchange(ref _shutdownDeadline, Deadline(limit), 0);
    }

    private long Deadline(TimeSpan limit) =>
        _now() + (long)(limit.TotalSeconds * Stopwatch.Frequency);

    private void Run()
    {
        while (true)
        {
            Thread.Sleep(_poll);
            var now = _now();
            var shutdown = Volatile.Read(ref _shutdownDeadline);
            if (shutdown != 0 && now >= shutdown)
            {
                _expired(ShutdownTimeoutExitCode, "shutdown did not complete");
                return;
            }

            var request = Volatile.Read(ref _requestDeadline);
            if (request != 0 && now >= request)
            {
                _expired(RequestTimeoutExitCode, "a driver operation did not return");
                return;
            }
        }
    }

    private sealed class Scope(BrokerWatchdog owner, long deadline) : IDisposable
    {
        public void Dispose() =>
            _ = Interlocked.CompareExchange(ref owner._requestDeadline, 0, deadline);
    }
}
