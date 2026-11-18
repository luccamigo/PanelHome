using System.Threading.Channels;
using PanelHost.Protocol;
using PanelOS.Windows.LoopMedia;
using PanelOS.Windows.StreamDeck;

namespace PanelHost.Services;

internal sealed class LoopMediaService : IAsyncDisposable
{
    private readonly JsonLoopMediaStore _store;
    private readonly Action<string>? _log;
    private readonly StreamDeckConfigurationFileWatcher _watcher;
    private readonly Channel<byte> _changes = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _shutdown = new();
    private LoopMediaConfiguration _current = new();
    private Task? _worker;

    public LoopMediaService(JsonLoopMediaStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log;
        _watcher = new StreamDeckConfigurationFileWatcher(store.Location);
    }

    public event EventHandler? StateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _watcher.Changed += OnChanged;
        try { _watcher.Start(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("[LoopMedia] watcher unavailable");
        }
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        _worker = ProcessChangesAsync(_shutdown.Token);
    }

    private void OnChanged(object? sender, EventArgs args) => _changes.Writer.TryWrite(0);

    private async Task ProcessChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                do
                {
                    while (_changes.Reader.TryRead(out _)) { }
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                } while (_changes.Reader.TryPeek(out _));
                await ReloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var configuration = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _current, configuration);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public LoopMediaStateMessage GetState()
    {
        var configuration = Volatile.Read(ref _current);
        if (configuration.MediaId is null || !IsReadable(configuration)) return new();
        return new()
        {
            Available = true,
            MediaId = configuration.MediaId,
            Kind = configuration.Kind,
            Mime = configuration.Mime,
            SizeBytes = configuration.SizeBytes,
        };
    }

    private bool IsReadable(LoopMediaConfiguration configuration)
    {
        try
        {
            var file = new FileInfo(_store.GetMediaPath(configuration));
            if (file.Exists && file.Length == configuration.SizeBytes) return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        _log?.Invoke("[LoopMedia] media unavailable or size mismatch");
        return false;
    }

    public async Task<LoopMediaChunkMessage> ReadChunkAsync(string? mediaId, long offset,
        CancellationToken cancellationToken)
    {
        var validId = LoopMediaValidator.IsValidMediaId(mediaId);
        var response = new LoopMediaChunkMessage
        {
            MediaId = validId ? mediaId! : string.Empty,
            Offset = offset,
        };
        if (!validId) return response with { Error = "stale_media" };
        var configuration = Volatile.Read(ref _current);
        if (configuration.MediaId is null) return response with { Error = "unavailable" };
        if (mediaId != configuration.MediaId) return response with { Error = "stale_media" };
        if (offset < 0 || offset >= configuration.SizeBytes)
            return response with { Error = "invalid_offset" };
        try
        {
            await using var file = new FileStream(_store.GetMediaPath(configuration),
                FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                81920, useAsync: true);
            if (file.Length != configuration.SizeBytes) throw new IOException();
            file.Position = offset;
            var bytes = new byte[(int)Math.Min(LoopMediaValidator.ChunkBytes, file.Length - offset)];
            await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return response with
            {
                TotalBytes = file.Length,
                Data = Convert.ToBase64String(bytes),
                Last = offset + bytes.Length == file.Length,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("[LoopMedia] chunk unavailable");
            return response with { Error = "unavailable" };
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Dispose();
        _shutdown.Cancel();
        _changes.Writer.TryComplete();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
