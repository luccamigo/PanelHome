namespace PanelHost.Transport;

internal sealed record CarrierLimits(bool IsWifi)
{
    public static CarrierLimits Cable { get; } = new(false);
    public static CarrierLimits WifiV1 { get; } = new(true);
    public const int WifiFrameBytes = 64 * 1024;
    public const int ChannelBytes = 2 * 1024 * 1024;
    public const int SessionBytes = 8 * 1024 * 1024;
    public const int WorkspaceBytes = WifiFrameBytes + 16 * 1024;
    public void Validate(PanelChannelId channel, int bytes)
    {
        if (IsWifi && (bytes is < 0 or > WifiFrameBytes || channel is PanelChannelId.Assets or PanelChannelId.Audio))
            throw new InvalidDataException("WiFi carrier limit exceeded.");
    }
}

internal sealed class CarrierBudget(CarrierLimits limits)
{
    private readonly object _gate = new();
    private readonly long[] _bytes = new long[4];
    private readonly int[] _items = new int[4];
    public long LiveBytes { get; private set; } = limits.IsWifi ? CarrierLimits.WorkspaceBytes : 0;
    public IDisposable? Reserve(bool outbound, PanelChannelId channel, int bytes)
    {
        limits.Validate(channel, bytes);
        if (!limits.IsWifi) return null;
        var index = (outbound ? 2 : 0) + (channel == PanelChannelId.Video ? 1 : 0);
        lock (_gate)
        {
            if (_items[index] >= (channel == PanelChannelId.Video ? 32 : 64) ||
                bytes > CarrierLimits.ChannelBytes - _bytes[index] || bytes > CarrierLimits.SessionBytes - LiveBytes)
                throw new InvalidDataException("WiFi carrier budget exceeded.");
            _items[index]++; _bytes[index] += bytes; LiveBytes += bytes;
            return new Lease(this, index, bytes);
        }
    }
    private sealed class Lease(CarrierBudget budget, int index, int bytes) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (budget._gate) { budget._items[index]--; budget._bytes[index] -= bytes; budget.LiveBytes -= bytes; }
        }
    }
}

// Opening a closed channel is valid; repeatedly cycling it is still bounded.
internal sealed class CarrierOpenRate(TimeProvider? clock = null, int maximum = 12)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<PanelChannelId, Queue<long>> _opens = new();
    public void Record(PanelChannelId channel)
    {
        if (!TryRecord(channel)) throw new InvalidDataException("WiFi channel reopen rate exceeded.");
    }
    public bool TryRecord(PanelChannelId channel)
    {
        if (!_opens.TryGetValue(channel, out var times)) _opens.Add(channel, times = new());
        var now = _clock.GetTimestamp();
        while (times.TryPeek(out var first) && _clock.GetElapsedTime(first, now) >= TimeSpan.FromMinutes(1)) times.Dequeue();
        if (times.Count >= maximum) return false;
        times.Enqueue(now);
        return true;
    }
}
