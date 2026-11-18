using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Management;
using PanelHost.Services;
using PanelHost.Video;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelHostManagementTests
{
    [TestMethod]
    public void ManagementPipeGrantsOnlyTheWindowsUserWithLimitedRights()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var state = new PanelHostRuntimeState();
        var server = new PanelHostManagementServer(
            state, $"PanelOS.Host.Tests.{Guid.NewGuid():N}");
        using var pipe = (NamedPipeServerStream)typeof(PanelHostManagementServer)
            .GetMethod("CreatePipe", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(server, null)!;
        var security = pipe.GetAccessControl();
        Assert.AreEqual(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        Assert.AreEqual(1, rules.Count);
        var rule = (PipeAccessRule)rules[0]!;
        Assert.AreEqual(identity.User, rule.IdentityReference);
        Assert.AreEqual(AccessControlType.Allow, rule.AccessControlType);
        Assert.AreEqual(
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance |
                PipeAccessRights.Synchronize,
            rule.PipeAccessRights);
    }

    [TestMethod]
    public void RuntimeStatePublishesAtomicLifecycleAndMeasuredVideoState()
    {
        var clock = new ManagementTestTimeProvider();
        using var state = new PanelHostRuntimeState(
            clock,
            TimeSpan.FromSeconds(1),
            processId: 42,
            version: "1.2.3",
            startedAtUtc: clock.GetUtcNow());
        var notifications = 0;
        state.SnapshotChanged += (_, _) => notifications++;

        state.DisplayChanged(new DesktopServiceSnapshot(
            DesktopServiceState.Running,
            Width: 1920,
            Height: 1200,
            RefreshRate: 60));
        var controlGeneration = state.ControlConnected(1, ["desktop", "touch"]);
        var videoGeneration = state.SessionConnected(
            new VideoAck(VideoCodec.H264, 1920, 1200, 45),
            configuredFps: 60);
        state.MetricsUpdated(
            videoGeneration,
            new PanelVideoRuntimeMetrics(
                "H264", "encoder", 1920, 1200, 60, 45,
                44.8, 44.7, 44.6, 3.25, 4.2, 18.5, 2));
        var streamDeck = StreamDeckSnapshot.Create(
            StreamDeckDefaults.CreateConfiguration());
        state.StreamDeckChanged(streamDeck);

        var snapshot = state.Snapshot;
        Assert.AreEqual(PanelHostManagementProtocol.Version,
            snapshot.ManagementProtocolVersion);
        Assert.AreEqual(42, snapshot.Host.ProcessId);
        Assert.AreEqual("1.2.3", snapshot.Host.Version);
        Assert.IsTrue(snapshot.Display.Connected);
        Assert.AreEqual(1920, snapshot.Display.Width);
        Assert.IsTrue(snapshot.Tablet.ControlConnected);
        Assert.IsTrue(snapshot.Tablet.VideoConnected);
        CollectionAssert.AreEqual(
            new[] { "desktop", "touch" },
            snapshot.Tablet.NegotiatedFeatures.ToArray());
        Assert.AreEqual(45, snapshot.Video.NegotiatedFps);
        Assert.AreEqual(44.6, snapshot.Video.SendFps);
        Assert.AreEqual(2L, snapshot.Video.SequenceGap);
        Assert.AreEqual(streamDeck.Revision, snapshot.StreamDeck.PublishedRevision);
        Assert.AreEqual(streamDeck.ProfileId, snapshot.StreamDeck.ActiveProfileId);
        Assert.AreEqual(streamDeck.PageCount, snapshot.StreamDeck.PageCount);

        state.ControlDisconnected(controlGeneration);
        state.SessionDisconnected(videoGeneration);
        snapshot = state.Snapshot;
        Assert.IsFalse(snapshot.Tablet.ControlConnected);
        Assert.IsFalse(snapshot.Tablet.VideoConnected);
        Assert.IsFalse(snapshot.Video.Connected);
        Assert.IsTrue(notifications >= 5);
    }

    [TestMethod]
    public void VideoNotificationsAreCoalescedButLastKnownValuesRemainCurrent()
    {
        var clock = new ManagementTestTimeProvider();
        using var state = new PanelHostRuntimeState(
            clock,
            TimeSpan.FromSeconds(1));
        var generation = state.SessionConnected(
            new VideoAck(VideoCodec.H264, 800, 600, 30),
            30);
        var notifications = 0;
        state.SnapshotChanged += (_, _) => notifications++;

        state.MetricsUpdated(generation, Metrics(sendFps: 10));
        state.MetricsUpdated(generation, Metrics(sendFps: 20));
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(20d, state.Snapshot.Video.SendFps);

        clock.Advance(TimeSpan.FromSeconds(1));
        state.MetricsUpdated(generation, Metrics(sendFps: 30));
        Assert.AreEqual(2, notifications);
        Assert.AreEqual(30d, state.Snapshot.Video.SendFps);
    }

    [TestMethod]
    public void ObsoleteSessionTeardownCannotClearANewerSession()
    {
        using var state = new PanelHostRuntimeState();
        var oldControl = state.ControlConnected(1, ["desktop"]);
        var newControl = state.ControlConnected(1, ["desktop", "touch"]);
        var oldVideo = state.SessionConnected(
            new VideoAck(VideoCodec.H264, 800, 600, 30),
            30);
        var newVideo = state.SessionConnected(
            new VideoAck(VideoCodec.H264, 1920, 1200, 60),
            60);

        state.ControlDisconnected(oldControl);
        state.SessionDisconnected(oldVideo);

        Assert.IsTrue(state.Snapshot.Tablet.ControlConnected);
        Assert.IsTrue(state.Snapshot.Tablet.VideoConnected);
        Assert.AreEqual(newControl, state.Snapshot.Tablet.ControlSessionGeneration);
        Assert.AreEqual(newVideo, state.Snapshot.Video.SessionGeneration);
        Assert.AreEqual(60, state.Snapshot.Video.NegotiatedFps);
    }

    [TestMethod]
    public void OptionalServiceHealthIsAdditiveAndOlderV1SnapshotsRemainReadable()
    {
        using var state = new PanelHostRuntimeState();
        state.ServiceChanged("telemetry", "degraded", "sensor_unavailable");
        state.ServiceChanged("media", "running");

        var response = PanelHostManagementProtocol.DeserializeResponse(
            PanelHostManagementProtocol.SerializeSnapshot(state.Snapshot));
        Assert.AreEqual("degraded", response?.Snapshot?.Services?.Telemetry.State);
        Assert.AreEqual(
            "sensor_unavailable",
            response?.Snapshot?.Services?.Telemetry.ErrorCode);
        Assert.AreEqual("running", response?.Snapshot?.Services?.Media.State);

        var currentJson = PanelHostManagementProtocol.SerializeSnapshot(state.Snapshot);
        using var document = System.Text.Json.JsonDocument.Parse(currentJson);
        var responseRoot = document.RootElement;
        var snapshot = responseRoot.GetProperty("snapshot");
        var legacySnapshot = string.Join(",", snapshot.EnumerateObject()
            .Where(property => property.Name != "services")
            .Select(property => $"\"{property.Name}\":{property.Value.GetRawText()}"));
        var legacyJson =
            $"{{\"type\":\"snapshot\",\"protocolVersion\":1,\"snapshot\":{{{legacySnapshot}}}}}";

        var legacy = PanelHostManagementProtocol.DeserializeResponse(legacyJson);
        Assert.IsNotNull(legacy?.Snapshot);
        Assert.IsNull(legacy.Snapshot.Services);
        Assert.AreEqual(PanelHostManagementProtocol.Version, legacy.ProtocolVersion);
    }

    [TestMethod]
    public async Task PipeSupportsSnapshotAndConcurrentSubscriptions()
    {
        ElevatedRunnerGuard.RequireNonElevatedProcess();
        var pipeName = $"PanelOS.Host.Tests.{Guid.NewGuid():N}";
        using var state = new PanelHostRuntimeState(processId: 777, version: "test");
        state.ControlConnected(1, ["desktop", "stream_deck"]);
        state.SessionConnected(
            new VideoAck(VideoCodec.H264, 1280, 800, 60),
            configuredFps: 60);
        state.StreamDeckChanged(StreamDeckSnapshot.Create(
            StreamDeckDefaults.CreateConfiguration()));
        var server = new PanelHostManagementServer(state, pipeName);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(shutdown.Token);

        await using var first = await SubscribeAsync(pipeName, shutdown.Token);
        await using var second = await SubscribeAsync(pipeName, shutdown.Token);
        var completeSnapshot = await ReadSnapshotAsync(first, shutdown.Token);
        Assert.AreEqual(777, completeSnapshot.Host.ProcessId);
        Assert.IsTrue(completeSnapshot.Tablet.ControlConnected);
        Assert.IsTrue(completeSnapshot.Tablet.VideoConnected);
        Assert.IsTrue(completeSnapshot.Video.Connected);
        Assert.IsNotNull(completeSnapshot.StreamDeck.PublishedRevision);
        Assert.AreEqual(777, (await ReadSnapshotAsync(second, shutdown.Token)).Host.ProcessId);

        state.DisplayChanged(new DesktopServiceSnapshot(
            DesktopServiceState.Running,
            Width: 1280,
            Height: 800,
            RefreshRate: 60));
        Assert.IsTrue((await ReadSnapshotAsync(first, shutdown.Token)).Display.Connected);
        Assert.IsTrue((await ReadSnapshotAsync(second, shutdown.Token)).Display.Connected);

        await using var oneShot = await ConnectAsync(pipeName, shutdown.Token);
        await oneShot.Writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeRequest(
                PanelHostManagementProtocol.GetSnapshot));
        Assert.AreEqual(1280, (await ReadSnapshotAsync(oneShot, shutdown.Token)).Display.Width);

        shutdown.Cancel();
        await serverTask;
    }

    [TestMethod]
    public async Task Medium_management_client_controls_desktop_through_host_lifecycle()
    {
        ElevatedRunnerGuard.RequireNonElevatedProcess();
        var pipeName = $"PanelOS.Host.Tests.{Guid.NewGuid():N}";
        using var state = new PanelHostRuntimeState();
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        var server = new PanelHostManagementServer(state, desktop, pipeName);
        using var client = new PanelHostManagementClient(pipeName);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(shutdown.Token);

        var started = await client.StartDesktopAsync(shutdown.Token);
        Assert.IsTrue(started.Succeeded);
        Assert.AreEqual("Running", started.State);
        Assert.AreEqual(1, desktop.StartRequestCount);

        var stopped = await client.StopDesktopAsync(shutdown.Token);
        Assert.IsTrue(stopped.Succeeded);
        Assert.AreEqual("Stopped", stopped.State);
        Assert.AreEqual(1, desktop.StopRequestCount);

        shutdown.Cancel();
        await serverTask;
    }

    [TestMethod]
    public async Task SlowSubscriberDoesNotBlockNewSnapshotClients()
    {
        ElevatedRunnerGuard.RequireNonElevatedProcess();
        var pipeName = $"PanelOS.Host.Tests.{Guid.NewGuid():N}";
        using var state = new PanelHostRuntimeState();
        var server = new PanelHostManagementServer(state, pipeName);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(shutdown.Token);

        await using var slow = await SubscribeAsync(pipeName, shutdown.Token);
        _ = await ReadSnapshotAsync(slow, shutdown.Token);
        for (var index = 0; index < 5_000; index++)
        {
            state.DisplayChanged(new DesktopServiceSnapshot(
                DesktopServiceState.Running,
                Width: 1000 + index,
                Height: 800,
                RefreshRate: 60));
        }

        await using var fast = await ConnectAsync(pipeName, shutdown.Token);
        await fast.Writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeRequest(
                PanelHostManagementProtocol.GetSnapshot));
        var latest = await ReadSnapshotAsync(fast, shutdown.Token);
        Assert.AreEqual(5999, latest.Display.Width);

        shutdown.Cancel();
        await serverTask;
    }

    private static PanelVideoRuntimeMetrics Metrics(double sendFps) => new(
        "H264", "encoder", 800, 600, 30, 30,
        30, 30, sendFps, 1, 2, 3, 0);

    private static async Task<PipeConnection> SubscribeAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var connection = await ConnectAsync(pipeName, cancellationToken);
        await connection.Writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeRequest(
                PanelHostManagementProtocol.Subscribe));
        return connection;
    }

    private static async Task<PipeConnection> ConnectAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(cancellationToken);
        return new PipeConnection(pipe);
    }

    private static async Task<PanelHostSnapshot> ReadSnapshotAsync(
        PipeConnection connection,
        CancellationToken cancellationToken)
    {
        var line = await connection.Reader.ReadLineAsync(cancellationToken);
        Assert.IsNotNull(line);
        var response = PanelHostManagementProtocol.DeserializeResponse(line);
        Assert.AreEqual(PanelHostManagementProtocol.Snapshot, response?.Type);
        Assert.IsNotNull(response?.Snapshot);
        return response.Snapshot;
    }

    private sealed class PipeConnection : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;

        internal PipeConnection(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            Reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            Writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                4096,
                true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };
        }

        internal StreamReader Reader { get; }

        internal StreamWriter Writer { get; }

        public async ValueTask DisposeAsync()
        {
            Reader.Dispose();
            await Writer.DisposeAsync();
            await _pipe.DisposeAsync();
        }
    }

    private sealed class ManagementTestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now =
            new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan amount) => _now += amount;
    }
}
