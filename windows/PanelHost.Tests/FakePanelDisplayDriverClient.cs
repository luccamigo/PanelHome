using PanelOS.DriverInterop;
using PanelOS.DisplayBroker.Protocol;

namespace PanelHost.Tests;

internal enum FakePanelDisplayDriverOperation
{
    GetStatus,
    SetCaptureTarget,
    Connect,
    Disconnect,
}

/// <summary>
/// Stateful, scriptable driver client for Desktop lifecycle tests. It never
/// discovers or opens a real Windows device interface.
/// </summary>
internal sealed class FakePanelDisplayDriverClient :
    IPanelDisplayDriverClient,
    IPanelDisplayDriverCaptureConfigurationClient,
    IPanelDisplayBrokerConnection
{
    private readonly object _sync = new();
    private readonly List<FakePanelDisplayDriverOperation> _calls = [];

    private PanelDisplayDriverState _driverState = PanelDisplayDriverState.Ready;
    private PanelDisplayMonitorState _monitorState =
        PanelDisplayMonitorState.Disconnected;
    private int _activeCalls;
    private int _maxConcurrentCalls;
    private uint _configuredTargetFps = 30;

    public string? InterfacePath { get; set; } = @"\\?\panel-display-test";

    public bool IsConnected { get; private set; } = true;

    public event EventHandler<PanelDisplayBrokerDisconnectedEventArgs>? Disconnected;

    public Task<PanelDisplayDriverStatus> GetStatusIfConnectedAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected) throw new PanelDisplayBrokerDisconnectedException(
            PanelDisplayBrokerDisconnectReason.PipeClosed, "Synthetic disconnected broker");
        return GetStatusAsync(cancellationToken);
    }

    public void SimulateBrokerDisconnect(
        PanelDisplayBrokerDisconnectReason reason =
            PanelDisplayBrokerDisconnectReason.BrokerExited)
    {
        IsConnected = false;
        Disconnected?.Invoke(
            this,
            new PanelDisplayBrokerDisconnectedEventArgs(reason, "test disconnect"));
    }

    /// <summary>A new broker has been started (for example after a second UAC consent).</summary>
    public void SimulateBrokerRestored() => IsConnected = true;

    public Func<CancellationToken, Task<PanelDisplayDriverStatus>>? GetStatusHandler
    {
        get;
        set;
    }

    public Func<CancellationToken, Task<PanelDisplayCommandResponse>>? ConnectHandler
    {
        get;
        set;
    }

    public Func<CancellationToken, Task<PanelDisplayCommandResponse>>? DisconnectHandler
    {
        get;
        set;
    }

    public Func<uint, CancellationToken, Task<uint>>? SetCaptureTargetHandler
    {
        get;
        set;
    }

    public IReadOnlyList<FakePanelDisplayDriverOperation> Calls
    {
        get
        {
            lock (_sync)
            {
                return [.. _calls];
            }
        }
    }

    public int GetStatusCount => Count(FakePanelDisplayDriverOperation.GetStatus);

    public int ConnectCount => Count(FakePanelDisplayDriverOperation.Connect);

    public int SetCaptureTargetCount =>
        Count(FakePanelDisplayDriverOperation.SetCaptureTarget);

    public int DisconnectCount => Count(FakePanelDisplayDriverOperation.Disconnect);

    public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    public uint ConfiguredTargetFps
    {
        get
        {
            lock (_sync)
            {
                return _configuredTargetFps;
            }
        }
    }

    public PanelDisplayDriverState DriverState
    {
        get
        {
            lock (_sync)
            {
                return _driverState;
            }
        }
        set
        {
            lock (_sync)
            {
                _driverState = value;
            }
        }
    }

    public PanelDisplayMonitorState MonitorState
    {
        get
        {
            lock (_sync)
            {
                return _monitorState;
            }
        }
        set
        {
            lock (_sync)
            {
                _monitorState = value;
            }
        }
    }

    public Task<PanelDisplayDriverStatus> GetStatusAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            FakePanelDisplayDriverOperation.GetStatus,
            cancellationToken,
            GetStatusHandler ?? GetCurrentStatusAsync);

    public Task<PanelDisplayCommandResponse> ConnectAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            FakePanelDisplayDriverOperation.Connect,
            cancellationToken,
            ConnectHandler ?? ConnectDefaultAsync);

    public Task<PanelDisplayCommandResponse> DisconnectAsync(
        CancellationToken cancellationToken) =>
        InvokeAsync(
            FakePanelDisplayDriverOperation.Disconnect,
            cancellationToken,
            DisconnectHandler ?? DisconnectDefaultAsync);

    public Task<uint> SetCaptureTargetFpsAsync(
        uint targetFramesPerSecond,
        CancellationToken cancellationToken) =>
        InvokeAsync(
            FakePanelDisplayDriverOperation.SetCaptureTarget,
            cancellationToken,
            token => SetCaptureTargetHandler is { } handler
                ? handler(targetFramesPerSecond, token)
                : SetCaptureTargetDefaultAsync(targetFramesPerSecond, token));

    public PanelDisplayDriverStatus GetCurrentStatus(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return new PanelDisplayDriverStatus(
                PanelDisplayDriverContract.ProtocolVersion,
                _driverState,
                _monitorState,
                default,
                FramesProcessed: 0,
                ConnectCount: 0,
                Modes: Array.Empty<PanelDisplayMode>());
        }
    }

    public static PanelDisplayDriverException CreateFailure(
        PanelDisplayDriverFailureKind kind,
        string operation,
        int? win32ErrorCode = null) =>
        new(
            kind,
            operation,
            $"Scripted {operation} failure.",
            win32ErrorCode,
            @"\\?\panel-display-test");

    private Task<PanelDisplayDriverStatus> GetCurrentStatusAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(GetCurrentStatus(cancellationToken));

    private Task<PanelDisplayCommandResponse> ConnectDefaultAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MonitorState = PanelDisplayMonitorState.Connected;
        return Task.FromResult(new PanelDisplayCommandResponse(
            PanelDisplayCommandResult.Ok,
            PanelDisplayMonitorState.Connected));
    }

    private Task<PanelDisplayCommandResponse> DisconnectDefaultAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MonitorState = PanelDisplayMonitorState.Disconnected;
        return Task.FromResult(new PanelDisplayCommandResponse(
            PanelDisplayCommandResult.Ok,
            PanelDisplayMonitorState.Disconnected));
    }

    private Task<uint> SetCaptureTargetDefaultAsync(
        uint targetFramesPerSecond,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _configuredTargetFps = targetFramesPerSecond;
        }

        return Task.FromResult(targetFramesPerSecond);
    }

    private async Task<T> InvokeAsync<T>(
        FakePanelDisplayDriverOperation operation,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> handler)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _calls.Add(operation);
        }

        var activeCalls = Interlocked.Increment(ref _activeCalls);
        UpdateMaximum(activeCalls);
        try
        {
            return await handler(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    private int Count(FakePanelDisplayDriverOperation operation)
    {
        lock (_sync)
        {
            return _calls.Count(call => call == operation);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        var maximum = Volatile.Read(ref _maxConcurrentCalls);
        while (candidate > maximum)
        {
            var observed = Interlocked.CompareExchange(
                ref _maxConcurrentCalls,
                candidate,
                maximum);
            if (observed == maximum)
            {
                return;
            }

            maximum = observed;
        }
    }
}
