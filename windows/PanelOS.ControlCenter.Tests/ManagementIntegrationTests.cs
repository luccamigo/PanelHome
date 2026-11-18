using System.IO.Pipes;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class ManagementIntegrationTests
{
    [TestMethod]
    public void DashboardUsesHostSnapshotForExternalHostTabletDisplayAndStreamDeck()
    {
        var management = new FakeManagementClient();
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelOS.Windows.Host.PanelHostStatus
            {
                State = PanelOS.Windows.Host.PanelHostProcessState.Running,
                ProcessId = 8080,
                OwnedByControlCenter = false,
            },
        };
        var viewModel = new DashboardViewModel(
            new FakePanelDriverController(),
            host,
            managementClient: management);

        var snapshot = CreateSnapshot();
        management.Publish(snapshot);

        Assert.AreEqual("Conectado", viewModel.TabletStatusText);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.TabletLevel);
        StringAssert.Contains(viewModel.TabletDetail, "Protocolo v1");
        Assert.AreEqual("Conectado", viewModel.MonitorText);
        Assert.AreEqual("1920 × 1200", viewModel.ResolutionText);
        Assert.AreEqual("Publicado no Host", viewModel.StreamDeckStatusText);
        StringAssert.Contains(viewModel.PanelHostProcessText, "4242");
        Assert.IsFalse(viewModel.CanStopPanelHost);
        Assert.IsFalse(viewModel.CanRestartPanelHost);

        management.Publish(snapshot with
        {
            Tablet = snapshot.Tablet with
            {
                ControlConnected = false,
                VideoConnected = false,
                ProtocolVersion = null,
                NegotiatedFeatures = [],
            },
            Video = new VideoManagementSnapshot(),
            StreamDeck = new StreamDeckManagementSnapshot(null, null, null, 0, 0),
        });
        Assert.AreEqual("Desconectado", viewModel.TabletStatusText);
        Assert.AreEqual("Sem perfil", viewModel.StreamDeckStatusText);
    }

    /// <summary>
    /// Bench 01:29: after a failed Start the host display is in Error, the management controller
    /// maps that to an unavailable driver, and the Connect button vanished until the host was
    /// restarted. A lifecycle error keeps the button; a driver that is really absent does not.
    /// </summary>
    [TestMethod]
    public async Task ConnectStaysOfferedAfterTheHostDisplayFailedToStart()
    {
        var management = new FakeManagementClient();
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelOS.Windows.Driver.PanelDriverSnapshot
            {
                Availability = PanelOS.Windows.Driver.PanelDriverAvailability.Error,
                Monitor = PanelOS.Windows.Driver.PanelMonitorConnection.Disconnected,
            },
        };
        var viewModel = new DashboardViewModel(
            driver,
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(CreateSnapshot() with
        {
            Display = new DisplayManagementSnapshot(false, "Error", null, null, null),
        });
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.CanConnect, "the operator must be able to try again");
        Assert.IsFalse(viewModel.IsMonitorConnected);
    }

    [TestMethod]
    public async Task ConnectIsNotOfferedWhenTheDriverIsMissingAndTheHostIsNotInError()
    {
        var management = new FakeManagementClient();
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelOS.Windows.Driver.PanelDriverSnapshot
            {
                Availability = PanelOS.Windows.Driver.PanelDriverAvailability.NotInstalled,
            },
        };
        var viewModel = new DashboardViewModel(
            driver,
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(CreateSnapshot() with
        {
            Display = new DisplayManagementSnapshot(false, "Stopped", null, null, null),
        });
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.CanConnect);
    }

    [TestMethod]
    public void DiagnosticsPublishesMeasuredMetricsAndClearsThemOnDisconnect()
    {
        var management = new FakeManagementClient();
        var viewModel = new DiagnosticsViewModel(
            new FakePanelDriverController(),
            new InMemoryStreamDeckConfigurationStore(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(CreateSnapshot());

        Assert.AreEqual("44,6 FPS", viewModel.RuntimeMetrics
            .Single(metric => metric.Label == "Send FPS").Value);
        Assert.AreEqual("3,25 MB/s", viewModel.RuntimeMetrics
            .Single(metric => metric.Label == "Bandwidth").Value);
        Assert.AreEqual("2", viewModel.RuntimeMetrics
            .Single(metric => metric.Label == "Sequence gap").Value);

        management.Disconnect();

        Assert.IsTrue(viewModel.RuntimeMetrics.All(metric => metric.Value == "—"));
    }

    [TestMethod]
    public async Task ClientReconnectsAndRemovesStaleSnapshotWhenPipeCloses()
    {
        var pipeName = $"PanelOS.ControlCenter.Tests.{Guid.NewGuid():N}";
        using var client = new PanelHostManagementClient(
            pipeName,
            TimeSpan.FromMilliseconds(10));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<PanelHostSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cleared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot is { } snapshot)
            {
                received.TrySetResult(snapshot);
            }
            else
            {
                cleared.TrySetResult();
            }
        };
        var clientTask = client.RunAsync(shutdown.Token);

        await using (var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await server.WaitForConnectionAsync(shutdown.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true);
            await using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
            var request = await reader.ReadLineAsync(shutdown.Token);
            Assert.IsNotNull(request);
            Assert.AreEqual(
                PanelHostManagementProtocol.Subscribe,
                PanelHostManagementProtocol.DeserializeRequest(request)?.Type);
            await writer.WriteLineAsync(
                PanelHostManagementProtocol.SerializeSnapshot(CreateSnapshot()));
            Assert.AreEqual(4242, (await received.Task.WaitAsync(
                TimeSpan.FromSeconds(3))).Host.ProcessId);
        }

        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(client.Snapshot);
        shutdown.Cancel();
        await clientTask;
    }

    [TestMethod]
    public async Task MissingPipeIsANormalDisconnectedState()
    {
        using var client = new PanelHostManagementClient(
            $"PanelOS.ControlCenter.Tests.Missing.{Guid.NewGuid():N}",
            TimeSpan.FromMilliseconds(10));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await client.RunAsync(shutdown.Token);

        Assert.AreEqual(
            PanelHostManagementConnectionState.Disconnected,
            client.ConnectionState);
        Assert.IsNull(client.Snapshot);
    }

    private static PanelHostSnapshot CreateSnapshot() => new()
    {
        Sequence = 9,
        ManagementProtocolVersion = PanelHostManagementProtocol.Version,
        CapturedAtUtc = DateTimeOffset.UtcNow,
        Host = new HostManagementSnapshot(
            4242,
            "1.2.3",
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2),
            120),
        Tablet = new TabletManagementSnapshot(
            true,
            true,
            1,
            ["desktop", "touch", "stream_deck"],
            3,
            4),
        Display = new DisplayManagementSnapshot(true, "Running", 1920, 1200, 60),
        Video = new VideoManagementSnapshot
        {
            Connected = true,
            SessionGeneration = 4,
            Codec = "H264",
            Encoder = "hardware encoder",
            Width = 1920,
            Height = 1200,
            ConfiguredFps = 60,
            NegotiatedFps = 45,
            DriverCaptureFps = 44.8,
            HostReceiveFps = 44.7,
            SendFps = 44.6,
            BandwidthMegabytesPerSecond = 3.25,
            EncodeLatencyMilliseconds = 4.2,
            CaptureToSendMilliseconds = 18.5,
            SequenceGap = 2,
        },
        StreamDeck = new StreamDeckManagementSnapshot(
            "profile-1",
            "Principal",
            "abcdef",
            2,
            8),
    };
}
