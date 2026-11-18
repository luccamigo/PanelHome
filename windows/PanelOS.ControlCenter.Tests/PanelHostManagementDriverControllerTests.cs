using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class PanelHostManagementDriverControllerTests
{
    [TestMethod]
    public async Task Snapshot_comes_from_management_without_direct_driver_access()
    {
        var management = new ManagementDriverFake
        {
            Snapshot = Snapshot(displayConnected: true),
        };
        var controller = new PanelHostManagementDriverController(
            management,
            management);

        var result = await controller.GetSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverAvailability.Available, result.Availability);
        Assert.AreEqual(PanelMonitorConnection.Connected, result.Monitor);
        Assert.AreEqual(1280u, result.ActiveMode.Width);
        Assert.AreEqual("management://PanelHost/display", result.InterfacePath);
    }

    [TestMethod]
    public async Task Commands_map_to_management_desktop_lifecycle()
    {
        var management = new ManagementDriverFake
        {
            StartResult = new PanelHostManagementDesktopResult(
                true, false, "Running"),
            StopResult = new PanelHostManagementDesktopResult(
                true, true, "Stopped"),
        };
        var controller = new PanelHostManagementDriverController(
            management,
            management);

        var connected = await controller.ConnectAsync(CancellationToken.None);
        var disconnected = await controller.DisconnectAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverCommandStatus.Succeeded, connected.Status);
        Assert.AreEqual(PanelMonitorConnection.Connected, connected.Monitor);
        Assert.AreEqual(
            PanelDriverCommandStatus.AlreadyInDesiredState,
            disconnected.Status);
        Assert.AreEqual(1, management.StartCount);
        Assert.AreEqual(1, management.StopCount);
    }

    [TestMethod]
    public async Task A_declined_elevation_prompt_maps_to_its_own_status()
    {
        var management = new ManagementDriverFake
        {
            StartResult = new PanelHostManagementDesktopResult(
                false, false, "Error", "elevation_declined"),
        };
        var controller = new PanelHostManagementDriverController(management, management);

        var result = await controller.ConnectAsync(CancellationToken.None);

        Assert.AreEqual(PanelDriverCommandStatus.ElevationDeclined, result.Status);
        Assert.IsFalse(result.Succeeded);
    }

    private static PanelHostSnapshot Snapshot(bool displayConnected) => new()
    {
        Sequence = 1,
        ManagementProtocolVersion = PanelHostManagementProtocol.Version,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Host = new HostManagementSnapshot(1, "test", DateTimeOffset.UtcNow, 1),
        Tablet = new TabletManagementSnapshot(false, false, null, [], 0, 0),
        Display = new DisplayManagementSnapshot(
            displayConnected,
            displayConnected ? "Running" : "Stopped",
            displayConnected ? 1280 : null,
            displayConnected ? 800 : null,
            displayConnected ? 60 : null),
        Video = new VideoManagementSnapshot(),
        StreamDeck = new StreamDeckManagementSnapshot(null, null, null, 0, 0),
    };

    private sealed class ManagementDriverFake :
        IPanelHostManagementClient,
        IPanelHostManagementDesktopCommands
    {
        public PanelHostManagementConnectionState ConnectionState =>
            PanelHostManagementConnectionState.Connected;

        public PanelHostSnapshot? Snapshot { get; set; }

        public PanelHostManagementDesktopResult StartResult { get; set; } =
            new(false, false, "Error", "test");

        public PanelHostManagementDesktopResult StopResult { get; set; } =
            new(false, false, "Error", "test");

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

#pragma warning disable CS0067
        public event EventHandler<PanelHostManagementSnapshotChangedEventArgs>?
            SnapshotChanged;
        public event EventHandler<PanelHostManagementConnectionChangedEventArgs>?
            ConnectionChanged;
#pragma warning restore CS0067

        public Task RunAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<PanelHostManagementDesktopResult> StartDesktopAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            return Task.FromResult(StartResult);
        }

        public Task<PanelHostManagementDesktopResult> StopDesktopAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.FromResult(StopResult);
        }

        public void Dispose()
        {
        }
    }
}
