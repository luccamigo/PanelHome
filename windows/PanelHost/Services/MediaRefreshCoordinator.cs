using PanelHost.Protocol;

namespace PanelHost.Services;

internal readonly record struct MediaRefreshLease(long Generation, object? Session);

internal sealed class MediaRefreshCoordinator
{
    private readonly object _stateLock = new();
    private readonly object _notificationLock = new();

    private MediaStateMessage _currentState = CreateUnavailableState();
    private MediaArtworkMessage _currentArtwork = CreateUnavailableArtwork();
    private object? _session;
    private long _generation;
    private long _stateVersion;
    private long _lastNotifiedStateVersion;

    public MediaStateMessage CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    public MediaArtworkMessage CurrentArtwork
    {
        get
        {
            lock (_stateLock)
            {
                return _currentArtwork;
            }
        }
    }

    public event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    public MediaRefreshLease BindSession(object? session)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_session, session))
            {
                _session = session;
                _generation++;
            }

            return new MediaRefreshLease(_generation, _session);
        }
    }

    public MediaRefreshLease Invalidate()
    {
        lock (_stateLock)
        {
            _generation++;
            return new MediaRefreshLease(_generation, _session);
        }
    }

    public MediaRefreshLease MarkSessionPending(object pendingSession)
    {
        ArgumentNullException.ThrowIfNull(pendingSession);
        lock (_stateLock)
        {
            _session = pendingSession;
            _generation++;
            return new MediaRefreshLease(_generation, _session);
        }
    }

    public bool TryBindSession(
        MediaRefreshLease expectedLease,
        object? session,
        out MediaRefreshLease boundLease)
    {
        lock (_stateLock)
        {
            if (!IsCurrentLocked(expectedLease))
            {
                boundLease = new MediaRefreshLease(_generation, _session);
                return false;
            }

            _session = session;
            _generation++;
            boundLease = new MediaRefreshLease(_generation, _session);
            return true;
        }
    }

    public MediaRefreshLease Capture()
    {
        lock (_stateLock)
        {
            return new MediaRefreshLease(_generation, _session);
        }
    }

    public bool IsCurrent(MediaRefreshLease lease)
    {
        lock (_stateLock)
        {
            return IsCurrentLocked(lease);
        }
    }

    public bool TryPublishState(
        MediaRefreshLease lease,
        MediaStateMessage state,
        bool resetArtwork)
    {
        CoordinatedChange? change = null;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(lease))
            {
                return false;
            }

            var stateChanged = state != _currentState;
            var stateMediaId = state.Available ? state.MediaId ?? string.Empty : string.Empty;
            var artwork = resetArtwork ||
                !string.Equals(
                    _currentArtwork.MediaId,
                    stateMediaId,
                    StringComparison.Ordinal)
                ? CreateUnavailableArtwork(stateMediaId)
                : _currentArtwork;
            var artworkChanged = artwork != _currentArtwork;

            _currentState = state;
            _currentArtwork = artwork;
            if (stateChanged || artworkChanged)
            {
                change = new CoordinatedChange(
                    ++_stateVersion,
                    new MediaStateChangedEventArgs(state, artwork, artworkChanged));
            }
        }

        if (change is not null)
        {
            NotifyStateChanged(change);
        }

        return true;
    }

    public bool TryPublishArtwork(
        MediaRefreshLease lease,
        MediaArtworkMessage artwork)
    {
        CoordinatedChange? change = null;
        lock (_stateLock)
        {
            if (!IsCurrentLocked(lease) ||
                !_currentState.Available ||
                string.IsNullOrEmpty(_currentState.MediaId) ||
                !string.Equals(
                    artwork.MediaId,
                    _currentState.MediaId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (artwork != _currentArtwork)
            {
                _currentArtwork = artwork;
                change = new CoordinatedChange(
                    ++_stateVersion,
                    new MediaStateChangedEventArgs(
                        _currentState,
                        artwork,
                        artworkChanged: true));
            }
        }

        if (change is not null)
        {
            NotifyStateChanged(change);
        }

        return true;
    }

    public bool TryPublishUnavailable(MediaRefreshLease lease) =>
        TryPublishState(lease, CreateUnavailableState(), resetArtwork: true);

    internal static MediaStateMessage CreateUnavailableState() => new()
    {
        Available = false,
        Title = null,
        Artist = null,
    };

    internal static MediaArtworkMessage CreateUnavailableArtwork(
        string mediaId = "") => new()
        {
            MediaId = mediaId,
            Available = false,
        };

    private bool IsCurrentLocked(MediaRefreshLease lease) =>
        lease.Generation == _generation &&
        ReferenceEquals(lease.Session, _session);

    private void NotifyStateChanged(CoordinatedChange change)
    {
        lock (_notificationLock)
        {
            if (change.Version <= _lastNotifiedStateVersion)
            {
                return;
            }

            _lastNotifiedStateVersion = change.Version;
            StateChanged?.Invoke(this, change.EventArgs);
        }
    }

    private sealed record CoordinatedChange(
        long Version,
        MediaStateChangedEventArgs EventArgs);
}
