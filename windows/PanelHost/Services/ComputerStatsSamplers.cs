using PanelHost.Protocol;

namespace PanelHost.Services;

internal sealed record NetworkCounter(string Id, string Name, long? ReceivedBytes, long? SentBytes);
internal sealed record ProcessCounter(string Name, int Pid, long? StartTimeUtcTicks,
    double? CpuSeconds, long? MemoryBytes, string Owner, bool Protected);

internal sealed record ProcessListOptions(string Sort = "cpu", bool Grouped = false)
{
    internal static bool TryCreate(string? sort, bool? grouped, out ProcessListOptions options)
    {
        options = new();
        if (sort is not ("cpu" or "memory") || grouped is null) return false;
        options = new(sort, grouped.Value);
        return true;
    }
}

internal sealed class NetworkRateSampler
{
    private Dictionary<string, NetworkCounter> _previous = [];
    private double? _previousSeconds;

    internal NetworkRatesMessage Sample(IReadOnlyList<NetworkCounter> counters, double seconds, long unixMs)
    {
        var elapsed = seconds - _previousSeconds;
        var rates = counters.Select(current =>
        {
            _previous.TryGetValue(current.Id, out var before);
            double? Rate(long? value, long? old) => elapsed is > 0 && value >= old && old >= 0
                ? (value - old) / elapsed : null;
            return new NetworkAdapterRate(current.Id, current.Name,
                Rate(current.ReceivedBytes, before?.ReceivedBytes), Rate(current.SentBytes, before?.SentBytes));
        }).ToArray();
        _previous = counters.ToDictionary(x => x.Id);
        _previousSeconds = seconds;
        double? Total(Func<NetworkAdapterRate, double?> get) => rates.Length == 0 || rates.Any(x => get(x) is null)
            ? null : rates.Sum(x => get(x)!.Value);
        return new(unixMs, Total(x => x.DownloadBytesPerSecond), Total(x => x.UploadBytesPerSecond), rates);
    }
}

internal sealed class ProcessRateSampler(int logicalProcessors)
{
    private Dictionary<(int, long?), ProcessCounter> _previous = [];
    private double? _previousSeconds;

    internal ProcessesMessage Sample(IReadOnlyList<ProcessCounter> counters, double seconds, long unixMs, ProcessListOptions options)
    {
        var elapsed = seconds - _previousSeconds;
        var items = counters.Select(current =>
        {
            _previous.TryGetValue((current.Pid, current.StartTimeUtcTicks), out var before);
            var delta = current.CpuSeconds - before?.CpuSeconds;
            double? cpu = current.StartTimeUtcTicks is not null && elapsed is > 0 && delta is >= 0
                ? Math.Clamp(delta.Value / elapsed.Value / Math.Max(1, logicalProcessors) * 100, 0, 100) : null;
            return new ProcessListItem(current.Name, current.Pid, current.StartTimeUtcTicks, cpu,
                current.MemoryBytes, current.Owner, current.Protected);
        }).ToArray();
        _previous = counters.ToDictionary(x => (x.Pid, x.StartTimeUtcTicks));
        _previousSeconds = seconds;
        IEnumerable<ProcessListItem> selected = items;
        if (options.Grouped)
        {
            selected = items.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(group => new ProcessListItem(
                group.Key, null, null,
                group.Any(x => x.CpuPercent is null) ? null : group.Sum(x => x.CpuPercent),
                group.Any(x => x.MemoryBytes is null) ? null : group.Sum(x => x.MemoryBytes),
                group.Select(x => x.Owner).Distinct().Count() == 1 ? group.First().Owner : "unknown", true, group.Count()));
        }
        selected = options.Sort == "memory"
            ? selected.OrderByDescending(x => x.MemoryBytes).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Pid)
            : selected.OrderByDescending(x => x.CpuPercent).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Pid);
        return new(unixMs, options.Sort, options.Grouped, selected.Take(15).ToArray());
    }
}

internal interface IComputerStatsSource
{
    IReadOnlyList<NetworkCounter> ReadNetwork();
    IReadOnlyList<ProcessCounter> ReadProcesses();
}
