using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelOS.Windows.Driver;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class PanelDisplayDriverControllerTests
{
    [TestMethod]
    public void ExpectedProtocolVersionMatchesTheDriverContract()
    {
        Assert.AreEqual(
            PanelDisplayDriverContract.ProtocolVersion,
            PanelDisplayDriverController.ExpectedProtocolVersion);
        Assert.AreEqual(
            new Guid("2F21C12F-51EC-48CB-93D1-776DC7468B1B"),
            PanelDisplayDriverController.DeviceInterfaceGuid);
    }

    [TestMethod]
    public async Task ReadyDriverIsReportedWithItsActiveMode()
    {
        var client = new FakeDriverClient
        {
            DriverState = PanelDisplayDriverState.Ready,
            MonitorState = PanelDisplayMonitorState.Connected,
            ActiveMode = new PanelDisplayMode(1920, 1200, 60),
        };
        var controller = new PanelDisplayDriverController(client);

        var snapshot = await controller.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverAvailability.Available, snapshot.Availability);
        Assert.AreEqual(PanelDriverLifecycle.Ready, snapshot.Lifecycle);
        Assert.AreEqual(PanelMonitorConnection.Connected, snapshot.Monitor);
        Assert.IsTrue(snapshot.IsReady);
        Assert.IsTrue(snapshot.IsMonitorConnected);
        Assert.AreEqual(
            PanelDisplayDriverContract.ProtocolVersion,
            snapshot.ProtocolVersion);
        Assert.AreEqual(1920u, snapshot.ActiveMode.Width);
        Assert.AreEqual(1200u, snapshot.ActiveMode.Height);
        Assert.AreEqual(60u, snapshot.ActiveMode.RefreshHz);
        Assert.AreEqual(1, snapshot.SupportedModes.Count);
        Assert.IsNull(snapshot.Detail);
        Assert.AreEqual(@"\\?\panel-display-test", snapshot.InterfacePath);
    }

    [TestMethod]
    public async Task MissingDriverIsReportedInsteadOfThrowing()
    {
        var client = new FakeDriverClient
        {
            StatusFailure = FakeDriverClient.Failure(
                PanelDisplayDriverFailureKind.NotFound),
        };
        var controller = new PanelDisplayDriverController(client);

        var snapshot = await controller.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverAvailability.NotInstalled, snapshot.Availability);
        Assert.AreEqual(PanelDriverLifecycle.Unknown, snapshot.Lifecycle);
        Assert.AreEqual(PanelMonitorConnection.Unknown, snapshot.Monitor);
        Assert.IsFalse(snapshot.IsAvailable);
        Assert.IsNotNull(snapshot.Detail);
    }

    [TestMethod]
    public async Task AccessDeniedIsDistinctFromMissing()
    {
        var client = new FakeDriverClient
        {
            StatusFailure = FakeDriverClient.Failure(
                PanelDisplayDriverFailureKind.AccessDenied),
        };
        var controller = new PanelDisplayDriverController(client);

        var snapshot = await controller.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverAvailability.AccessDenied, snapshot.Availability);
    }

    [TestMethod]
    public async Task UnexpectedFailureIsReportedAsAnError()
    {
        var client = new FakeDriverClient
        {
            StatusFailure = new InvalidOperationException("boom"),
        };
        var controller = new PanelDisplayDriverController(client);

        var snapshot = await controller.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverAvailability.Error, snapshot.Availability);
        StringAssert.Contains(snapshot.Detail!, "boom");
    }

    [TestMethod]
    public async Task ConnectAndDisconnectReportTheResultingMonitorState()
    {
        var client = new FakeDriverClient();
        var controller = new PanelDisplayDriverController(client);

        var connect = await controller.ConnectAsync(CancellationToken.None);
        Assert.AreEqual(PanelDriverCommandStatus.Succeeded, connect.Status);
        Assert.AreEqual(PanelMonitorConnection.Connected, connect.Monitor);
        Assert.IsTrue(connect.Succeeded);

        var disconnect = await controller.DisconnectAsync(CancellationToken.None);
        Assert.AreEqual(PanelDriverCommandStatus.Succeeded, disconnect.Status);
        Assert.AreEqual(PanelMonitorConnection.Disconnected, disconnect.Monitor);
        Assert.AreEqual(1, client.ConnectCalls);
        Assert.AreEqual(1, client.DisconnectCalls);
    }

    [TestMethod]
    public async Task IdempotentResultsAreSuccesses()
    {
        var client = new FakeDriverClient
        {
            ConnectResult = PanelDisplayCommandResult.AlreadyConnected,
            DisconnectResult = PanelDisplayCommandResult.AlreadyDisconnected,
        };
        var controller = new PanelDisplayDriverController(client);

        var connect = await controller.ConnectAsync(CancellationToken.None);
        var disconnect = await controller.DisconnectAsync(CancellationToken.None);

        Assert.AreEqual(
            PanelDriverCommandStatus.AlreadyInDesiredState,
            connect.Status);
        Assert.AreEqual(
            PanelDriverCommandStatus.AlreadyInDesiredState,
            disconnect.Status);
        Assert.IsTrue(connect.Succeeded && disconnect.Succeeded);
    }

    [TestMethod]
    public async Task AdapterNotReadyIsReportedAsDriverNotReady()
    {
        var client = new FakeDriverClient
        {
            ConnectResult = PanelDisplayCommandResult.AdapterNotReady,
        };
        var controller = new PanelDisplayDriverController(client);

        var result = await controller.ConnectAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverCommandStatus.DriverNotReady, result.Status);
        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task CommandOnAMissingDriverIsReportedInsteadOfThrowing()
    {
        var client = new FakeDriverClient
        {
            CommandFailure = FakeDriverClient.Failure(
                PanelDisplayDriverFailureKind.NotFound,
                "CONNECT_DISPLAY"),
        };
        var controller = new PanelDisplayDriverController(client);

        var result = await controller.ConnectAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverCommandStatus.DriverNotInstalled, result.Status);
        Assert.IsFalse(result.Succeeded);
    }
}
