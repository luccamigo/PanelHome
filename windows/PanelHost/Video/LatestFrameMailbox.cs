namespace PanelHost.Video;

/// <summary>
/// Single-slot producer/consumer handoff. Publishing replaces an unsent frame,
/// so a slow client increases drops rather than latency.
/// </summary>
internal sealed class LatestFrameMailbox<T> : IDisposable
    where T : class
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _available = new(0, 1);
    private T? _item;
    private bool _disposed;
    private long _dropped;

    internal long Dropped => Interlocked.Read(ref _dropped);

    internal void Publish(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var signal = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_item is not null)
            {
                Interlocked.Increment(ref _dropped);
            }
            else
            {
                signal = true;
            }

            _item = item;
        }

        if (signal)
        {
            _available.Release();
        }
    }

    internal async Task<T> ReadAsync(CancellationToken cancellationToken)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var item = _item ?? throw new InvalidOperationException(
                "Frame mailbox was signaled without a frame.");
            _item = null;
            return item;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _item = null;
        }

        _available.Dispose();
    }
}
