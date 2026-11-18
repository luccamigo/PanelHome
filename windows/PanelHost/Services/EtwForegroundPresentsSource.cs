using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using PanelHost.Protocol;

namespace PanelHost.Services;

/// <summary>Foreground app presents/s; falls back to same-image family (e.g. browser GPU subprocesses).</summary>
internal sealed class EtwForegroundPresentsSource : IDisposable
{
    private TraceEventSession? _session;
    private Task? _consumer;
    private readonly ForegroundPresentsCounter _counter = new();
    private volatile bool _available;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    internal void Start()
    {
        try
        {
            _session = new TraceEventSession($"PanelOS.ForegroundPresents.{Environment.ProcessId}.{Guid.NewGuid():N}") { StopOnDispose = true };
            _session.Source.Dynamic.All += e => { if ((int)e.ID == 184) _counter.Record(e.ProcessID); };
            _session.EnableProvider("Microsoft-Windows-DxgKrnl", TraceEventLevel.Informational, 0x8000000UL,
                new TraceEventProviderOptions { EventIDsToEnable = new List<int> { 184 } });
            _available = true;
            _consumer = Task.Run(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception) { }
                finally { _available = false; }
            });
        }
        catch (Exception) { _available = false; }
    }

    internal FpsTelemetry Sample()
    {
        if (!_available) return new();
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        string? name = null;
        try { using var process = Process.GetProcessById((int)pid); name = process.ProcessName; }
        catch (ArgumentException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return _counter.Sample((int)pid, name, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
            ForegroundProcessFamily.Read((int)pid));
    }

    public void Dispose()
    {
        _available = false;
        _session?.Dispose();
        try { _consumer?.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }
}

internal sealed class ForegroundPresentsCounter
{
    private readonly object _gate = new();
    private int _pid;
    private long _identity;
    private Dictionary<int, long> _targets = new();
    private readonly Dictionary<int, long> _counts = new();
    private double _start;

    internal void Record(int pid)
    {
        lock (_gate) { if (_targets.ContainsKey(pid) && pid > 0) _counts[pid] = _counts.GetValueOrDefault(pid) + 1; }
    }

    internal FpsTelemetry Sample(int pid, string? name, double now, IReadOnlyDictionary<int, long>? family = null)
    {
        lock (_gate)
        {
            family ??= pid > 0 ? new Dictionary<int, long> { [pid] = 0 } : new Dictionary<int, long>();
            var hasIdentity = family.TryGetValue(pid, out var identity);
            var changed = pid != _pid || identity != _identity;
            long ValidCount(int id) => family.TryGetValue(id, out var current) && _targets.TryGetValue(id, out var previous) && current == previous
                ? _counts.GetValueOrDefault(id) : 0;
            var count = ValidCount(pid);
            // Prefer direct presents to avoid double counting. Otherwise this is the app family's
            // presentation rate (a browser GPU process may compose multiple tabs), never video FPS.
            if (count == 0) count = family.Keys.Where(id => id != pid).Sum(ValidCount);
            _counts.Clear();
            _targets = new Dictionary<int, long>(family);
            _pid = pid;
            _identity = identity;
            var elapsed = now - _start;
            _start = now;
            if (changed || !hasIdentity || pid <= 0 || elapsed <= 0 || count == 0)
            {
                return new() { State = "no_app" };
            }
            var rate = count / elapsed;
            if (!double.IsFinite(rate) || rate <= 0 || rate > 10000) return new() { State = "no_app" };
            return new() { State = "measuring", AppName = name, PresentsPerSecond = Math.Round(rate, 1) };
        }
    }
}
