using System.Reflection;
using PanelHost.Services;
using PanelHost.Video;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;

namespace PanelHost.Management;

internal sealed class PanelHostSnapshotChangedEventArgs(PanelHostSnapshot snapshot) : EventArgs
{
    internal PanelHostSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
/// Lock-protected last-known runtime state. Pipe reads never call the driver, encoder or config
/// store; all slow work publishes into this object at its existing lifecycle boundary.
/// </summary>
internal sealed class PanelHostRuntimeState : IPanelVideoRuntimeObserver, IDisposable
{
    private static readonly TimeSpan DefaultVideoPublishInterval = TimeSpan.FromSeconds(1);

    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _videoPublishInterval;
    private readonly DateTimeOffset _startedAtUtc;
    private PanelHostSnapshot _snapshot;
    private DateTimeOffset _lastVideoPublishedAtUtc = DateTimeOffset.MinValue;
    private long _controlGeneration;
    private long _videoGeneration;
    private bool _disposed;

    internal PanelHostRuntimeState(
        TimeProvider? timeProvider = null,
        TimeSpan? videoPublishInterval = null,
        int? processId = null,
        string? version = null,
        DateTimeOffset? startedAtUtc = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _videoPublishInterval = videoPublishInterval ?? DefaultVideoPublishInterval;
        _startedAtUtc = startedAtUtc ?? _timeProvider.GetUtcNow();
        var hostVersion = version ??
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        _snapshot = new PanelHostSnapshot
        {
            Sequence = 1,
            ManagementProtocolVersion = PanelHostManagementProtocol.Version,
            CapturedAtUtc = _startedAtUtc,
            Host = new HostManagementSnapshot(
                processId ?? Environment.ProcessId,
                hostVersion,
                _startedAtUtc,
                0),
            Tablet = new TabletManagementSnapshot(false, false, null, [], 0, 0),
            Display = new DisplayManagementSnapshot(
                false,
                DesktopServiceState.Stopped.ToString(),
                null,
                null,
                null),
            Video = new VideoManagementSnapshot(),
            StreamDeck = new StreamDeckManagementSnapshot(null, null, null, 0, 0),
            Services = new HostServicesManagementSnapshot(
                new HostServiceManagementSnapshot("starting"),
                new HostServiceManagementSnapshot("starting")),
        };
    }

    internal event EventHandler<PanelHostSnapshotChangedEventArgs>? SnapshotChanged;

    internal PanelHostSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                var now = _timeProvider.GetUtcNow();
                return _snapshot with
                {
                    CapturedAtUtc = now,
                    Host = _snapshot.Host with
                    {
                        UptimeSeconds = Math.Max(0, (now - _startedAtUtc).TotalSeconds),
                    },
                };
            }
        }
    }

    internal long ControlConnected(int protocolVersion, IReadOnlyList<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var generation = Interlocked.Increment(ref _controlGeneration);
        Update(snapshot => snapshot with
        {
            Tablet = snapshot.Tablet with
            {
                ControlConnected = true,
                ProtocolVersion = protocolVersion,
                NegotiatedFeatures = [.. features],
                ControlSessionGeneration = generation,
            },
        });
        return generation;
    }

    internal void ControlDisconnected(long generation) =>
        UpdateIf(
            snapshot => snapshot.Tablet.ControlSessionGeneration == generation,
            snapshot => snapshot with
            {
                Tablet = snapshot.Tablet with
                {
                    ControlConnected = false,
                    ProtocolVersion = null,
                    NegotiatedFeatures = [],
                },
            });

    internal void DisplayChanged(DesktopServiceSnapshot desktop) =>
        Update(snapshot => snapshot with
        {
            Display = new DisplayManagementSnapshot(
                desktop.State == DesktopServiceState.Running,
                desktop.State.ToString(),
                desktop.Width,
                desktop.Height,
                desktop.RefreshRate),
        });

    internal void StreamDeckChanged(StreamDeckSnapshot streamDeck)
    {
        ArgumentNullException.ThrowIfNull(streamDeck);
        Update(snapshot => snapshot with
        {
            StreamDeck = new StreamDeckManagementSnapshot(
                streamDeck.ProfileId,
                streamDeck.ProfileName,
                streamDeck.Revision,
                streamDeck.PageCount,
                streamDeck.Layout?.Pages.Sum(page => page.Buttons.Count) ?? 0),
        });
    }

    internal void ServiceChanged(string service, string state, string? errorCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        Update(snapshot => snapshot with
        {
            Services = service switch
            {
                "telemetry" => (snapshot.Services ?? EmptyServices()) with
                {
                    Telemetry = new HostServiceManagementSnapshot(state, errorCode),
                },
                "media" => (snapshot.Services ?? EmptyServices()) with
                {
                    Media = new HostServiceManagementSnapshot(state, errorCode),
                },
                _ => snapshot.Services,
            },
        });
    }

    public long SessionConnected(VideoAck ack, int configuredFps)
    {
        var generation = Interlocked.Increment(ref _videoGeneration);
        Update(snapshot => snapshot with
        {
            Tablet = snapshot.Tablet with
            {
                VideoConnected = true,
                VideoSessionGeneration = generation,
            },
            Video = new VideoManagementSnapshot
            {
                Connected = true,
                SessionGeneration = generation,
                Codec = ack.SelectedCodec.ToString(),
                Width = checked((int)ack.MaxWidth),
                Height = checked((int)ack.MaxHeight),
                ConfiguredFps = configuredFps,
                NegotiatedFps = checked((int)ack.Fps),
            },
        });
        return generation;
    }

    public void MetricsUpdated(long sessionGeneration, PanelVideoRuntimeMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        PanelHostSnapshot? published = null;
        lock (_sync)
        {
            if (_disposed ||
                !_snapshot.Video.Connected ||
                _snapshot.Video.SessionGeneration != sessionGeneration)
            {
                return;
            }

            _snapshot = Next(_snapshot with
            {
                Video = _snapshot.Video with
                {
                    Codec = metrics.Codec,
                    Encoder = metrics.Encoder,
                    Width = metrics.Width,
                    Height = metrics.Height,
                    ConfiguredFps = metrics.ConfiguredFps,
                    NegotiatedFps = metrics.NegotiatedFps,
                    DriverCaptureFps = metrics.DriverCaptureFps,
                    HostReceiveFps = metrics.HostReceiveFps,
                    SendFps = metrics.SendFps,
                    BandwidthMegabytesPerSecond = metrics.BandwidthMegabytesPerSecond,
                    EncodeLatencyMilliseconds = metrics.EncodeLatencyMilliseconds,
                    CaptureToSendMilliseconds = metrics.CaptureToSendMilliseconds,
                    SequenceGap = metrics.SequenceGap,
                },
            });
            var now = _timeProvider.GetUtcNow();
            if (now - _lastVideoPublishedAtUtc >= _videoPublishInterval)
            {
                _lastVideoPublishedAtUtc = now;
                published = SnapshotLocked(now);
            }
        }

        Publish(published);
    }

    public void SessionDisconnected(long sessionGeneration) =>
        UpdateIf(
            snapshot => snapshot.Video.SessionGeneration == sessionGeneration,
            snapshot => snapshot with
            {
                Tablet = snapshot.Tablet with { VideoConnected = false },
                Video = new VideoManagementSnapshot
                {
                    SessionGeneration = sessionGeneration,
                },
            });

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            SnapshotChanged = null;
        }
    }

    private void Update(Func<PanelHostSnapshot, PanelHostSnapshot> transform) =>
        UpdateIf(_ => true, transform);

    private void UpdateIf(
        Func<PanelHostSnapshot, bool> predicate,
        Func<PanelHostSnapshot, PanelHostSnapshot> transform)
    {
        PanelHostSnapshot? published = null;
        lock (_sync)
        {
            if (_disposed || !predicate(_snapshot))
            {
                return;
            }

            _snapshot = Next(transform(_snapshot));
            published = SnapshotLocked(_timeProvider.GetUtcNow());
        }

        Publish(published);
    }

    private PanelHostSnapshot Next(PanelHostSnapshot snapshot) => snapshot with
    {
        Sequence = _snapshot.Sequence + 1,
        ManagementProtocolVersion = PanelHostManagementProtocol.Version,
    };

    private PanelHostSnapshot SnapshotLocked(DateTimeOffset now) => _snapshot with
    {
        CapturedAtUtc = now,
        Host = _snapshot.Host with
        {
            UptimeSeconds = Math.Max(0, (now - _startedAtUtc).TotalSeconds),
        },
    };

    private void Publish(PanelHostSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            SnapshotChanged?.Invoke(this, new PanelHostSnapshotChangedEventArgs(snapshot));
        }
    }

    private static HostServicesManagementSnapshot EmptyServices() => new(
        new HostServiceManagementSnapshot("unknown"),
        new HostServiceManagementSnapshot("unknown"));
}
