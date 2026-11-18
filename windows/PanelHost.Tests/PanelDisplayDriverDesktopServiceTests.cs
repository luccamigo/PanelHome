using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Protocol;
using PanelHost.Services;
using PanelOS.DisplayBroker.Protocol;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelDisplayDriverDesktopServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// PAN-3: a broker that dies or is ended by its watchdog cannot disconnect the monitor (the driver
    /// keeps it, with no owner). The host never relaunches a broker just to clean up, so the leftover
    /// monitor is recovered on the next explicit Start (adopted, no second CONNECT) and removed by the
    /// Stop that follows.
    /// </summary>
    [TestMethod]
    public async Task Pan3_monitor_left_by_a_lost_broker_is_adopted_by_the_next_start_and_removed_by_stop()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        var first = await service.StartAsync(new DesktopRequest(), CancellationToken.None).WaitAsync(TestTimeout);
        Assert.IsTrue(first.Succeeded);

        client.SimulateBrokerDisconnect(PanelDisplayBrokerDisconnectReason.Unresponsive);
        var leave = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.IsTrue(leave.Succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(0, client.DisconnectCount, "no broker means no driver access and no UAC just to clean up");
        Assert.AreEqual(PanelDisplayMonitorState.Connected, client.MonitorState, "the residual monitor is still there");

        client.SimulateBrokerRestored();
        var restart = await service.StartAsync(new DesktopRequest(), CancellationToken.None).WaitAsync(TestTimeout);
        Assert.IsTrue(restart.Succeeded);
        Assert.IsTrue(restart.AlreadyRunning, "the leftover monitor is adopted");
        Assert.AreEqual(0, client.ConnectCount);

        var stop = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
        Assert.IsTrue(stop.Succeeded);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Pan4_stop_without_a_broker_does_not_open_driver_or_launch_broker()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw new InvalidOperationException("Stop must not reconnect"),
        };
        client.SimulateBrokerDisconnect();
        await using var service = new PanelDisplayDriverDesktopService(client);
        var states = CaptureStates(service);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stop = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);
            Assert.IsTrue(stop.Succeeded);
            Assert.IsTrue(stop.AlreadyStopped);
            Assert.AreEqual(DesktopServiceState.Stopped, stop.State);
            Assert.IsNull(stop.ErrorCode);
        }

        Assert.AreEqual(0, client.Calls.Count);
        CollectionAssert.AreEqual(new[] {
            DesktopServiceState.Stopping, DesktopServiceState.Stopped,
            DesktopServiceState.Stopping, DesktopServiceState.Stopped,
        }, states);
    }

    [DataTestMethod]
    [DataRow(false, DesktopErrorCodes.DriverIoError)]
    [DataRow(true, DesktopErrorCodes.ElevationDeclined)]
    public async Task Pan4_failed_start_can_return_to_stopped_without_retrying_dead_broker(
        bool elevationDeclined, string expectedStartError)
    {
        var client = new FakePanelDisplayDriverClient();
        if (elevationDeclined) client.SimulateBrokerDisconnect();
        client.GetStatusHandler = _ =>
        {
            if (elevationDeclined)
                throw new PanelDisplayBrokerDisconnectedException(
                    PanelDisplayBrokerDisconnectReason.ElevationDeclined,
                    "Synthetic elevation cancellation",
                    new System.ComponentModel.Win32Exception(1223));
            client.SimulateBrokerDisconnect();
            throw new IOException("Pipe is broken.");
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        AssertFailed(await service.StartAsync(new DesktopRequest(), CancellationToken.None), expectedStartError);
        var calls = client.GetStatusCount;
        var states = CaptureStates(service);

        var result = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
        Assert.AreEqual(calls, client.GetStatusCount);
        Assert.AreEqual(0, client.DisconnectCount);
        CollectionAssert.AreEqual(new[] { DesktopServiceState.Stopping, DesktopServiceState.Stopped }, states);
    }

    /// <summary>
    /// Bench 01:29: a broker that dropped the lease while starting reached the operator as
    /// driver_io_error / "unexpected driver client failure". It is a broker disconnect, and the
    /// Start that follows must be allowed (the Control Center hid its Connect button instead).
    /// </summary>
    [DataTestMethod]
    [DataRow(PanelDisplayBrokerDisconnectReason.PipeClosed, DesktopErrorCodes.BrokerDisconnected)]
    [DataRow(PanelDisplayBrokerDisconnectReason.BrokerExited, DesktopErrorCodes.BrokerDisconnected)]
    [DataRow(PanelDisplayBrokerDisconnectReason.AuthenticationFailed, DesktopErrorCodes.BrokerDisconnected)]
    [DataRow(PanelDisplayBrokerDisconnectReason.ElevationDeclined, DesktopErrorCodes.ElevationDeclined)]
    public async Task Start_failed_by_the_broker_reports_a_specific_code_and_a_new_start_is_allowed(
        PanelDisplayBrokerDisconnectReason reason, string expectedCode)
    {
        var client = new FakePanelDisplayDriverClient();
        client.GetStatusHandler = _ => throw new PanelDisplayBrokerDisconnectedException(
            reason, "Synthetic startup failure");
        await using var service = new PanelDisplayDriverDesktopService(client);

        var failed = await service.StartAsync(new DesktopRequest(), CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.IsFalse(failed.Succeeded);
        Assert.AreEqual(DesktopServiceState.Error, service.State);
        Assert.AreEqual(expectedCode, service.ErrorCode);

        client.GetStatusHandler = null;
        var retry = await service.StartAsync(new DesktopRequest(), CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.IsTrue(retry.Succeeded);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
        Assert.IsNull(service.ErrorCode);
    }

    [TestMethod]
    public async Task Pan4_broker_dying_during_stop_completes_local_stop()
    {
        var client = new FakePanelDisplayDriverClient();
        await using var service = new PanelDisplayDriverDesktopService(client);
        Assert.IsTrue((await service.StartAsync(new DesktopRequest(), CancellationToken.None)).Succeeded);
        client.GetStatusHandler = _ =>
        {
            client.SimulateBrokerDisconnect();
            throw new IOException("Pipe is broken.");
        };

        var result = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, result.State);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual(0, client.DisconnectCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Pan4_lost_broker_during_cancellation_publishes_stopped(bool callerCancelled)
    {
        var client = new FakePanelDisplayDriverClient();
        await using var service = new PanelDisplayDriverDesktopService(client);
        Assert.IsTrue((await service.StartAsync(new DesktopRequest(), CancellationToken.None)).Succeeded);
        using var cancellation = new CancellationTokenSource();
        client.GetStatusHandler = _ =>
        {
            client.SimulateBrokerDisconnect();
            if (callerCancelled) cancellation.Cancel();
            throw new OperationCanceledException();
        };

        if (callerCancelled)
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(
                () => service.StopAsync(cancellation.Token).WaitAsync(TestTimeout));
        else
            Assert.IsTrue((await service.StopAsync(cancellation.Token).WaitAsync(TestTimeout)).Succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
    }

    [TestMethod]
    public async Task Pan4_broker_dying_during_compensating_stop_publishes_stopped()
    {
        var client = new FakePanelDisplayDriverClient();
        await using var service = new PanelDisplayDriverDesktopService(client);
        Assert.IsTrue((await service.StartAsync(new DesktopRequest(), CancellationToken.None)).Succeeded);
        client.DisconnectHandler = _ =>
        {
            if (client.DisconnectCount == 1)
                return Task.FromResult(new PanelDisplayCommandResponse(
                    PanelDisplayCommandResult.AdapterNotReady, PanelDisplayMonitorState.Connected));
            client.SimulateBrokerDisconnect();
            return Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok, PanelDisplayMonitorState.Disconnected));
        };
        var statusCalls = client.GetStatusCount;

        var result = await service.StopAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(statusCalls + 1, client.GetStatusCount);
    }

    [TestMethod]
    public async Task Pan4_live_broker_io_failure_is_not_reported_as_stopped()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw new IOException("Synthetic live broker driver failure"),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        AssertFailed(await service.StopAsync(CancellationToken.None), DesktopErrorCodes.DriverIoError);
        Assert.IsTrue(client.IsConnected);
    }

    [TestMethod]
    public async Task Broker_disconnect_publishes_typed_safe_desktop_failure()
    {
        var client = new FakePanelDisplayDriverClient();
        using var log = new StringWriter();
        await using var service = new PanelDisplayDriverDesktopService(client, log);
        Assert.IsTrue((await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None)).Succeeded);

        client.SimulateBrokerDisconnect(
            PanelDisplayBrokerDisconnectReason.BrokerExited);

        Assert.AreEqual(DesktopServiceState.Error, service.State);
        Assert.AreEqual(DesktopErrorCodes.BrokerDisconnected, service.ErrorCode);
        StringAssert.Contains(log.ToString(), "stopped safely");
    }

    [TestMethod]
    public async Task Disconnected_driver_start_connects_confirms_and_runs()
    {
        var client = new FakePanelDisplayDriverClient();
        using var log = new StringWriter();
        await using var service = new PanelDisplayDriverDesktopService(client, log);
        var states = CaptureStates(service);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
        Assert.AreEqual(PanelDisplayMonitorState.Connected, client.MonitorState);
        AssertCalls(
            client,
            FakePanelDisplayDriverOperation.GetStatus,
            FakePanelDisplayDriverOperation.Connect,
            FakePanelDisplayDriverOperation.GetStatus);
        CollectionAssert.AreEqual(
            new[] { DesktopServiceState.Starting, DesktopServiceState.Running },
            states);
        StringAssert.Contains(log.ToString(), "CONNECT_DISPLAY requested");
        StringAssert.Contains(log.ToString(), "Driver status: Connected");
    }

    [TestMethod]
    public async Task Capture_target_is_persisted_before_connect_and_reapplied_after_reconnect()
    {
        var client = new FakePanelDisplayDriverClient();
        using var log = new StringWriter();
        await using var service = new PanelDisplayDriverDesktopService(
            client,
            log,
            captureTargetFps: 60);

        var firstStart = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsTrue(firstStart.Succeeded);
        Assert.AreEqual(60u, client.ConfiguredTargetFps);
        AssertCalls(
            client,
            FakePanelDisplayDriverOperation.GetStatus,
            FakePanelDisplayDriverOperation.SetCaptureTarget,
            FakePanelDisplayDriverOperation.Connect,
            FakePanelDisplayDriverOperation.GetStatus);

        var stop = await service.StopAsync(CancellationToken.None);
        Assert.IsTrue(stop.Succeeded);
        var secondStart = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsTrue(secondStart.Succeeded);
        Assert.AreEqual(2, client.SetCaptureTargetCount);
        StringAssert.Contains(
            log.ToString(),
            "Pre-connect target=60 success=true configured=60 protocol=v5");
    }

    [TestMethod]
    public async Task Connected_driver_start_is_idempotent_without_connect_ioctl()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
        Assert.AreEqual(0, client.ConnectCount);
        AssertCalls(client, FakePanelDisplayDriverOperation.GetStatus);
    }

    [TestMethod]
    public async Task Running_service_stop_disconnects_confirms_and_stops()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        using var log = new StringWriter();
        await using var service = new PanelDisplayDriverDesktopService(client, log);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);
        var states = CaptureStates(service);

        var result = await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.AlreadyStopped);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
        Assert.AreEqual(1, client.DisconnectCount);
        CollectionAssert.AreEqual(
            new[] { DesktopServiceState.Stopping, DesktopServiceState.Stopped },
            states);
        StringAssert.Contains(log.ToString(), "DISCONNECT_DISPLAY requested");
        StringAssert.Contains(log.ToString(), "Driver status: Disconnected");
    }

    [TestMethod]
    public async Task Disconnected_driver_stop_is_idempotent_without_disconnect_ioctl()
    {
        var client = new FakePanelDisplayDriverClient();
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyStopped);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(0, client.DisconnectCount);
        AssertCalls(client, FakePanelDisplayDriverOperation.GetStatus);
    }

    [TestMethod]
    public async Task Missing_driver_returns_stable_not_found_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.NotFound,
                "locate interface",
                win32ErrorCode: 1168),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverNotFound);
        Assert.AreEqual(0, client.ConnectCount);
    }

    [TestMethod]
    public async Task Stop_after_missing_driver_start_completes_and_remains_idempotent()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.NotFound,
                "locate interface",
                win32ErrorCode: 1168),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        var start = await service.StartAsync(new DesktopRequest(), CancellationToken.None);
        AssertFailed(start, DesktopErrorCodes.DriverNotFound);
        var states = CaptureStates(service);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stop = await service.StopAsync(CancellationToken.None);

            Assert.IsTrue(stop.Succeeded);
            Assert.IsTrue(stop.AlreadyStopped);
            Assert.AreEqual(DesktopServiceState.Stopped, stop.State);
            Assert.IsNull(stop.ErrorCode);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                DesktopServiceState.Stopping, DesktopServiceState.Stopped,
                DesktopServiceState.Stopping, DesktopServiceState.Stopped,
            },
            states);
        Assert.AreEqual(0, client.DisconnectCount);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Driver_absent_after_disconnect_is_stopped_without_compensation(bool duringConfirmation)
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        client.DisconnectHandler = _ =>
        {
            if (!duringConfirmation)
                throw FakePanelDisplayDriverClient.CreateFailure(
                    PanelDisplayDriverFailureKind.NotFound, "DISCONNECT_DISPLAY");
            client.MonitorState = PanelDisplayMonitorState.Disconnected;
            return Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok, PanelDisplayMonitorState.Disconnected));
        };
        client.GetStatusHandler = cancellationToken =>
        {
            if (client.DisconnectCount > 0)
                throw FakePanelDisplayDriverClient.CreateFailure(
                    PanelDisplayDriverFailureKind.NotFound, "GET_STATUS");
            return Task.FromResult(client.GetCurrentStatus(cancellationToken));
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.AlreadyStopped);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(duringConfirmation ? 2 : 1, client.GetStatusCount);
    }

    [TestMethod]
    public async Task Access_denied_returns_stable_error_and_clear_log()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.AccessDenied,
                "open interface",
                win32ErrorCode: 5),
        };
        using var log = new StringWriter();
        await using var service = new PanelDisplayDriverDesktopService(client, log);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverAccessDenied);
        StringAssert.Contains(
            log.ToString(),
            "[Desktop] PanelDisplayDriver access denied.");
        StringAssert.Contains(log.ToString(), "Win32=5 (0x00000005)");
    }

    [DataTestMethod]
    [DataRow((int)PanelDisplayDriverState.Initializing)]
    [DataRow((int)PanelDisplayDriverState.Stopped)]
    public async Task Non_ready_driver_lifecycle_rejects_start(
        int driverStateValue)
    {
        var client = new FakePanelDisplayDriverClient
        {
            DriverState = (PanelDisplayDriverState)driverStateValue,
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverNotReady);
        Assert.AreEqual(0, client.ConnectCount);
    }

    [TestMethod]
    public async Task Adapter_not_ready_connect_response_returns_stable_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            ConnectHandler = _ => Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.AdapterNotReady,
                PanelDisplayMonitorState.Disconnected)),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverNotReady);
        Assert.AreEqual(1, client.ConnectCount);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Connect_ioctl_failure_returns_stable_io_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            ConnectHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.IoError,
                "CONNECT_DISPLAY",
                win32ErrorCode: 31),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        AssertCalls(
            client,
            FakePanelDisplayDriverOperation.GetStatus,
            FakePanelDisplayDriverOperation.Connect,
            FakePanelDisplayDriverOperation.Disconnect,
            FakePanelDisplayDriverOperation.GetStatus);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Connect_failed_driver_response_returns_stable_io_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            ConnectHandler = _ => Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Failed,
                PanelDisplayMonitorState.Disconnected)),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(1, client.ConnectCount);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(2, client.GetStatusCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Disconnect_ioctl_failure_returns_stable_io_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        var disconnectCalls = 0;
        client.DisconnectHandler = cancellationToken =>
        {
            if (Interlocked.Increment(ref disconnectCalls) == 1)
            {
                throw FakePanelDisplayDriverClient.CreateFailure(
                    PanelDisplayDriverFailureKind.IoError,
                    "DISCONNECT_DISPLAY",
                    win32ErrorCode: 31);
            }

            client.MonitorState = PanelDisplayMonitorState.Disconnected;
            return Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok,
                PanelDisplayMonitorState.Disconnected));
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);

        var result = await service.StopAsync(CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(DesktopServiceState.Error, service.State);
        Assert.AreEqual(2, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Disconnect_failed_driver_response_returns_stable_io_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        var disconnectCalls = 0;
        client.DisconnectHandler = _ =>
        {
            if (Interlocked.Increment(ref disconnectCalls) == 1)
            {
                return Task.FromResult(new PanelDisplayCommandResponse(
                    PanelDisplayCommandResult.Failed,
                    PanelDisplayMonitorState.Connected));
            }

            client.MonitorState = PanelDisplayMonitorState.Disconnected;
            return Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok,
                PanelDisplayMonitorState.Disconnected));
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StopAsync(CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(2, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Status_ioctl_failure_returns_stable_io_error()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.IoError,
                "GET_STATUS",
                win32ErrorCode: 31),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(0, client.ConnectCount);
    }

    [TestMethod]
    public async Task Driver_disappearing_between_status_and_connect_returns_not_found()
    {
        var client = new FakePanelDisplayDriverClient
        {
            ConnectHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.NotFound,
                "CONNECT_DISPLAY",
                win32ErrorCode: 1167),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverNotFound);
        AssertCalls(
            client,
            FakePanelDisplayDriverOperation.GetStatus,
            FakePanelDisplayDriverOperation.Connect,
            FakePanelDisplayDriverOperation.Disconnect,
            FakePanelDisplayDriverOperation.GetStatus);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Connect_response_without_status_confirmation_never_runs()
    {
        var client = new FakePanelDisplayDriverClient
        {
            ConnectHandler = _ => Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok,
                PanelDisplayMonitorState.Connected)),
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
        Assert.AreEqual(3, client.GetStatusCount);
        Assert.AreEqual(1, client.DisconnectCount);
    }

    [TestMethod]
    public async Task Disconnect_response_without_status_confirmation_never_stops()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        var disconnectCalls = 0;
        client.DisconnectHandler = _ =>
        {
            if (Interlocked.Increment(ref disconnectCalls) == 1)
            {
                return Task.FromResult(new PanelDisplayCommandResponse(
                    PanelDisplayCommandResult.Ok,
                    PanelDisplayMonitorState.Disconnected));
            }

            client.MonitorState = PanelDisplayMonitorState.Disconnected;
            return Task.FromResult(new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok,
                PanelDisplayMonitorState.Disconnected));
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);

        var result = await service.StopAsync(CancellationToken.None);

        AssertFailed(result, DesktopErrorCodes.DriverIoError);
        Assert.AreEqual(2, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
        Assert.AreEqual(DesktopServiceState.Error, service.State);
    }

    [TestMethod]
    public async Task Start_retries_successfully_after_driver_reappears()
    {
        var client = new FakePanelDisplayDriverClient();
        var statusCalls = 0;
        client.GetStatusHandler = cancellationToken =>
        {
            if (Interlocked.Increment(ref statusCalls) == 1)
            {
                throw FakePanelDisplayDriverClient.CreateFailure(
                    PanelDisplayDriverFailureKind.NotFound,
                    "locate interface");
            }

            return Task.FromResult(client.GetCurrentStatus(cancellationToken));
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var failed = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        var recovered = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        AssertFailed(failed, DesktopErrorCodes.DriverNotFound);
        Assert.IsTrue(recovered.Succeeded);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
        Assert.AreEqual(1, client.ConnectCount);
    }

    [TestMethod]
    public async Task Driver_status_overrides_stale_local_running_state()
    {
        var client = new FakePanelDisplayDriverClient();
        await using var service = new PanelDisplayDriverDesktopService(client);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);
        client.MonitorState = PanelDisplayMonitorState.Disconnected;

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.AlreadyRunning);
        Assert.AreEqual(2, client.ConnectCount);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
    }

    [TestMethod]
    public async Task Driver_status_overrides_stale_local_stopped_state()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var result = await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(result.AlreadyStopped);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
    }

    [TestMethod]
    public async Task Concurrent_starts_are_serialized_and_connect_only_once()
    {
        var client = new FakePanelDisplayDriverClient();
        var firstStatusEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStatus = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCalls = 0;
        client.GetStatusHandler = async cancellationToken =>
        {
            if (Interlocked.Increment(ref statusCalls) == 1)
            {
                firstStatusEntered.TrySetResult(true);
                await releaseFirstStatus.Task.WaitAsync(cancellationToken);
            }

            return client.GetCurrentStatus(cancellationToken);
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        var firstStart = service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        await firstStatusEntered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(firstStart.IsCompleted);
        var secondStart = service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        Assert.IsFalse(secondStart.IsCompleted);
        releaseFirstStatus.TrySetResult(true);

        var results = await Task.WhenAll(firstStart, secondStart)
            .WaitAsync(TestTimeout);

        Assert.IsTrue(results.All(result => result.Succeeded));
        Assert.AreEqual(1, results.Count(result => result.AlreadyRunning));
        Assert.AreEqual(1, client.ConnectCount);
        Assert.AreEqual(1, client.MaxConcurrentCalls);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
    }

    [TestMethod]
    public async Task Stop_during_start_is_serialized_and_finishes_disconnected()
    {
        var client = new FakePanelDisplayDriverClient();
        var firstStatusEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStatus = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var statusCalls = 0;
        client.GetStatusHandler = async cancellationToken =>
        {
            if (Interlocked.Increment(ref statusCalls) == 1)
            {
                firstStatusEntered.TrySetResult(true);
                await releaseFirstStatus.Task.WaitAsync(cancellationToken);
            }

            return client.GetCurrentStatus(cancellationToken);
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        var states = CaptureStates(service);

        var start = service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        await firstStatusEntered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(start.IsCompleted);
        var stop = service.StopAsync(CancellationToken.None);
        Assert.IsFalse(stop.IsCompleted);
        releaseFirstStatus.TrySetResult(true);

        var startResult = await start.WaitAsync(TestTimeout);
        var stopResult = await stop.WaitAsync(TestTimeout);

        Assert.IsTrue(startResult.Succeeded);
        Assert.IsTrue(stopResult.Succeeded);
        Assert.AreEqual(1, client.ConnectCount);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(1, client.MaxConcurrentCalls);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        CollectionAssert.AreEqual(
            new[]
            {
                DesktopServiceState.Starting,
                DesktopServiceState.Running,
                DesktopServiceState.Stopping,
                DesktopServiceState.Stopped,
            },
            states);
    }

    [TestMethod]
    public async Task Cancellation_after_connect_effect_compensates_disconnect()
    {
        var client = new FakePanelDisplayDriverClient();
        using var cancellation = new CancellationTokenSource();
        client.ConnectHandler = cancellationToken =>
        {
            client.MonitorState = PanelDisplayMonitorState.Connected;
            cancellation.Cancel();
            return Task.FromCanceled<PanelDisplayCommandResponse>(
                cancellationToken);
        };
        await using var service = new PanelDisplayDriverDesktopService(client);

        try
        {
            await service.StartAsync(new DesktopRequest(), cancellation.Token);
            Assert.Fail("Start should have been cancelled.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
    }

    [TestMethod]
    public async Task Cancellation_racing_completed_status_never_publishes_running()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        using var cancellation = new CancellationTokenSource();
        client.GetStatusHandler = _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(client.GetCurrentStatus());
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        var states = CaptureStates(service);

        try
        {
            await service.StartAsync(new DesktopRequest(), cancellation.Token);
            Assert.Fail("Start should have observed cancellation after GET_STATUS.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(0, client.ConnectCount);
        Assert.AreEqual(0, client.DisconnectCount);
        CollectionAssert.AreEqual(
            new[] { DesktopServiceState.Starting, DesktopServiceState.Stopped },
            states);
    }

    [TestMethod]
    public async Task Stop_returns_before_async_driver_wait_and_cancellation_cleans_up()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        await using var service = new PanelDisplayDriverDesktopService(client);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);
        var disconnectEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectCalls = 0;
        client.DisconnectHandler = async cancellationToken =>
        {
            if (Interlocked.Increment(ref disconnectCalls) == 1)
            {
                disconnectEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new AssertFailedException("Cancellation was not observed.");
            }

            client.MonitorState = PanelDisplayMonitorState.Disconnected;
            return new PanelDisplayCommandResponse(
                PanelDisplayCommandResult.Ok,
                PanelDisplayMonitorState.Disconnected);
        };
        using var cancellation = new CancellationTokenSource();

        var stopTask = service.StopAsync(cancellation.Token);

        Assert.IsFalse(stopTask.IsCompleted);
        await disconnectEntered.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(stopTask.IsCompleted);
        cancellation.Cancel();

        try
        {
            await stopTask.WaitAsync(TestTimeout);
            Assert.Fail("Stop should propagate its original cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(2, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
    }

    [TestMethod]
    public async Task Shutdown_helper_with_driver_service_issues_disconnect_ioctl()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
        };
        await using var service = new PanelDisplayDriverDesktopService(
            client,
            TextWriter.Null);
        using var log = new StringWriter();

        var succeeded = await DesktopShutdown.StopAsync(
            service,
            log,
            "driver test",
            TestTimeout);

        Assert.IsTrue(succeeded);
        Assert.AreEqual(1, client.DisconnectCount);
        Assert.AreEqual(PanelDisplayMonitorState.Disconnected, client.MonitorState);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        StringAssert.Contains(log.ToString(), "[Desktop] Stopped");
    }

    [TestMethod]
    public async Task Shutdown_helper_treats_missing_driver_as_stopped_and_returns_promptly()
    {
        var client = new FakePanelDisplayDriverClient
        {
            MonitorState = PanelDisplayMonitorState.Connected,
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.NotFound,
                "locate interface",
                win32ErrorCode: 1168),
        };
        await using var service = new PanelDisplayDriverDesktopService(
            client,
            TextWriter.Null);
        using var log = new StringWriter();

        var succeeded = await DesktopShutdown.StopAsync(
                service,
                log,
                "missing driver test",
                TimeSpan.FromMilliseconds(250))
            .WaitAsync(TestTimeout);

        Assert.IsTrue(succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.IsNull(service.ErrorCode);
        StringAssert.Contains(log.ToString(), "[Desktop] Stopped");
    }

    [DataTestMethod]
    [DataRow(DesktopErrorCodes.DriverNotFound, "driver_not_found")]
    [DataRow(DesktopErrorCodes.DriverAccessDenied, "driver_access_denied")]
    [DataRow(DesktopErrorCodes.DriverNotReady, "driver_not_ready")]
    [DataRow(DesktopErrorCodes.DriverIoError, "driver_io_error")]
    public void Driver_error_code_literals_are_stable(
        string errorCode,
        string expectedLiteral)
    {
        Assert.AreEqual(expectedLiteral, errorCode);
    }

    private static List<DesktopServiceState> CaptureStates(
        IDesktopService service)
    {
        var states = new List<DesktopServiceState>();
        service.StateChanged += (_, eventArgs) =>
            states.Add(eventArgs.State.State);
        return states;
    }

    private static void AssertFailed(
        DesktopStartResult result,
        string expectedErrorCode)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Error, result.State);
        Assert.AreEqual(expectedErrorCode, result.ErrorCode);
    }

    private static void AssertFailed(
        DesktopStopResult result,
        string expectedErrorCode)
    {
        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.AlreadyStopped);
        Assert.AreEqual(DesktopServiceState.Error, result.State);
        Assert.AreEqual(expectedErrorCode, result.ErrorCode);
    }

    private static void AssertCalls(
        FakePanelDisplayDriverClient client,
        params FakePanelDisplayDriverOperation[] expected) =>
        CollectionAssert.AreEqual(expected, client.Calls.ToArray());
}
