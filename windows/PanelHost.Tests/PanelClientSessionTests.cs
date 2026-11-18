using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Input;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Services;
using PanelHost.Transport;
using PanelOS.DisplayBroker.Protocol;
using PanelOS.Windows.LoopMedia;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelClientSessionTests
{
    [TestMethod]
    public async Task Phase1_capabilities_and_collectors_survive_a_loop_media_transfer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PanelHost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bytes = "GIF89a"u8.ToArray();
            var source = Path.Combine(directory, "sample.gif");
            await File.WriteAllBytesAsync(source, bytes);
            var store = new JsonLoopMediaStore(Path.Combine(directory, "media.json"), Path.Combine(directory, "media"));
            var media = await store.ImportAsync(source, CancellationToken.None);
            await using var loopMedia = new LoopMediaService(store);
            await loopMedia.StartAsync(CancellationToken.None);
            var notices = new LocalNoticeBroker();
            notices.SetEnabled(true);
            notices.Publish(new LocalNoticePayload("Loop test", "synthetic", "info", null, null, 5, 300));
            var permissions = new ClaudePermissionBroker();
            var computer = new FakeComputerStatsSource();
            string[] features = ["local_notices", "claude_permissions", ProtocolFeatures.ComputerStats,
                ProtocolFeatures.Processes, ProtocolFeatures.LoopMedia];
            await using var session = await SessionHarness.StartAsync(new FakeMediaService(), features,
                computerStatsSource: () => computer, notices: notices, permissions: permissions, loopMedia: loopMedia);
            foreach (var feature in features)
                Assert.IsTrue(session.HelloAcknowledgement.SupportsFeature(feature), feature);
            Assert.AreEqual(media.MediaId,
                (await session.ReadUntilAsync<LoopMediaStateMessage>(MessageTypes.LoopMediaState)).MediaId);
            await session.SendAsync(new CommandMessage
            {
                Command = CommandNames.LoopMediaRequest, MediaId = media.MediaId, Offset = 0,
            });
            var chunk = await session.ReadUntilAsync<LoopMediaChunkMessage>(MessageTypes.LoopMediaChunk);
            CollectionAssert.AreEqual(bytes, Convert.FromBase64String(chunk.Data!));
            Assert.IsTrue(chunk.Last);
            await session.ReadUntilAsync<NetworkRatesMessage>("network_rates");
            await session.ReadUntilAsync<ProcessesMessage>("processes");
            Assert.IsTrue((await session.ReadUntilAsync<LocalNoticesMessage>("local_notices")).Enabled);
            Assert.IsTrue(permissions.Connected);
            Assert.IsTrue(computer.ProcessReads > 0);
            await session.SendAsync(new CommandMessage { Command = "processes.options", Sort = "memory", Grouped = true });
            Assert.IsTrue((await session.ReadUntilAsync<ProcessesMessage>("processes", x => x.Grouped)).Grouped);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Phase1_notices_permissions_and_disabled_termination_travel_only_after_negotiation()
    {
        var notices = new LocalNoticeBroker(); notices.SetEnabled(true);
        notices.Publish(new LocalNoticePayload("Permitir acesso?", "É só um aviso.", "info", null, null, 5, 300));
        var permissions = new ClaudePermissionBroker();
        var targets = new NeverOpenTargets();
        var termination = new ProcessTerminationService(targets);
        await using var session = await SessionHarness.StartAsync(new FakeMediaService(),
            ["local_notices", "claude_permissions", ProtocolFeatures.Processes], computerStatsSource: () => new FakeComputerStatsSource(),
            notices: notices, permissions: permissions, termination: termination);
        var notice = await session.ReadUntilAsync<LocalNoticesMessage>("local_notices");
        Assert.IsTrue(notice.Enabled);
        Assert.AreEqual("Permitir acesso?", notice.Notices.Single().Title);
        Assert.AreEqual(0, permissions.Snapshot().Requests.Count, "A notice never becomes a permission request.");
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!permissions.Connected) await Task.Delay(10, ready.Token);
        var decision = permissions.RequestAsync(new("project", "Bash", "echo synthetic", "", 0), 30, ready.Token);
        var pending = await session.ReadUntilAsync<ClaudePermissionsMessage>("claude_permissions", x => x.Requests.Count > 0);
        var id = pending.Requests.Single().Id;
        await session.SendAsync(new CommandMessage { Command = "claude.shown", RequestId = id });
        await session.SendAsync(new PingMessage()); await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
        await Task.Delay(720);
        await session.SendAsync(new CommandMessage { Command = "claude.respond", RequestId = id, Decision = "deny" });
        Assert.AreEqual("deny", await decision.WaitAsync(TimeSpan.FromSeconds(2)));
        await session.SendAsync(new CommandMessage { Command = "processes.terminate", RequestId = Guid.NewGuid().ToString("N"), Pid = 101, StartTimeUtcTicks = 1000 });
        Assert.AreEqual("disabled", (await session.ReadUntilAsync<ProcessTerminationResult>("processes.terminate_result")).Result);
    }

    private sealed class NeverOpenTargets : IProcessTerminationTargets
    { public IProcessTerminationTarget? Open(int pid) => throw new AssertFailedException("Disabled gate opened a process."); }

    [TestMethod]
    public async Task Phase1_terminating_a_process_listed_as_protected_answers_protected_not_gone()
    {
        var termination = new ProcessTerminationService(new NeverOpenTargets(), reviewed: true);
        Assert.IsTrue(termination.SetEnabled(true));
        await using var session = await SessionHarness.StartAsync(new FakeMediaService(), [ProtocolFeatures.Processes],
            computerStatsSource: () => new ProtectedListingSource(), termination: termination);
        var list = await session.ReadUntilAsync<ProcessesMessage>("processes");
        Assert.IsTrue(list.Items.Any(x => x.Pid == 5000 && x.Protected));
        await session.SendAsync(new CommandMessage { Command = "processes.terminate", RequestId = Guid.NewGuid().ToString("N"), Pid = 5000, StartTimeUtcTicks = 7 });
        Assert.AreEqual("protected", (await session.ReadUntilAsync<ProcessTerminationResult>("processes.terminate_result")).Result);
        // Same PID with a different start time, and a PID never listed, are really not there: "gone".
        await session.SendAsync(new CommandMessage { Command = "processes.terminate", RequestId = Guid.NewGuid().ToString("N"), Pid = 5000, StartTimeUtcTicks = 8 });
        Assert.AreEqual("gone", (await session.ReadUntilAsync<ProcessTerminationResult>("processes.terminate_result")).Result);
        await session.SendAsync(new CommandMessage { Command = "processes.terminate", RequestId = Guid.NewGuid().ToString("N"), Pid = 6000, StartTimeUtcTicks = 7 });
        Assert.AreEqual("gone", (await session.ReadUntilAsync<ProcessTerminationResult>("processes.terminate_result")).Result);
    }

    private sealed class ProtectedListingSource : IComputerStatsSource
    {
        public IReadOnlyList<NetworkCounter> ReadNetwork() => [new("id", "Ethernet", 100, 20)];
        public IReadOnlyList<ProcessCounter> ReadProcesses() =>
            [new("editor", 101, 1000, 1, 100, "current_user", false), new("OpenBot", 5000, 7, 1, 200, "current_user", true)];
    }

    [TestMethod]
    public async Task Phase1_old_client_never_constructs_or_samples_new_collectors()
    {
        await using var session = await SessionHarness.StartAsync(new FakeMediaService(),
            computerStatsSource: () => throw new AssertFailedException("Old client sampled phase1 data"));
        await session.ReadUntilAsync<TelemetryMessage>(MessageTypes.Telemetry);
        await session.SendAsync(new CommandMessage { Command = "processes.options", Sort = "cpu", Grouped = false });
        var error = await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);
        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
        await session.SendAsync(new PingMessage());
        var types = await session.ReadTypesThroughAsync(MessageTypes.Pong);
        CollectionAssert.DoesNotContain(types, "processes");
        CollectionAssert.DoesNotContain(types, "network_rates");
    }

    [TestMethod]
    public async Task Phase1_negotiates_samples_and_validates_read_only_options()
    {
        var source = new FakeComputerStatsSource();
        await using var session = await SessionHarness.StartAsync(new FakeMediaService(),
            [ProtocolFeatures.ComputerStats, ProtocolFeatures.Processes], computerStatsSource: () => source);
        Assert.IsTrue(session.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.ComputerStats));
        Assert.IsTrue(session.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.Processes));
        await session.ReadUntilAsync<NetworkRatesMessage>("network_rates");
        var initial = await session.ReadUntilAsync<ProcessesMessage>("processes");
        Assert.IsFalse(initial.TerminateEnabled);
        Assert.AreEqual("cpu", initial.Sort);
        await session.SendAsync(new CommandMessage { Command = "processes.options", Sort = "shell", Grouped = false });
        Assert.AreEqual(ErrorCodes.InvalidMessage, (await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error)).Code);
        await session.SendAsync(new CommandMessage { Command = "processes.options", Sort = "memory", Grouped = true });
        var next = await session.ReadUntilAsync<ProcessesMessage>("processes", x => x.Grouped);
        Assert.AreEqual("memory", next.Sort);
        Assert.IsNull(next.Items.Single().Pid);
        Assert.AreEqual(2, next.Items.Single().Count);
        Assert.IsTrue(source.ProcessReads >= 2);
    }

    private sealed class FakeComputerStatsSource : IComputerStatsSource
    {
        internal int ProcessReads;
        public IReadOnlyList<NetworkCounter> ReadNetwork() => [new("id", "Ethernet", 100, 20)];
        public IReadOnlyList<ProcessCounter> ReadProcesses()
        {
            Interlocked.Increment(ref ProcessReads);
            return [new("editor", 1, 1, 1, 100, "current_user", false), new("editor", 2, 2, 1, 200, "current_user", false)];
        }
    }

    [TestMethod]
    public async Task Pan4_dead_broker_stop_publishes_stopped_on_wire_and_keeps_session_alive()
    {
        var client = new FakePanelDisplayDriverClient();
        client.GetStatusHandler = _ =>
        {
            client.SimulateBrokerDisconnect(PanelDisplayBrokerDisconnectReason.BrokerExited);
            throw new IOException("Pipe is broken.");
        };
        await using var desktop = new PanelDisplayDriverDesktopService(client);
        await using var session = await SessionHarness.StartAsync(new FakeMediaService(),
            [ProtocolFeatures.Desktop], desktopService: desktop);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
        await session.SendAsync(new CommandMessage { Command = CommandNames.DesktopStart });
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState,
            state => state.State == DesktopStates.Error && state.ErrorCode == DesktopErrorCodes.DriverIoError);

        await session.SendAsync(new CommandMessage { Command = CommandNames.DesktopStop });
        var stopped = await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState,
            state => state.State == DesktopStates.Stopped);

        Assert.IsNull(stopped.ErrorCode);
        Assert.AreEqual(1, client.GetStatusCount);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }
    [TestMethod]
    public async Task No_session_is_sent_as_unavailable_with_absent_artwork()
    {
        var mediaService = new FakeMediaService();
        await using var session = await SessionHarness.StartAsync(
            mediaService,
            [ProtocolFeatures.MediaArtwork]);

        var state = await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);
        var artwork = await session.ReadUntilAsync<MediaArtworkMessage>(MessageTypes.MediaArtwork);

        Assert.IsFalse(state.Available);
        Assert.IsNull(state.MediaId);
        Assert.IsFalse(artwork.Available);
        Assert.IsNull(artwork.MimeType);
        Assert.IsNull(artwork.DataBase64);
    }

    [TestMethod]
    public async Task Current_media_is_sent_after_handshake()
    {
        var expected = CreateMediaState();
        var mediaService = new FakeMediaService(expected);
        await using var session = await SessionHarness.StartAsync(mediaService);

        var actual = await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public async Task Playback_update_is_forwarded_to_panel_home()
    {
        var initial = CreateMediaState() with { IsPlaying = false, PositionMs = 12_000 };
        var mediaService = new FakeMediaService(initial);
        await using var session = await SessionHarness.StartAsync(mediaService);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        mediaService.Publish(initial with { IsPlaying = true, PositionMs = 13_000 });

        var update = await session.ReadUntilAsync<MediaStateMessage>(
            MessageTypes.MediaState,
            state => state.IsPlaying);
        Assert.AreEqual(13_000, update.PositionMs);
    }

    [TestMethod]
    public async Task Track_change_forwards_new_state_and_artwork()
    {
        var first = CreateMediaState();
        var firstArtwork = CreateArtwork(first.MediaId!, "AQID");
        var mediaService = new FakeMediaService(first, firstArtwork);
        await using var session = await SessionHarness.StartAsync(
            mediaService,
            [ProtocolFeatures.MediaArtwork]);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);
        await session.ReadUntilAsync<MediaArtworkMessage>(MessageTypes.MediaArtwork);

        var next = CreateMediaState() with
        {
            MediaId = "Chrome.exe|second-track",
            SourceApp = "Chrome.exe",
            Title = "Second Track",
            Artist = "Second Artist",
            Album = null,
            PositionMs = 0,
        };
        var nextArtwork = CreateArtwork(next.MediaId!, "BAUG");
        mediaService.Publish(next, nextArtwork, artworkChanged: true);

        var stateUpdate = await session.ReadUntilAsync<MediaStateMessage>(
            MessageTypes.MediaState,
            state => state.MediaId == next.MediaId);
        var artworkUpdate = await session.ReadUntilAsync<MediaArtworkMessage>(
            MessageTypes.MediaArtwork,
            artwork => artwork.MediaId == next.MediaId);

        Assert.AreEqual(next, stateUpdate);
        Assert.AreEqual(nextArtwork, artworkUpdate);
    }

    [DataTestMethod]
    [DataRow(CommandNames.MediaPlayPause, MediaCommand.PlayPause)]
    [DataRow(CommandNames.MediaNext, MediaCommand.Next)]
    [DataRow(CommandNames.MediaPrevious, MediaCommand.Previous)]
    public async Task Media_commands_are_dispatched_to_media_service(
        string wireCommand,
        MediaCommand expectedCommand)
    {
        var mediaService = new FakeMediaService(CreateMediaState());
        await using var session = await SessionHarness.StartAsync(mediaService);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        await session.SendAsync(new CommandMessage { Command = wireCommand });
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        Assert.AreEqual(1, mediaService.CommandResults.Count);
        Assert.AreEqual(expectedCommand, mediaService.CommandResults[0].Command);
        Assert.IsTrue(mediaService.CommandResults[0].Succeeded);
    }

    [TestMethod]
    public async Task Command_without_session_is_rejected_without_closing_connection()
    {
        var mediaService = new FakeMediaService();
        await using var session = await SessionHarness.StartAsync(mediaService);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.MediaPlayPause });
        var state = await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        Assert.IsFalse(state.Available);
        Assert.AreEqual((MediaCommand.PlayPause, false), mediaService.CommandResults.Single());

        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Unsupported_capability_does_not_close_connection()
    {
        var state = CreateMediaState() with { CanNext = false };
        var mediaService = new FakeMediaService(state);
        await using var session = await SessionHarness.StartAsync(mediaService);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        await session.SendAsync(new CommandMessage { Command = CommandNames.MediaNext });
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        Assert.AreEqual((MediaCommand.Next, false), mediaService.CommandResults.Single());

        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Artwork_is_not_sent_without_feature_negotiation()
    {
        var state = CreateMediaState();
        var mediaService = new FakeMediaService(
            state,
            CreateArtwork(state.MediaId!, "AQID"));
        await using var session = await SessionHarness.StartAsync(mediaService);
        await session.ReadUntilAsync<MediaStateMessage>(MessageTypes.MediaState);

        await session.SendAsync(new PingMessage());
        var seenTypes = await session.ReadTypesThroughAsync(MessageTypes.Pong);

        CollectionAssert.DoesNotContain(seenTypes, MessageTypes.MediaArtwork);
    }

    [TestMethod]
    public async Task Hello_ack_advertises_volume_but_state_requires_client_feature()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 72,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            volumeService: volumeService);

        Assert.IsTrue(
            session.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.Volume));

        await session.SendAsync(new PingMessage());
        var seenTypes = await session.ReadTypesThroughAsync(MessageTypes.Pong);

        CollectionAssert.DoesNotContain(seenTypes, MessageTypes.VolumeState);
    }

    [TestMethod]
    public async Task Current_volume_is_sent_after_negotiated_handshake()
    {
        var expected = new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 72,
            IsMuted = false,
        };
        var volumeService = new FakeVolumeService(expected);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);

        var actual = await session.ReadUntilAsync<VolumeStateMessage>(
            MessageTypes.VolumeState);

        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public async Task Unavailable_volume_is_sent_without_synthetic_values()
    {
        var volumeService = new FakeVolumeService();
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);

        var state = await session.ReadUntilAsync<VolumeStateMessage>(
            MessageTypes.VolumeState);

        Assert.IsFalse(state.Available);
        Assert.AreEqual(0, state.VolumePercent);
        Assert.IsFalse(state.IsMuted);
    }

    [TestMethod]
    public async Task External_volume_update_is_forwarded_to_panel_home()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 50,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);
        await session.ReadUntilAsync<VolumeStateMessage>(MessageTypes.VolumeState);

        volumeService.Publish(volumeService.CurrentState with
        {
            VolumePercent = 33,
            IsMuted = true,
        });
        var update = await session.ReadUntilAsync<VolumeStateMessage>(
            MessageTypes.VolumeState,
            state => state.VolumePercent == 33);

        Assert.IsTrue(update.IsMuted);
    }

    [TestMethod]
    public async Task Volume_set_command_dispatches_integer_value()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 50,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);
        await session.ReadUntilAsync<VolumeStateMessage>(MessageTypes.VolumeState);

        await session.SendAsync(new CommandMessage
        {
            Command = CommandNames.VolumeSet,
            Value = 72,
        });
        var update = await session.ReadUntilAsync<VolumeStateMessage>(
            MessageTypes.VolumeState,
            state => state.VolumePercent == 72);

        CollectionAssert.AreEqual(new[] { 72 }, volumeService.SetRequests.ToArray());
        Assert.AreEqual(72, update.VolumePercent);
    }

    [TestMethod]
    public async Task Volume_toggle_mute_command_is_dispatched()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 40,
            IsMuted = false,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);
        await session.ReadUntilAsync<VolumeStateMessage>(MessageTypes.VolumeState);

        await session.SendAsync(new CommandMessage
        {
            Command = CommandNames.VolumeToggleMute,
        });
        var update = await session.ReadUntilAsync<VolumeStateMessage>(
            MessageTypes.VolumeState,
            state => state.IsMuted);

        Assert.AreEqual(1, volumeService.ToggleMuteRequestCount);
        Assert.IsTrue(update.IsMuted);
    }

    [TestMethod]
    public async Task Volume_set_without_value_is_invalid_and_connection_stays_open()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 50,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Volume],
            volumeService: volumeService);
        await session.ReadUntilAsync<VolumeStateMessage>(MessageTypes.VolumeState);

        await session.SendAsync(new CommandMessage
        {
            Command = CommandNames.VolumeSet,
        });
        var error = await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);

        Assert.AreEqual(ErrorCodes.InvalidMessage, error.Code);
        Assert.AreEqual(0, volumeService.SetRequests.Count);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Volume_command_without_feature_is_rejected()
    {
        var volumeService = new FakeVolumeService(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = 50,
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            volumeService: volumeService);

        await session.SendAsync(new CommandMessage
        {
            Command = CommandNames.VolumeSet,
            Value = 72,
        });
        var error = await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);

        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
        Assert.AreEqual(0, volumeService.SetRequests.Count);
    }

    [TestMethod]
    public async Task Hello_ack_advertises_desktop_but_state_requires_client_feature()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            desktopService: desktopService);

        Assert.IsTrue(
            session.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.Desktop));

        await session.SendAsync(new PingMessage());
        var seenTypes = await session.ReadTypesThroughAsync(MessageTypes.Pong);

        CollectionAssert.DoesNotContain(seenTypes, MessageTypes.DesktopState);
    }

    [TestMethod]
    public async Task Desktop_start_and_stop_publish_latest_state_from_one_writer()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        var initial = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var startStates = await session.ReadDesktopStatesThroughAsync(
            DesktopStates.Running);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStop });
        var stopStates = await session.ReadDesktopStatesThroughAsync(
            DesktopStates.Stopped);

        Assert.AreEqual(DesktopStates.Stopped, initial.State);
        Assert.AreEqual(DesktopStates.Running, startStates[^1]);
        Assert.IsTrue(startStates.All(state =>
            state is DesktopStates.Starting or DesktopStates.Running));
        Assert.AreEqual(DesktopStates.Stopped, stopStates[^1]);
        Assert.IsTrue(stopStates.All(state =>
            state is DesktopStates.Stopping or DesktopStates.Stopped));
        Assert.AreEqual(new DesktopRequest(), desktopService.LastStartRequest);
        Assert.AreEqual(1, desktopService.StartRequestCount);
        Assert.AreEqual(1, desktopService.StopRequestCount);
    }

    [TestMethod]
    public async Task Negotiated_touch_receives_running_generation_and_disconnect_cleanup()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        var touchService = new FakeSessionTouchInjectionService();
        var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop, ProtocolFeatures.Touch],
            desktopService: desktopService,
            touchService: touchService);
        await using (session)
        {
            Assert.IsTrue(
                session.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.Touch));
            await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
            await session.SendAsync(
                new CommandMessage { Command = CommandNames.DesktopStart });
            var running = await session.ReadUntilAsync<DesktopStateMessage>(
                MessageTypes.DesktopState,
                state => state.State == DesktopStates.Running);
            Assert.AreEqual(touchService.ActiveGeneration, running.Generation);

            await session.SendAsync(new CommandMessage
            {
                Command = CommandNames.TouchDown,
                PointerId = 0,
                X = 0.42,
                Y = 0.67,
                Generation = running.Generation,
            });
            await session.SendAsync(new PingMessage());
            await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);

            var command = touchService.Commands.Single();
            Assert.AreEqual(DesktopTouchAction.Down, command.Action);
            Assert.AreEqual(0.42, command.X);
            Assert.AreEqual(0.67, command.Y);

            await session.CloseClientSocketAndWaitForEofAsync();
            Assert.IsTrue(touchService.CancelRequests > 0);
            Assert.AreEqual(1, touchService.DeactivateRequests);
        }
    }

    [TestMethod]
    public async Task Touch_command_without_bilateral_feature_is_rejected()
    {
        var touchService = new FakeSessionTouchInjectionService();
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            touchService: touchService);

        await session.SendAsync(new CommandMessage
        {
            Command = CommandNames.TouchDown,
            PointerId = 0,
            X = 0.5,
            Y = 0.5,
            Generation = 1,
        });
        var error = await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);

        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
        Assert.AreEqual(0, touchService.Commands.Count);
    }

    [TestMethod]
    public async Task Desktop_stop_and_ping_are_read_while_start_is_pending()
    {
        var releaseTransition = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var desktopService = new FakeDesktopService(
            cancellationToken => releaseTransition.Task.WaitAsync(cancellationToken));
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var starting = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Starting);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStop });
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);

        Assert.AreEqual(DesktopStates.Starting, starting.State);
        Assert.AreEqual(DesktopServiceState.Starting, desktopService.State);
        Assert.AreEqual(1, desktopService.StartRequestCount);
        Assert.AreEqual(0, desktopService.StopRequestCount);
        Assert.IsFalse(releaseTransition.Task.IsCompleted);

        releaseTransition.SetResult(true);
        var terminalStates = await session.ReadDesktopStatesThroughAsync(
            DesktopStates.Stopped);

        Assert.AreEqual(DesktopStates.Stopped, terminalStates[^1]);
        Assert.IsTrue(terminalStates.All(state => state is
            DesktopStates.Running or DesktopStates.Stopping or DesktopStates.Stopped));
        Assert.AreEqual(DesktopServiceState.Stopped, desktopService.State);
        Assert.AreEqual(1, desktopService.StopRequestCount);
    }

    [TestMethod]
    public async Task Eof_during_desktop_start_cancels_operation_before_cleanup()
    {
        var releaseTransition = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var desktopService = new FakeDesktopService(
            cancellationToken => releaseTransition.Task.WaitAsync(cancellationToken));
        var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await using (session)
        {
            await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
            await session.SendAsync(
                new CommandMessage { Command = CommandNames.DesktopStart });
            await session.ReadUntilAsync<DesktopStateMessage>(
                MessageTypes.DesktopState,
                state => state.State == DesktopStates.Starting);

            await session.CloseClientSocketAndWaitForEofAsync();

            Assert.IsFalse(session.ServerCancellationRequested);
            Assert.IsFalse(releaseTransition.Task.IsCompleted);
            Assert.AreEqual(DesktopServiceState.Stopped, desktopService.State);
            Assert.AreEqual(1, desktopService.StartRequestCount);
            Assert.AreEqual(0, desktopService.StopRequestCount);
        }
    }

    [TestMethod]
    public async Task Desktop_stop_failure_is_sent_on_wire_and_session_stays_alive()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Running);
        desktopService.FailNextStop("test_stop_failed");

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStop });
        var error = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Error);

        Assert.AreEqual("test_stop_failed", error.ErrorCode);
        Assert.AreEqual(DesktopServiceState.Error, desktopService.State);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Missing_driver_start_error_can_be_stopped_without_an_error_loop()
    {
        var client = new FakePanelDisplayDriverClient
        {
            GetStatusHandler = _ => throw FakePanelDisplayDriverClient.CreateFailure(
                PanelDisplayDriverFailureKind.NotFound,
                "locate interface",
                win32ErrorCode: 1168),
        };
        await using var desktopService = new PanelDisplayDriverDesktopService(client);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var error = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Error);
        Assert.AreEqual(DesktopErrorCodes.DriverNotFound, error.ErrorCode);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStop });
        var stopStates = await session.ReadDesktopStatesThroughAsync(DesktopStates.Stopped);

        Assert.AreEqual(DesktopStates.Stopped, stopStates[^1]);
        Assert.IsTrue(stopStates.All(state =>
            state is DesktopStates.Stopping or DesktopStates.Stopped));
        Assert.IsNull(desktopService.ErrorCode);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Desktop_failure_sends_error_state_and_keeps_connection_alive()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        desktopService.FailNextStart("test_start_failed");
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var error = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Error);

        Assert.AreEqual("test_start_failed", error.ErrorCode);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Unexpected_desktop_exception_becomes_internal_error_state()
    {
        await using var desktopService = new ThrowingDesktopService();
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var error = await session.ReadUntilAsync<DesktopStateMessage>(
            MessageTypes.DesktopState,
            state => state.State == DesktopStates.Error);

        Assert.AreEqual(DesktopErrorCodes.InternalError, error.ErrorCode);
        await session.SendAsync(new PingMessage());
        await session.ReadUntilAsync<PongMessage>(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task Desktop_command_without_feature_is_rejected_without_starting_service()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            desktopService: desktopService);

        await session.SendAsync(
            new CommandMessage { Command = CommandNames.DesktopStart });
        var error = await session.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);

        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
        Assert.AreEqual(0, desktopService.StartRequestCount);
    }

    [TestMethod]
    public async Task Disconnect_cleans_up_running_desktop()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await using (session)
        {
            await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
            await session.SendAsync(
                new CommandMessage { Command = CommandNames.DesktopStart });
            await session.ReadUntilAsync<DesktopStateMessage>(
                MessageTypes.DesktopState,
                state => state.State == DesktopStates.Running);

            await session.CloseClientSocketAndWaitForEofAsync();

            Assert.IsFalse(session.ServerCancellationRequested);
            Assert.AreEqual(DesktopServiceState.Stopped, desktopService.State);
            Assert.AreEqual(1, desktopService.StopRequestCount);
        }
    }

    [TestMethod]
    public async Task Disconnect_cleanup_is_not_conditioned_on_desktop_feature()
    {
        await using var desktopService = new FakeDesktopService(TimeSpan.Zero);
        await desktopService.StartAsync(new DesktopRequest(), CancellationToken.None);
        var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            desktopService: desktopService);

        await session.DisposeAsync();

        Assert.AreEqual(DesktopServiceState.Stopped, desktopService.State);
        Assert.AreEqual(1, desktopService.StopRequestCount);
    }

    [TestMethod]
    public async Task Disconnect_with_driver_desktop_service_issues_disconnect_ioctl()
    {
        var driverClient = new FakePanelDisplayDriverClient();
        await using var desktopService = new PanelDisplayDriverDesktopService(
            driverClient,
            TextWriter.Null);
        var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            [ProtocolFeatures.Desktop],
            desktopService: desktopService);
        await using (session)
        {
            await session.ReadUntilAsync<DesktopStateMessage>(MessageTypes.DesktopState);
            await session.SendAsync(
                new CommandMessage { Command = CommandNames.DesktopStart });
            await session.ReadUntilAsync<DesktopStateMessage>(
                MessageTypes.DesktopState,
                state => state.State == DesktopStates.Running);

            await session.CloseClientSocketAndWaitForEofAsync();

            Assert.AreEqual(1, driverClient.ConnectCount);
            Assert.AreEqual(1, driverClient.DisconnectCount);
            Assert.AreEqual(
                PanelDisplayMonitorState.Disconnected,
                driverClient.MonitorState);
            Assert.AreEqual(DesktopServiceState.Stopped, desktopService.State);
        }
    }

    [TestMethod]
    public async Task Telemetry_snapshot_from_fake_service_is_sent_to_panel_home()
    {
        var telemetryService = new FakeTelemetryService(new TelemetryMessage
        {
            Cpu = new CpuTelemetry
            {
                Name = "CPU Test",
                TemperatureC = 51.5,
                UsagePercent = 17.0,
                ClockGhz = 4.2,
                PowerW = 61.0,
            },
            Gpu = new GpuTelemetry
            {
                Name = "GPU Test",
                UsagePercent = 29.0,
            },
            Memory = new MemoryTelemetry
            {
                UsedGb = 12.0,
                TotalGb = 32.0,
                UsagePercent = 37.5,
            },
        });
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            telemetryService: telemetryService);

        var snapshot = await session.ReadUntilAsync<TelemetryMessage>(
            MessageTypes.Telemetry);

        Assert.AreEqual("CPU Test", snapshot.Cpu.Name);
        Assert.AreEqual(51.5, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(4.2, snapshot.Cpu.ClockGhz);
        Assert.AreEqual("GPU Test", snapshot.Gpu.Name);
        Assert.AreEqual(29.0, snapshot.Gpu.UsagePercent);
        Assert.AreEqual(12.0, snapshot.Memory.UsedGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
    }

    [TestMethod]
    public async Task No_hardware_snapshot_is_sent_without_synthetic_values()
    {
        var telemetryService = new FakeTelemetryService();
        await using var session = await SessionHarness.StartAsync(
            new FakeMediaService(),
            telemetryService: telemetryService);

        var snapshot = await session.ReadUntilAsync<TelemetryMessage>(
            MessageTypes.Telemetry);

        Assert.IsNull(snapshot.Cpu.Name);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Gpu.Name);
        Assert.IsNull(snapshot.Gpu.UsagePercent);
        Assert.IsNull(snapshot.Memory.UsedGb);
        Assert.IsNull(snapshot.Memory.TotalGb);
        Assert.AreEqual(0, snapshot.Storage.Count);
    }

    private static MediaStateMessage CreateMediaState() => new()
    {
        Available = true,
        MediaId = "Spotify.exe|midnight-city",
        SourceApp = "Spotify.exe",
        Title = "Midnight City",
        Artist = "M83",
        Album = "Hurry Up, We're Dreaming",
        IsPlaying = true,
        PositionMs = 94_000,
        DurationMs = 243_000,
        CanPlayPause = true,
        CanPrevious = true,
        CanNext = true,
    };

    private static MediaArtworkMessage CreateArtwork(string mediaId, string dataBase64) => new()
    {
        MediaId = mediaId,
        Available = true,
        MimeType = "image/jpeg",
        DataBase64 = dataBase64,
    };

    private sealed class ThrowingDesktopService : IDesktopService
    {
        public DesktopServiceState State => DesktopServiceState.Stopped;

        public DesktopServiceSnapshot Snapshot { get; } =
            new(DesktopServiceState.Stopped);

        public string? ErrorCode => null;

        public event EventHandler<DesktopStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<DesktopStartResult> StartAsync(
            DesktopRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<DesktopStartResult>(
                new InvalidOperationException("Test failure with private details."));

        public Task<DesktopStopResult> StopAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopStopResult(
                Succeeded: true,
                AlreadyStopped: true,
                State: DesktopServiceState.Stopped));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        private readonly TcpClient _client;
        private readonly TcpClient _serverClient;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _sessionTask;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly IDesktopService? _ownedDesktopService;
        private bool _clientSocketClosed;

        private SessionHarness(
            TcpClient client,
            TcpClient serverClient,
            CancellationTokenSource cancellation,
            Task sessionTask,
            IDesktopService? ownedDesktopService)
        {
            _client = client;
            _serverClient = serverClient;
            _cancellation = cancellation;
            _sessionTask = sessionTask;
            _ownedDesktopService = ownedDesktopService;
            _reader = new StreamReader(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            _writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
        }

        public static async Task<SessionHarness> StartAsync(
            FakeMediaService mediaService,
            string[]? features = null,
            FakeTelemetryService? telemetryService = null,
            FakeVolumeService? volumeService = null,
            IDesktopService? desktopService = null,
            ITouchInjectionService? touchService = null,
            Func<IComputerStatsSource>? computerStatsSource = null,
            LocalNoticeBroker? notices = null, ClaudePermissionBroker? permissions = null, ProcessTerminationService? termination = null,
            LoopMediaService? loopMedia = null)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            var serverClient = await listener.AcceptTcpClientAsync();
            await connectTask;

            var cancellation = new CancellationTokenSource();
            var ownedDesktopService = desktopService is null
                ? new FakeDesktopService(TimeSpan.Zero)
                : null;
            var panelSession = new PanelClientSession(
                telemetryService ?? new FakeTelemetryService(),
                mediaService,
                volumeService ?? new FakeVolumeService(),
                desktopService ?? ownedDesktopService!,
                touchInjectionService: touchService,
                computerStatsSource: computerStatsSource, notices: notices, permissions: permissions, termination: termination,
                loopMediaService: loopMedia);
            var sessionTask = panelSession.HandleAsync(
                new StreamPanelConnection(
                    PanelChannelId.Control,
                    serverClient.GetStream(),
                    "test-control",
                    ownsStream: false),
                cancellation.Token);
            var harness = new SessionHarness(
                client,
                serverClient,
                cancellation,
                sessionTask,
                ownedDesktopService);

            await harness.SendAsync(new HelloMessage
            {
                DeviceName = "PanelOS Test",
                Features = features,
            });
            harness.HelloAcknowledgement =
                await harness.ReadUntilAsync<HelloAckMessage>(MessageTypes.HelloAck);
            return harness;
        }

        public HelloAckMessage HelloAcknowledgement { get; private set; } = new();

        public async Task SendAsync<T>(T message) where T : class
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            var line = PanelJson.SerializeLine(message);
            await _writer.WriteAsync(line.AsMemory(), timeout.Token);
            await _writer.FlushAsync(timeout.Token);
        }

        public async Task<T> ReadUntilAsync<T>(
            string messageType,
            Func<T, bool>? predicate = null)
            where T : class
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "PanelHost closed the test connection unexpectedly.");
                var envelope = PanelJson.Deserialize<MessageEnvelope>(line);
                if (envelope.Type != messageType)
                {
                    continue;
                }

                var message = PanelJson.Deserialize<T>(line);
                if (predicate is null || predicate(message))
                {
                    return message;
                }
            }
        }

        public async Task<string[]> ReadTypesThroughAsync(string finalMessageType)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            var types = new List<string>();
            do
            {
                var line = await _reader.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "PanelHost closed the test connection unexpectedly.");
                var type = PanelJson.Deserialize<MessageEnvelope>(line).Type;
                types.Add(type);
            }
            while (types[^1] != finalMessageType);

            return [.. types];
        }

        public async Task<string[]> ReadDesktopStatesThroughAsync(string finalState)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            var states = new List<string>();
            do
            {
                var line = await _reader.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "PanelHost closed the test connection unexpectedly.");
                var envelope = PanelJson.Deserialize<MessageEnvelope>(line);
                if (envelope.Type != MessageTypes.DesktopState)
                {
                    continue;
                }

                states.Add(PanelJson.Deserialize<DesktopStateMessage>(line).State);
            }
            while (states.Count == 0 || states[^1] != finalState);

            return [.. states];
        }

        public bool ServerCancellationRequested => _cancellation.IsCancellationRequested;

        public async Task CloseClientSocketAndWaitForEofAsync()
        {
            CloseClientSocket();
            await _sessionTask.WaitAsync(TestTimeout);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            CloseClientSocket();

            try
            {
                await _sessionTask.WaitAsync(TestTimeout);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or SocketException)
            {
            }
            finally
            {
                _reader.Dispose();
                try
                {
                    await _writer.DisposeAsync();
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException or IOException)
                {
                }
                _serverClient.Dispose();
                _cancellation.Dispose();
                if (_ownedDesktopService is not null)
                {
                    await _ownedDesktopService.DisposeAsync();
                }
            }
        }

        private void CloseClientSocket()
        {
            if (_clientSocketClosed)
            {
                return;
            }

            _clientSocketClosed = true;
            _client.Dispose();
        }
    }
}

internal sealed class FakeSessionTouchInjectionService : ITouchInjectionService
{
    private bool _running;
    private long _generation;

    public bool IsAvailable => true;

    public long ActiveGeneration => _running ? _generation : 0;

    public TouchInjectionMetrics Metrics => new(Commands.Count, Commands.Count, 0);

    internal List<NormalizedTouchCommand> Commands { get; } = [];

    internal int CancelRequests { get; private set; }

    internal int DeactivateRequests { get; private set; }

    public void SetDesktopState(DesktopServiceSnapshot snapshot)
    {
        if (snapshot.State == DesktopServiceState.Running)
        {
            if (!_running)
            {
                _running = true;
                _generation++;
            }
        }
        else
        {
            _running = false;
        }
    }

    public TouchInjectionResult Process(NormalizedTouchCommand command)
    {
        Commands.Add(command);
        return TouchInjectionResult.Success;
    }

    public void CancelActiveContact(string reason)
    {
        CancelRequests++;
    }

    public void DeactivateDesktop(string reason)
    {
        DeactivateRequests++;
        _running = false;
    }

    public void Dispose()
    {
    }
}
