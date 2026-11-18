using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using PanelHost.Protocol;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace PanelHost.Services;

public sealed class WindowsMediaService : IMediaService
{
    private static readonly TimeSpan TimelineRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly object PendingSession = new();

    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Channel<bool> _refreshRequests =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly MediaRefreshCoordinator _refreshCoordinator = new();
    private readonly object _artworkTasksLock = new();
    private readonly Dictionary<Task, CancellationTokenSource> _artworkTasks = [];
    private readonly TextWriter _log;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaMetadata? _metadata;
    private object? _metadataSession;
    private long _metadataGeneration = -1;
    private Task? _refreshWorker;
    private Task? _timelinePoller;
    private int _pendingRefreshReasons;
    private bool _acceptArtworkTasks = true;
    private bool _initialized;
    private bool _disposed;

    public WindowsMediaService()
        : this(Console.Out)
    {
    }

    internal WindowsMediaService(TextWriter log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _refreshCoordinator.StateChanged += OnCoordinatedStateChanged;
    }

    public MediaStateMessage CurrentState => _refreshCoordinator.CurrentState;

    public MediaArtworkMessage CurrentArtwork => _refreshCoordinator.CurrentArtwork;

    public event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            _manager = await GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask(cancellationToken);
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;

            _refreshWorker = ProcessRefreshRequestsAsync(_lifetimeCancellation.Token);
            _refreshCoordinator.MarkSessionPending(PendingSession);
            if (TryBindCurrentSession())
            {
                await RefreshCurrentSessionAsync(
                    refreshMediaProperties: true,
                    refreshArtwork: true,
                    cancellationToken);
            }
            else
            {
                RequestRefresh(RefreshReason.Rebind);
            }
            _initialized = true;
            _timelinePoller = PollTimelineAsync(_lifetimeCancellation.Token);
        }
        catch
        {
            UnsubscribeFromManager();
            UnsubscribeFromSession();
            _refreshCoordinator.Invalidate();
            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> ExecuteCommandAsync(
        MediaCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var session = _session;
            var sessionLease = _refreshCoordinator.Capture();
            if (session is null || !ReferenceEquals(sessionLease.Session, session))
            {
                return false;
            }

            bool succeeded;
            try
            {
                succeeded = command switch
                {
                    MediaCommand.Previous => await session.TrySkipPreviousAsync()
                        .AsTask(cancellationToken),
                    MediaCommand.PlayPause => await session.TryTogglePlayPauseAsync()
                        .AsTask(cancellationToken),
                    MediaCommand.Next => await session.TrySkipNextAsync()
                        .AsTask(cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Media command",
                    $"executing {command}",
                    exception);
                RequestRefresh(RefreshReason.Rebind);
                return false;
            }

            if (!succeeded)
            {
                return false;
            }

            var changesTrack = command is MediaCommand.Previous or MediaCommand.Next;
            try
            {
                await RefreshCurrentSessionAsync(
                    refreshMediaProperties: changesTrack,
                    refreshArtwork: changesTrack,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Metadata/playback refresh",
                    "refresh after media command",
                    exception);
                RequestRefresh(RefreshReason.Rebind);
            }

            return true;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshCoordinator.Invalidate();
        CancellationTokenSource[] artworkCancellations;
        lock (_artworkTasksLock)
        {
            _acceptArtworkTasks = false;
            artworkCancellations = [.. _artworkTasks.Values];
        }

        CancelArtworkRefreshes(artworkCancellations);
        _refreshRequests.Writer.TryComplete();
        await _lifetimeCancellation.CancelAsync();

        var backgroundTasks = new[] { _refreshWorker, _timelinePoller }
            .OfType<Task>()
            .ToArray();
        if (backgroundTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(backgroundTasks);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _operationGate.WaitAsync(CancellationToken.None);
        try
        {
            UnsubscribeFromManager();
            UnsubscribeFromSession();
        }
        finally
        {
            _operationGate.Release();
        }

        Task[] artworkTasks;
        lock (_artworkTasksLock)
        {
            artworkTasks = [.. _artworkTasks.Keys];
        }

        if (artworkTasks.Length > 0)
        {
            await Task.WhenAll(artworkTasks);
        }

        _refreshCoordinator.StateChanged -= OnCoordinatedStateChanged;
        _lifetimeCancellation.Dispose();
        _operationGate.Dispose();
    }

    private async Task ProcessRefreshRequestsAsync(CancellationToken cancellationToken)
    {
        while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_refreshRequests.Reader.TryRead(out _))
            {
            }

            var reasons = (RefreshReason)Interlocked.Exchange(
                ref _pendingRefreshReasons,
                0);
            if (reasons == 0)
            {
                continue;
            }

            try
            {
                await _operationGate.WaitAsync(cancellationToken);
                try
                {
                    if ((reasons & RefreshReason.Rebind) != 0)
                    {
                        if (!TryBindCurrentSession())
                        {
                            RequestRefresh(RefreshReason.Rebind);
                            continue;
                        }

                        reasons |= RefreshReason.MediaProperties |
                            RefreshReason.Playback |
                            RefreshReason.Timeline;
                    }

                    await RefreshCurrentSessionAsync(
                        refreshMediaProperties: (reasons & RefreshReason.MediaProperties) != 0,
                        refreshArtwork: (reasons & RefreshReason.MediaProperties) != 0,
                        cancellationToken);
                }
                finally
                {
                    _operationGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Metadata/playback refresh",
                    $"processing {reasons}",
                    exception);
            }
        }
    }

    private async Task PollTimelineAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimelineRefreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var lease = _refreshCoordinator.Capture();
            RequestRefresh(ReferenceEquals(lease.Session, PendingSession)
                ? RefreshReason.Rebind
                : RefreshReason.Playback | RefreshReason.Timeline);
        }
    }

    private async Task RefreshCurrentSessionAsync(
        bool refreshMediaProperties,
        bool refreshArtwork,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        var session = _session;
        var lease = _refreshCoordinator.Capture();
        if (session is null)
        {
            _refreshCoordinator.TryPublishUnavailable(lease);
            return;
        }

        if (!ReferenceEquals(lease.Session, session))
        {
            return;
        }

        var metadata = _metadata;
        var cacheIsCurrent = metadata is not null &&
            ReferenceEquals(_metadataSession, session) &&
            _metadataGeneration == lease.Generation;
        var mustRefreshMetadata = refreshMediaProperties || !cacheIsCurrent;
        IRandomAccessStreamReference? thumbnail = null;
        var metadataWasRefreshed = false;

        if (mustRefreshMetadata)
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties properties;
            try
            {
                properties = await session.TryGetMediaPropertiesAsync()
                    .AsTask(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Metadata/playback refresh",
                    "requesting media properties",
                    exception);
                return;
            }

            if (!IsRefreshCurrent(lease, session))
            {
                return;
            }

            try
            {
                metadata = CreateMetadata(session.SourceAppUserModelId, properties);
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Metadata/playback refresh",
                    "building media metadata",
                    exception);
                return;
            }

            if (refreshArtwork || !cacheIsCurrent)
            {
                try
                {
                    thumbnail = properties.Thumbnail;
                }
                catch (Exception exception)
                {
                    MediaDiagnostics.WriteFailure(
                        _log,
                        "Artwork refresh",
                        "reading thumbnail reference",
                        exception);
                }
            }

            if (!IsRefreshCurrent(lease, session))
            {
                return;
            }

            metadataWasRefreshed = true;
        }

        if (metadata is null)
        {
            return;
        }

        MediaStateMessage state;
        var playbackStage = "reading playback info";
        try
        {
            var playback = session.GetPlaybackInfo();
            playbackStage = "reading playback capabilities";
            var controls = playback.Controls;
            playbackStage = "reading timeline properties";
            var timeline = session.GetTimelineProperties();
            var duration = timeline.EndTime - timeline.StartTime;
            var isPlaying = playback.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var position = CalculatePosition(timeline, isPlaying);

            state = new MediaStateMessage
            {
                Available = true,
                MediaId = metadata.MediaId,
                SourceApp = EmptyToNull(metadata.SourceApp),
                Title = EmptyToNull(metadata.Title),
                Artist = EmptyToNull(metadata.Artist),
                Album = EmptyToNull(metadata.Album),
                IsPlaying = isPlaying,
                PositionMs = ToMilliseconds(position),
                DurationMs = ToMilliseconds(duration),
                CanPlayPause = controls.IsPlayPauseToggleEnabled,
                CanPrevious = controls.IsPreviousEnabled,
                CanNext = controls.IsNextEnabled,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MediaDiagnostics.WriteFailure(
                _log,
                "Metadata/playback refresh",
                playbackStage,
                exception);
            return;
        }

        bool statePublished;
        try
        {
            statePublished = _refreshCoordinator.TryPublishState(
                lease,
                state,
                resetArtwork: metadataWasRefreshed);
        }
        catch (Exception exception)
        {
            MediaDiagnostics.WriteFailure(
                _log,
                "Metadata/playback refresh",
                "publishing media state",
                exception);
            return;
        }

        if (!statePublished)
        {
            return;
        }

        if (metadataWasRefreshed)
        {
            _metadata = metadata;
            _metadataSession = session;
            _metadataGeneration = lease.Generation;
        }

        if (metadataWasRefreshed && thumbnail is not null)
        {
            StartArtworkRefresh(lease, session, metadata.MediaId, thumbnail);
        }
    }

    private void StartArtworkRefresh(
        MediaRefreshLease lease,
        GlobalSystemMediaTransportControlsSession session,
        string mediaId,
        IRandomAccessStreamReference thumbnail)
    {
        Task task;
        CancellationTokenSource cancellation;
        lock (_artworkTasksLock)
        {
            if (_disposed ||
                !_acceptArtworkTasks ||
                !IsRefreshCurrent(lease, session))
            {
                return;
            }

            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token);
            task = MediaArtworkRefresh.RunAsync(
                _refreshCoordinator,
                lease,
                mediaId,
                cancellationToken => ReadThumbnailAsync(thumbnail, cancellationToken),
                _log,
                cancellation.Token);
            _artworkTasks.Add(task, cancellation);
        }

        ObserveArtworkTask(task);
    }

    private async Task<MediaArtworkPayload?> ReadThumbnailAsync(
        IRandomAccessStreamReference thumbnail,
        CancellationToken cancellationToken)
    {
        var stage = "opening thumbnail stream";
        try
        {
            var stream = await thumbnail.OpenReadAsync().AsTask(cancellationToken);
            if (stream is null)
            {
                MediaDiagnostics.WriteNotice(
                    _log,
                    "Artwork refresh unavailable",
                    stage,
                    "Thumbnail stream is null.");
                return null;
            }

            using (stream)
            {
                stage = "checking thumbnail stream size";
                var size = stream.Size;
                if (size == 0)
                {
                    MediaDiagnostics.WriteNotice(
                        _log,
                        "Artwork refresh unavailable",
                        stage,
                        "Thumbnail stream is empty.");
                    return null;
                }

                if (size > MediaArtworkRefresh.MaximumArtworkBytes)
                {
                    MediaDiagnostics.WriteNotice(
                        _log,
                        "Artwork refresh unavailable",
                        stage,
                        $"Thumbnail exceeds the {MediaArtworkRefresh.MaximumArtworkBytes}-byte limit.");
                    return null;
                }

                stage = "reading thumbnail MIME type";
                var contentType = stream.ContentType;
                stage = "reading thumbnail stream";
                using var input = stream.GetInputStreamAt(0);
                using var reader = new DataReader(input);
                var expectedBytes = checked((uint)size);
                var loadedBytes = await reader.LoadAsync(expectedBytes)
                    .AsTask(cancellationToken);
                if (loadedBytes != expectedBytes)
                {
                    MediaDiagnostics.WriteNotice(
                        _log,
                        "Artwork refresh unavailable",
                        stage,
                        $"Thumbnail stream ended after {loadedBytes} of {expectedBytes} bytes.");
                    return null;
                }

                var bytes = new byte[expectedBytes];
                reader.ReadBytes(bytes);
                return new MediaArtworkPayload(contentType, bytes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MediaDiagnostics.WriteFailure(
                _log,
                "Artwork refresh",
                stage,
                exception);
            return null;
        }
    }

    private void ObserveArtworkTask(Task task)
    {
        _ = task.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception;
                CancellationTokenSource? cancellation = null;
                lock (_artworkTasksLock)
                {
                    if (_artworkTasks.Remove(completedTask, out var removedCancellation))
                    {
                        cancellation = removedCancellation;
                    }
                }

                cancellation?.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static MediaMetadata CreateMetadata(
        string sourceApp,
        GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        var mediaIdSource = string.Join(
            '\u001f',
            sourceApp,
            properties.Title,
            properties.Artist,
            properties.AlbumTitle);
        var mediaId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(mediaIdSource)))
            .ToLowerInvariant();

        return new MediaMetadata(
            sourceApp,
            properties.Title,
            properties.Artist,
            properties.AlbumTitle,
            mediaId);
    }

    private bool IsRefreshCurrent(
        MediaRefreshLease lease,
        GlobalSystemMediaTransportControlsSession session) =>
        !_disposed &&
        ReferenceEquals(_session, session) &&
        _refreshCoordinator.IsCurrent(lease);

    private bool TryBindCurrentSession()
    {
        var expectedLease = _refreshCoordinator.Capture();
        if (!ReferenceEquals(expectedLease.Session, PendingSession))
        {
            expectedLease = _refreshCoordinator.MarkSessionPending(PendingSession);
        }

        var session = _manager?.GetCurrentSession();
        if (!_refreshCoordinator.TryBindSession(
                expectedLease,
                session,
                out _))
        {
            return false;
        }

        try
        {
            ApplySession(session);
        }
        catch
        {
            _refreshCoordinator.MarkSessionPending(PendingSession);
            throw;
        }

        return true;
    }

    private void ApplySession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session))
        {
            return;
        }

        UnsubscribeFromSession();
        _session = session;
        ClearMetadataCache();

        if (_session is null)
        {
            return;
        }

        try
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
        catch
        {
            var failedSession = _session;
            _session = null;
            ClearMetadataCache();
            RemoveSessionHandlers(failedSession);
            throw;
        }
    }

    private void UnsubscribeFromSession()
    {
        if (_session is not null)
        {
            var session = _session;
            _session = null;
            RemoveSessionHandlers(session);
        }

        ClearMetadataCache();
    }

    private void RemoveSessionHandlers(
        GlobalSystemMediaTransportControlsSession session)
    {
        TryRemoveEventHandler(
            () => session.MediaPropertiesChanged -= OnMediaPropertiesChanged,
            "removing media properties handler");
        TryRemoveEventHandler(
            () => session.PlaybackInfoChanged -= OnPlaybackInfoChanged,
            "removing playback handler");
        TryRemoveEventHandler(
            () => session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged,
            "removing timeline handler");
    }

    private void TryRemoveEventHandler(Action removeHandler, string stage)
    {
        try
        {
            removeHandler();
        }
        catch (Exception exception)
        {
            MediaDiagnostics.WriteFailure(
                _log,
                "Media session cleanup",
                stage,
                exception);
        }
    }

    private void ClearMetadataCache()
    {
        _metadata = null;
        _metadataSession = null;
        _metadataGeneration = -1;
    }

    private void UnsubscribeFromManager()
    {
        if (_manager is null)
        {
            return;
        }

        var manager = _manager;
        _manager = null;
        TryRemoveEventHandler(
            () => manager.CurrentSessionChanged -= OnCurrentSessionChanged,
            "removing current-session manager handler");
        TryRemoveEventHandler(
            () => manager.SessionsChanged -= OnSessionsChanged,
            "removing sessions manager handler");
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _manager))
        {
            RequestRefresh(RefreshReason.Rebind);
        }
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _manager))
        {
            RequestRefresh(RefreshReason.Rebind);
        }
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _session))
        {
            RequestRefresh(RefreshReason.MediaProperties);
        }
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _session))
        {
            RequestRefresh(RefreshReason.Playback);
        }
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (ReferenceEquals(sender, _session))
        {
            RequestRefresh(RefreshReason.Timeline);
        }
    }

    private void RequestRefresh(RefreshReason reason)
    {
        if (_disposed)
        {
            return;
        }

        if ((reason & RefreshReason.Rebind) != 0)
        {
            _refreshCoordinator.MarkSessionPending(PendingSession);
            CancelArtworkRefreshes();
        }
        else if ((reason & RefreshReason.MediaProperties) != 0)
        {
            _refreshCoordinator.Invalidate();
            CancelArtworkRefreshes();
        }

        Interlocked.Or(ref _pendingRefreshReasons, (int)reason);
        _refreshRequests.Writer.TryWrite(true);
    }

    private void CancelArtworkRefreshes()
    {
        CancellationTokenSource[] cancellations;
        lock (_artworkTasksLock)
        {
            cancellations = [.. _artworkTasks.Values];
        }

        CancelArtworkRefreshes(cancellations);
    }

    private void CancelArtworkRefreshes(
        IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                MediaDiagnostics.WriteFailure(
                    _log,
                    "Artwork refresh cancellation",
                    "canceling obsolete thumbnail read",
                    exception);
            }
        }
    }

    private void OnCoordinatedStateChanged(
        object? sender,
        MediaStateChangedEventArgs eventArgs) =>
        StateChanged?.Invoke(this, eventArgs);

    private static long ToMilliseconds(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return 0;
        }

        return value.TotalMilliseconds >= long.MaxValue
            ? long.MaxValue
            : (long)value.TotalMilliseconds;
    }

    private static TimeSpan CalculatePosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
        bool isPlaying)
    {
        if (timeline.EndTime <= timeline.StartTime)
        {
            return TimeSpan.Zero;
        }

        var position = timeline.Position;
        if (position < timeline.StartTime)
        {
            position = timeline.StartTime;
        }
        else if (position > timeline.EndTime)
        {
            position = timeline.EndTime;
        }

        if (isPlaying && position < timeline.EndTime)
        {
            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero)
            {
                var remaining = timeline.EndTime - position;
                position = elapsed >= remaining
                    ? timeline.EndTime
                    : position + elapsed;
            }
        }

        return position - timeline.StartTime;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    [Flags]
    private enum RefreshReason
    {
        Playback = 1,
        Timeline = 2,
        MediaProperties = 4,
        Rebind = 8,
    }

    private sealed record MediaMetadata(
        string SourceApp,
        string Title,
        string Artist,
        string Album,
        string MediaId);
}
