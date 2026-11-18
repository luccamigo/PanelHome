using System.Diagnostics;

namespace PanelHost.Video;

internal readonly record struct TimingStatistics(
    int Count,
    double AverageMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds)
{
    internal string Format(string name) => Count == 0
        ? $"{name}=n/a"
        : $"{name}={AverageMilliseconds:F2}/{P50Milliseconds:F2}/" +
            $"{P95Milliseconds:F2}ms(avg/p50/p95)";
}

internal sealed class PerformanceTimingWindow
{
    private readonly object _sync = new();
    private readonly List<long> _microseconds = new(256);

    internal void RecordElapsed(long startedTimestamp) =>
        RecordMicroseconds(
            Stopwatch.GetElapsedTime(startedTimestamp).Ticks / 10);

    internal void RecordMicroseconds(long microseconds)
    {
        lock (_sync)
        {
            _microseconds.Add(Math.Max(0, microseconds));
        }
    }

    internal TimingStatistics Drain()
    {
        long[] samples;
        lock (_sync)
        {
            if (_microseconds.Count == 0)
            {
                return default;
            }

            samples = [.. _microseconds];
            _microseconds.Clear();
        }

        Array.Sort(samples);
        var total = 0d;
        foreach (var sample in samples)
        {
            total += sample;
        }

        return new TimingStatistics(
            samples.Length,
            total / samples.Length / 1000d,
            samples[PercentileIndex(samples.Length, 0.50)] / 1000d,
            samples[PercentileIndex(samples.Length, 0.95)] / 1000d);
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);
}
