using System.Diagnostics;
using PanelHost.Protocol;

namespace PanelHost.Services;

internal sealed class ComputerStatsSession(bool network, bool processes, IComputerStatsSource source)
{
    private readonly NetworkRateSampler _network = new();
    private readonly ProcessRateSampler _processes = new(Environment.ProcessorCount);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _nextProcesses;
    private ProcessListOptions _options = new();
    private HashSet<(int, long)> _offered = [];
    private HashSet<(int, long)> _listedProtected = [];
    internal bool Processes => processes;
    internal bool Offered(int? pid, long? ticks) => pid is int p && ticks is long t && Volatile.Read(ref _offered).Contains((p, t));
    // E1: a process the last list showed as protected answers "protected", not "already closed".
    internal bool ListedProtected(int? pid, long? ticks) => pid is int p && ticks is long t && Volatile.Read(ref _listedProtected).Contains((p, t));

    internal bool SetOptions(string? sort, bool? grouped)
    {
        if (!ProcessListOptions.TryCreate(sort, grouped, out var options)) return false;
        Volatile.Write(ref _options, options);
        return true;
    }

    internal IEnumerable<object> Sample()
    {
        var seconds = _clock.Elapsed.TotalSeconds;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (network) yield return _network.Sample(source.ReadNetwork(), seconds, now);
        if (processes && seconds >= _nextProcesses)
        {
            _nextProcesses = seconds + 1.9; // The caller ticks once a second; tolerate scheduler jitter.
            var sample = _processes.Sample(source.ReadProcesses(), seconds, now, Volatile.Read(ref _options));
            Volatile.Write(ref _offered, sample.Items.Where(x => x.Pid is not null && x.StartTimeUtcTicks is not null && !x.Protected)
                .Select(x => (x.Pid!.Value, x.StartTimeUtcTicks!.Value)).ToHashSet());
            Volatile.Write(ref _listedProtected, sample.Items.Where(x => x.Pid is not null && x.StartTimeUtcTicks is not null && x.Protected)
                .Select(x => (x.Pid!.Value, x.StartTimeUtcTicks!.Value)).ToHashSet());
            yield return sample;
        }
    }
}
