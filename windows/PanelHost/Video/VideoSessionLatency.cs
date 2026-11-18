using System.Diagnostics;

namespace PanelHost.Video;

internal static class VideoSessionLatency
{
    private const ulong MaximumLatencyMicros = 60_000_000u;

    internal static ulong GetCurrentTimestampMicros() => checked((ulong)(
        (UInt128)(ulong)Stopwatch.GetTimestamp() * 1_000_000u /
        (ulong)Stopwatch.Frequency));

    internal static bool TryCalculate(
        ulong sessionStartedMicros,
        ulong captureTimestampMicros,
        ulong sendTimestampMicros,
        out long latencyMicros)
    {
        latencyMicros = 0;
        if (captureTimestampMicros == 0 ||
            captureTimestampMicros < sessionStartedMicros ||
            sendTimestampMicros < captureTimestampMicros ||
            sendTimestampMicros - captureTimestampMicros > MaximumLatencyMicros)
        {
            return false;
        }

        latencyMicros = checked((long)(sendTimestampMicros - captureTimestampMicros));
        return true;
    }
}
