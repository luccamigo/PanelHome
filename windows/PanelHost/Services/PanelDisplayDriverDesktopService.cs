using PanelOS.DriverInterop;
using PanelOS.DisplayBroker.Protocol;
using PanelHost.Protocol;

namespace PanelHost.Services;

/// <summary>
/// Owns the PanelHost Desktop lifecycle while treating PanelDisplayDriver as
/// the authority for whether the virtual monitor exists.
/// </summary>
internal sealed class PanelDisplayDriverDesktopService : IDesktopService
{
    private static readonly TimeSpan CancellationCompensationTimeout =
        TimeSpan.FromSeconds(5);

    private readonly IPanelDisplayDriverClient _driverClient;
    private readonly TextWriter _log;
    private readonly int? _captureTargetFps;
    private readonly SemaphoreSlim _transitionLock = new(1, 1);

    private DesktopServiceSnapshot _snapshot = new(DesktopServiceState.Stopped);
    private int _disposeStarted;
    private int _brokerDisconnected;

    internal PanelDisplayDriverDesktopService(
        IPanelDisplayDriverClient driverClient,
        TextWriter? log = null,
        int? captureTargetFps = null)
    {
        ArgumentNullException.ThrowIfNull(driverClient);
        if (captureTargetFps is { } targetFps && targetFps is not (30 or 45 or 60))
        {
            throw new ArgumentOutOfRangeException(nameof(captureTargetFps));
        }

        _driverClient = driverClient;
        _log = log ?? Console.Out;
        _captureTargetFps = captureTargetFps;
        if (driverClient is IPanelDisplayBrokerConnection brokerConnection)
        {
            brokerConnection.Disconnected += OnBrokerDisconnected;
        }
    }

    public DesktopServiceState State => Snapshot.State;

    public DesktopServiceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public string? ErrorCode => Snapshot.ErrorCode;

    public event EventHandler<DesktopStateChangedEventArgs>? StateChanged;

    public Task<DesktopStartResult> StartAsync(
        DesktopRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        return StartCoreAsync(cancellationToken);
    }

    public Task<DesktopStopResult> StopAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposeStarted) != 0,
            this);
        return StopCoreAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            if (_driverClient is IPanelDisplayBrokerConnection brokerConnection)
            {
                brokerConnection.Disconnected -= OnBrokerDisconnected;
            }
            StateChanged = null;
        }

        // Program performs bounded StopAsync cleanup before the broker lease is
        // disposed. The semaphore stays managed-only so a late IPC completion
        // cannot release a disposed synchronization primitive.
        return ValueTask.CompletedTask;
    }

    private void OnBrokerDisconnected(
        object? sender,
        PanelDisplayBrokerDisconnectedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 ||
            Snapshot.State is DesktopServiceState.Stopped or DesktopServiceState.Error)
        {
            return;
        }

        _log.WriteLine(
            $"[Desktop] Display broker disconnected ({eventArgs.Reason}); " +
            "Desktop and video are being stopped safely.");
        Volatile.Write(ref _brokerDisconnected, 1);
        PublishState(new DesktopServiceSnapshot(
            DesktopServiceState.Error,
            DesktopErrorCodes.BrokerDisconnected));
    }

    private async Task<DesktopStartResult> StartCoreAsync(
        CancellationToken cancellationToken)
    {
        // Always return a Task before entering native I/O so the caller can
        // install its hard shutdown timeout even when the driver completes an
        // operation synchronously.
        await Task.Yield();
        await _transitionLock.WaitAsync(cancellationToken);
        var previousState = Snapshot;
        var driverMayBeConnected = previousState.State == DesktopServiceState.Running;
        var connectIssued = false;

        try
        {
            Volatile.Write(ref _brokerDisconnected, 0);
            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Starting));

            var status = await ReadStatusAsync(cancellationToken);
            driverMayBeConnected = status.MonitorState ==
                PanelDisplayMonitorState.Connected;

            if (status.DriverState != PanelDisplayDriverState.Ready)
            {
                return FailStart(
                    DesktopErrorCodes.DriverNotReady,
                    $"driver state is {status.DriverState}");
            }

            if (_captureTargetFps is { } captureTargetFps)
            {
                await ConfigureCaptureTargetAsync(
                    captureTargetFps,
                    cancellationToken).ConfigureAwait(false);
            }

            if (driverMayBeConnected)
            {
                PublishState(RunningSnapshot(status));
                return StartResult(succeeded: true, alreadyRunning: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _log.WriteLine("[Desktop] CONNECT_DISPLAY requested");
            connectIssued = true;
            driverMayBeConnected = true;
            var response = await _driverClient.ConnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var responseError = GetConnectResponseError(response);
            if (responseError is not null)
            {
                await TryCompensatingDisconnectAsync(
                    "failed start response",
                    publishStopped: false);
                return FailStart(responseError.Value.ErrorCode, responseError.Value.Detail);
            }

            var confirmedStatus = await ReadStatusAsync(cancellationToken);
            driverMayBeConnected = confirmedStatus.MonitorState ==
                PanelDisplayMonitorState.Connected;

            if (confirmedStatus.DriverState != PanelDisplayDriverState.Ready)
            {
                await TryCompensatingDisconnectAsync(
                    "unconfirmed start",
                    publishStopped: false);
                return FailStart(
                    DesktopErrorCodes.DriverNotReady,
                    $"driver state after CONNECT is {confirmedStatus.DriverState}");
            }

            if (!driverMayBeConnected)
            {
                await TryCompensatingDisconnectAsync(
                    "unconfirmed start",
                    publishStopped: false);
                return FailStart(
                    DesktopErrorCodes.DriverIoError,
                    "GET_STATUS did not confirm a connected monitor");
            }

            PublishState(RunningSnapshot(confirmedStatus));
            return StartResult(
                succeeded: true,
                alreadyRunning:
                    response.Result == PanelDisplayCommandResult.AlreadyConnected);
        }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref _brokerDisconnected) != 0)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    DesktopErrorCodes.BrokerDisconnected));
                throw;
            }
            if (driverMayBeConnected)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    DesktopErrorCodes.DriverIoError));
                await TryCompensatingDisconnectAsync(
                    "cancelled start",
                    publishStopped: true);
            }
            else
            {
                PublishState(previousState);
            }

            throw;
        }
        catch (PanelDisplayBrokerDisconnectedException exception)
        {
            _log.WriteLine(
                $"[Desktop] Display broker start failure: {exception.Reason}.");
            return exception.Reason == PanelDisplayBrokerDisconnectReason.ElevationDeclined
                ? FailStart(
                    DesktopErrorCodes.ElevationDeclined,
                    "Windows elevation was not approved")
                : FailStart(
                    DesktopErrorCodes.BrokerDisconnected,
                    "display broker disconnected");
        }
        catch (PanelDisplayDriverException exception)
        {
            LogDriverFailure("start", exception);
            if (connectIssued)
            {
                await TryCompensatingDisconnectAsync(
                    "failed start",
                    publishStopped: false);
            }

            return FailStart(MapDriverError(exception), exception.Operation);
        }
        catch (Exception exception)
        {
            _log.WriteLine(
                $"[Desktop] PanelDisplayDriver start failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            if (connectIssued)
            {
                await TryCompensatingDisconnectAsync(
                    "failed start",
                    publishStopped: false);
            }

            return FailStart(
                DesktopErrorCodes.DriverIoError,
                "unexpected driver client failure");
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private async Task<DesktopStopResult> StopCoreAsync(
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        await _transitionLock.WaitAsync(cancellationToken);
        var previousState = Snapshot;
        var driverMayBeConnected = previousState.State != DesktopServiceState.Stopped;
        var disconnectIssued = false;

        try
        {
            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopping));
            // Stop must not reconnect (and potentially prompt UAC) to an absent lease.
            if (BrokerUnavailable) return StopWithoutBroker();
            Volatile.Write(ref _brokerDisconnected, 0);

            var status = await ReadStatusAsync(cancellationToken, startBrokerIfNeeded: false);
            driverMayBeConnected = status.MonitorState ==
                PanelDisplayMonitorState.Connected;

            if (!driverMayBeConnected)
            {
                PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
                return StopResult(succeeded: true, alreadyStopped: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _log.WriteLine("[Desktop] DISCONNECT_DISPLAY requested");
            disconnectIssued = true;
            var response = await _driverClient.DisconnectAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var responseError = GetDisconnectResponseError(response);
            if (responseError is not null)
            {
                await TryCompensatingDisconnectAsync(
                    "failed stop response",
                    publishStopped: false);
                return FailStop(responseError.Value.ErrorCode, responseError.Value.Detail);
            }

            var confirmedStatus = await ReadStatusAsync(cancellationToken, startBrokerIfNeeded: false);
            driverMayBeConnected = confirmedStatus.MonitorState ==
                PanelDisplayMonitorState.Connected;
            if (driverMayBeConnected)
            {
                await TryCompensatingDisconnectAsync(
                    "unconfirmed stop",
                    publishStopped: false);
                return FailStop(
                    DesktopErrorCodes.DriverIoError,
                    "GET_STATUS did not confirm a disconnected monitor");
            }

            PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
            return StopResult(
                succeeded: true,
                alreadyStopped:
                    response.Result == PanelDisplayCommandResult.AlreadyDisconnected);
        }
        catch (OperationCanceledException)
        {
            if (BrokerUnavailable)
            {
                var result = StopWithoutBroker();
                if (cancellationToken.IsCancellationRequested) throw;
                return result;
            }
            if (Volatile.Read(ref _brokerDisconnected) != 0)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    DesktopErrorCodes.BrokerDisconnected));
                throw;
            }
            if (driverMayBeConnected)
            {
                PublishState(new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    DesktopErrorCodes.DriverIoError));
                await TryCompensatingDisconnectAsync(
                    "cancelled stop",
                    publishStopped: true);
            }
            else
            {
                PublishState(previousState);
            }

            throw;
        }
        catch (PanelDisplayBrokerDisconnectedException exception)
        {
            _log.WriteLine(
                $"[Desktop] Display broker stop failure: {exception.Reason}.");
            return FailStop(
                DesktopErrorCodes.BrokerDisconnected,
                "display broker disconnected");
        }
        catch (PanelDisplayDriverException exception)
        {
            if (exception.Kind == PanelDisplayDriverFailureKind.NotFound)
            {
                // An absent driver needs no compensating disconnect, even after issuing one.
                _log.WriteLine("[Desktop] Driver absent; desktop already stopped.");
                PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
                return StopResult(succeeded: true, alreadyStopped: true);
            }

            LogDriverFailure("stop", exception);
            if (disconnectIssued)
            {
                await TryCompensatingDisconnectAsync(
                    "failed stop",
                    publishStopped: false);
            }

            return FailStop(MapDriverError(exception), exception.Operation);
        }
        catch (Exception exception)
        {
            _log.WriteLine(
                $"[Desktop] PanelDisplayDriver stop failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            if (disconnectIssued)
            {
                await TryCompensatingDisconnectAsync(
                    "failed stop",
                    publishStopped: false);
            }

            return FailStop(
                DesktopErrorCodes.DriverIoError,
                "unexpected driver client failure");
        }
        finally
        {
            _transitionLock.Release();
        }
    }

    private bool BrokerUnavailable =>
        _driverClient is IPanelDisplayBrokerConnection { IsConnected: false };

    private DesktopStopResult StopWithoutBroker()
    {
        _log.WriteLine("[Desktop] Broker lease absent; local Desktop is stopped.");
        PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
        return StopResult(succeeded: true, alreadyStopped: true);
    }

    private async Task<PanelDisplayDriverStatus> ReadStatusAsync(
        CancellationToken cancellationToken,
        bool startBrokerIfNeeded = true)
    {
        _log.WriteLine("[Desktop] Looking for PanelDisplayDriver interface");
        var status = !startBrokerIfNeeded && _driverClient is IPanelDisplayBrokerConnection broker
            ? await broker.GetStatusIfConnectedAsync(cancellationToken)
            : await _driverClient.GetStatusAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(_driverClient.InterfacePath))
        {
            _log.WriteLine(
                $"[Desktop] Driver interface found: {_driverClient.InterfacePath}");
        }

        _log.WriteLine($"[Desktop] Driver lifecycle: {status.DriverState}");
        _log.WriteLine($"[Desktop] Driver status: {status.MonitorState}");
        _log.WriteLine(
            $"[VideoTarget] Driver contract=v{status.ProtocolVersion} " +
            "captureTargets=30,45,60");
        return status;
    }

    private async Task ConfigureCaptureTargetAsync(
        int targetFramesPerSecond,
        CancellationToken cancellationToken)
    {
        if (_driverClient is not IPanelDisplayDriverCaptureConfigurationClient client)
        {
            _log.WriteLine(
                $"[VideoTarget] Pre-connect target={targetFramesPerSecond} " +
                $"success=false requiredProtocol=v{PanelDisplayDriverContract.ProtocolVersion} " +
                "reason=capture_configuration_unavailable");
            throw new NotSupportedException(
                $"PanelDisplayDriver protocol v{PanelDisplayDriverContract.ProtocolVersion} " +
                "capture configuration is unavailable.");
        }

        _log.WriteLine(
            $"[VideoTarget] Pre-connect target={targetFramesPerSecond} " +
            $"requiredProtocol=v{PanelDisplayDriverContract.ProtocolVersion}");
        var configured = await client.SetCaptureTargetFpsAsync(
            checked((uint)targetFramesPerSecond),
            cancellationToken).ConfigureAwait(false);
        if (configured != targetFramesPerSecond)
        {
            _log.WriteLine(
                $"[VideoTarget] Pre-connect target={targetFramesPerSecond} " +
                $"success=false configured={configured}");
            throw new InvalidDataException(
                $"Driver configured unexpected pre-connect target {configured} FPS.");
        }

        _log.WriteLine(
            $"[VideoTarget] Pre-connect target={targetFramesPerSecond} " +
            $"success=true configured={configured} " +
            $"protocol=v{PanelDisplayDriverContract.ProtocolVersion}");
    }

    private async Task<bool> TryCompensatingDisconnectAsync(
        string reason,
        bool publishStopped)
    {
        _log.WriteLine(
            $"[Desktop] Compensating DISCONNECT_DISPLAY after {reason}.");
        using var cancellation = new CancellationTokenSource(
            CancellationCompensationTimeout);
        try
        {
            var response = await _driverClient.DisconnectAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var responseError = GetDisconnectResponseError(response);
            if (responseError is not null)
            {
                LogStableFailure(
                    "Compensating stop",
                    responseError.Value.ErrorCode,
                    responseError.Value.Detail);
                return false;
            }

            var status = await ReadStatusAsync(cancellation.Token, startBrokerIfNeeded: false);
            if (status.MonitorState != PanelDisplayMonitorState.Disconnected)
            {
                LogStableFailure(
                    "Compensating stop",
                    DesktopErrorCodes.DriverIoError,
                    "GET_STATUS did not confirm a disconnected monitor");
                return false;
            }

            if (publishStopped)
            {
                PublishState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));
            }

            _log.WriteLine("[Desktop] Compensating disconnect confirmed.");
            return true;
        }
        catch (PanelDisplayDriverException exception)
        {
            LogDriverFailure("compensating stop", exception);
            return false;
        }
        catch (OperationCanceledException)
        {
            _log.WriteLine("[Desktop] Compensating disconnect timed out.");
            return false;
        }
        catch (Exception exception)
        {
            _log.WriteLine(
                "[Desktop] Compensating disconnect failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private static (string ErrorCode, string Detail)? GetConnectResponseError(
        PanelDisplayCommandResponse response)
    {
        if (response.Result == PanelDisplayCommandResult.AdapterNotReady)
        {
            return (
                DesktopErrorCodes.DriverNotReady,
                "CONNECT_DISPLAY reported AdapterNotReady");
        }

        if (response.Result is not (
                PanelDisplayCommandResult.Ok or
                PanelDisplayCommandResult.AlreadyConnected) ||
            response.MonitorState != PanelDisplayMonitorState.Connected)
        {
            return (
                DesktopErrorCodes.DriverIoError,
                $"CONNECT_DISPLAY returned {response.Result}/" +
                response.MonitorState);
        }

        return null;
    }

    private static (string ErrorCode, string Detail)? GetDisconnectResponseError(
        PanelDisplayCommandResponse response)
    {
        if (response.Result == PanelDisplayCommandResult.AdapterNotReady)
        {
            return (
                DesktopErrorCodes.DriverNotReady,
                "DISCONNECT_DISPLAY reported AdapterNotReady");
        }

        if (response.Result is not (
                PanelDisplayCommandResult.Ok or
                PanelDisplayCommandResult.AlreadyDisconnected) ||
            response.MonitorState != PanelDisplayMonitorState.Disconnected)
        {
            return (
                DesktopErrorCodes.DriverIoError,
                $"DISCONNECT_DISPLAY returned {response.Result}/" +
                response.MonitorState);
        }

        return null;
    }

    private DesktopStartResult FailStart(string errorCode, string detail)
    {
        LogStableFailure("Start", errorCode, detail);
        PublishState(new DesktopServiceSnapshot(
            DesktopServiceState.Error,
            errorCode));
        return StartResult(succeeded: false, alreadyRunning: false);
    }

    private DesktopStopResult FailStop(string errorCode, string detail)
    {
        if (BrokerUnavailable) return StopWithoutBroker();
        LogStableFailure("Stop", errorCode, detail);
        PublishState(new DesktopServiceSnapshot(
            DesktopServiceState.Error,
            errorCode));
        return StopResult(succeeded: false, alreadyStopped: false);
    }

    private void LogStableFailure(string operation, string errorCode, string detail) =>
        _log.WriteLine($"[Desktop] {operation} failed ({errorCode}): {detail}.");

    private void LogDriverFailure(
        string operation,
        PanelDisplayDriverException exception)
    {
        if (exception.Kind == PanelDisplayDriverFailureKind.AccessDenied)
        {
            _log.WriteLine("[Desktop] PanelDisplayDriver access denied.");
        }
        else if (exception.Kind == PanelDisplayDriverFailureKind.NotFound)
        {
            _log.WriteLine("[Desktop] PanelDisplayDriver interface not found.");
        }

        var win32 = exception.Win32ErrorCode is int error
            ? $" Win32={error} (0x{error:X8})."
            : string.Empty;
        _log.WriteLine(
            $"[Desktop] PanelDisplayDriver {operation}/{exception.Operation} " +
            $"failed: {exception.Message}{win32}");
    }

    private static string MapDriverError(
        PanelDisplayDriverException exception) => exception.Kind switch
        {
            PanelDisplayDriverFailureKind.NotFound =>
                DesktopErrorCodes.DriverNotFound,
            PanelDisplayDriverFailureKind.AccessDenied =>
                DesktopErrorCodes.DriverAccessDenied,
            PanelDisplayDriverFailureKind.IoError =>
                DesktopErrorCodes.DriverIoError,
            _ => DesktopErrorCodes.DriverIoError,
        };

    private static DesktopServiceSnapshot RunningSnapshot(
        PanelDisplayDriverStatus status) => new(
        DesktopServiceState.Running,
        Width: status.ActiveMode.Width == 0
            ? null
            : checked((int)status.ActiveMode.Width),
        Height: status.ActiveMode.Height == 0
            ? null
            : checked((int)status.ActiveMode.Height),
        RefreshRate: status.ActiveMode.RefreshHz == 0
            ? null
            : checked((int)status.ActiveMode.RefreshHz));

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
            _log.WriteLine(
                $"[Desktop] State listener failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }
}
