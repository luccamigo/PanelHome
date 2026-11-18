using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanelHost.Services;

/// <summary>
/// Development-only Desktop lifecycle. It deliberately does not create a driver,
/// process, display, or window.
/// </summary>
public sealed class FakeDesktopService : IDesktopService
{
    private static readonly TimeSpan DefaultTransitionDelay =
        TimeSpan.FromMilliseconds(75);

    private readonly SemaphoreSlim _transitionLock = new(1, 1);
    private readonly Func<CancellationToken, Task> _transitionDelay;
    private readonly ILogger<FakeDesktopService> _logger;

    private DesktopServiceSnapshot _snapshot = new(DesktopServiceState.Stopped);
    private DesktopRequest? _lastStartRequest;
    private string? _nextStartErrorCode;
    private string? _nextStopErrorCode;
    private int _startRequestCount;
    private int _stopRequestCount;
    private int _disposeStarted;

    public FakeDesktopService(
        TimeSpan? transitionDelay = null,
        ILogger<FakeDesktopService>? logger = null)
    {
        var delay = transitionDelay ?? DefaultTransitionDelay;
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(transitionDelay));
        }

        _transitionDelay = cancellationToken =>
            delay == TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(delay, cancellationToken);
        _logger = logger ?? NullLogger<FakeDesktopService>.Instance;
    }

    internal FakeDesktopService(
        Func<CancellationToken, Task> transitionDelay,
        ILogger<FakeDesktopService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transitionDelay);
        _transitionDelay = transitionDelay;
        _logger = logger ?? NullLogger<FakeDesktopService>.Instance;
    }

    public DesktopServiceState State => Snapshot.State;

    public DesktopServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public string? ErrorCode => Snapshot.ErrorCode;

    public DesktopRequest? LastStartRequest => Volatile.Read(ref _lastStartRequest);

    public int StartRequestCount => Volatile.Read(ref _startRequestCount);

    public int StopRequestCount => Volatile.Read(ref _stopRequestCount);

    public event EventHandler<DesktopStateChangedEventArgs>? StateChanged;

    public Task<DesktopStartResult> StartAsync(
        DesktopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        return StartCoreAsync(request, cancellationToken);
    }

    public Task<DesktopStopResult> StopAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        return StopCoreAsync(cancellationToken);
    }

    public void FailNextStart(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Interlocked.Exchange(ref _nextStartErrorCode, errorCode);
    }

    public void FailNextStop(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        Interlocked.Exchange(ref _nextStopErrorCode, errorCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        try
        {
            await StopCoreAsync(CancellationToken.None);
        }
        finally
        {
            StateChanged = null;
            _transitionLock.Dispose();
        }
    }

    private async Task<DesktopStartResult> StartCoreAsync(
        DesktopRequest request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _startRequestCount);
        Volatile.Write(ref _lastStartRequest, request);
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            if (State == DesktopServiceState.Running)
            {
                return StartResult(succeeded: true, alreadyRunning: true);
            }

            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Starting));
            try
            {
                await DelayTransitionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
                throw;
            }

            var errorCode = Interlocked.Exchange(ref _nextStartErrorCode, null);
            if (errorCode is not null)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    errorCode));
                return StartResult(succeeded: false, alreadyRunning: false);
            }

            PublishState(new DesktopServiceSnapshot(
                DesktopServiceState.Running,
                Width: request.Width,
                Height: request.Height,
                RefreshRate: request.RefreshRate));
            return StartResult(succeeded: true, alreadyRunning: false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private async Task<DesktopStopResult> StopCoreAsync(
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _stopRequestCount);
        await _transitionLock.WaitAsync(cancellationToken);
        try
        {
            var previousState = Snapshot;
            if (previousState.State == DesktopServiceState.Stopped)
            {
                return StopResult(succeeded: true, alreadyStopped: true);
            }

            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopping));
            try
            {
                await DelayTransitionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                PublishState(previousState);
                throw;
            }

            var errorCode = Interlocked.Exchange(ref _nextStopErrorCode, null);
            if (errorCode is not null)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    errorCode));
                return StopResult(succeeded: false, alreadyStopped: false);
            }

            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
            return StopResult(succeeded: true, alreadyStopped: false);
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private Task DelayTransitionAsync(CancellationToken cancellationToken) =>
        _transitionDelay(cancellationToken);

    private DesktopStartResult StartResult(
        bool succeeded,
        bool alreadyRunning) =>
        new(succeeded, alreadyRunning, State, ErrorCode);

    private DesktopStopResult StopResult(
        bool succeeded,
        bool alreadyStopped) =>
        new(succeeded, alreadyStopped, State, ErrorCode);

    private void PublishState(DesktopServiceSnapshot state)
    {
        Volatile.Write(ref _snapshot, state);
        try
        {
            StateChanged?.Invoke(this, new DesktopStateChangedEventArgs(state));
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Desktop state listener failed with driverState {DriverState}",
                state.State);
        }
    }
}
