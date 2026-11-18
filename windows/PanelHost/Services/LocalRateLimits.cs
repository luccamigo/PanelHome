namespace PanelHost.Services;

internal sealed class LocalTokenBucket(double perSecond, double capacity)
{
    private double _tokens = capacity;
    private readonly double _capacity = capacity;
    private double? _last;
    internal bool Take(double now)
    {
        if (_last is double before) _tokens = Math.Min(_capacity, _tokens + Math.Max(0, now - before) * perSecond);
        _last = now;
        if (_tokens < 1) return false;
        _tokens--;
        return true;
    }
}

internal sealed class LocalRateLimits
{
    private readonly LocalTokenBucket _all = new(10, 20);
    private readonly LocalTokenBucket _notices = new(2, 5);
    private readonly Queue<double> _failures = new();
    private double _blockedUntil;
    internal bool IsBlocked(double now) => now < _blockedUntil;
    internal void AuthenticationFailed(double now)
    {
        while (_failures.TryPeek(out var before) && before <= now - 60) _failures.Dequeue();
        _failures.Enqueue(now);
        if (_failures.Count >= 10) { _blockedUntil = now + 60; _failures.Clear(); }
    }
    internal bool Take(double now, bool notice) => _all.Take(now) && (!notice || _notices.Take(now));
}
