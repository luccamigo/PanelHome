using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;
using PanelHost.Protocol;
using PanelHost.Security.Wifi;
using PanelHost.Server;
using PanelHost.Transport;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiBenchTests
{
    [TestMethod]
    public async Task Bench_is_disabled_by_default_and_every_gate_precedes_socket_construction()
    {
        var command = new PanelHostCommandLine((_, _) => Task.FromResult(0));
        Assert.IsFalse(command.Bind(command.Parse([])).WifiBench.Enabled);
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => WifiBenchListener.RunAsync(new(), TextWriter.Null, CancellationToken.None));
        var valid = new WifiBenchOptions(true, "192.168.1.2", 40002, 60, true);
        Assert.AreEqual(IPAddress.Parse("192.168.1.2"), valid.Validate());
        foreach (var invalid in new[] { valid with { Enabled = false }, valid with { CableDisconnected = false },
            valid with { WindowSeconds = 0 }, valid with { WindowSeconds = 901 }, valid with { SessionPort = 8765 },
            valid with { SessionPort = 0 }, valid with { BindAddress = "0.0.0.0" }, valid with { BindAddress = "127.0.0.1" },
            valid with { BindAddress = "::1" }, valid with { BindAddress = "8.8.8.8" }, valid with { BindAddress = "192.168.001.2" } })
            Assert.ThrowsException<ArgumentException>(() => invalid.Validate());
        var bound = command.Bind(command.Parse(["--wifi-bench", "--wifi-bind", "192.168.1.2", "--wifi-session-port", "40002",
            "--wifi-window-seconds", "60", "--wifi-cable-disconnected"]));
        Assert.AreEqual(valid, bound.WifiBench);
    }

    [TestMethod]
    public void Binding_rejects_public_virtual_down_offlink_ipv6_network_and_broadcast()
    {
        var binding = new WifiLanBinding("synthetic", IPAddress.Parse("192.168.1.2"), 24, true, true, true);
        Assert.IsTrue(binding.Allows(IPAddress.Parse("192.168.1.3")));
        foreach (var text in new[] { "192.168.2.3", "192.168.1.0", "192.168.1.255", "192.168.1.2", "::ffff:192.168.1.3", "8.8.8.8" })
            Assert.IsFalse(binding.Allows(IPAddress.Parse(text)), text);
        foreach (var invalid in new[] { binding with { Private = false }, binding with { Physical = false }, binding with { Up = false },
            binding with { Address = IPAddress.Any }, binding with { PrefixLength = 0 } })
            Assert.ThrowsException<InvalidOperationException>(invalid.Validate);
    }

    [TestMethod]
    public void Shared_admission_has_no_queue_four_workers_burst_four_and_twelve_per_rolling_minute()
    {
        var clock = new WifiSecurityRegressionTests.Clock(); var admission = new WifiHandshakeAdmission(clock);
        var leases = Enumerable.Range(0, 4).Select(_ => admission.TryEnter()!).ToArray();
        Assert.IsTrue(leases.All(lease => lease is not null));
        clock.Seconds = 5;
        Assert.IsNull(admission.TryEnter(), "Four concurrent workers is a hard cap even after refill.");
        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        for (var second = 5; second <= 40; second += 5)
        { clock.Seconds = second; using var lease = admission.TryEnter(); Assert.IsNotNull(lease); }
        clock.Seconds = 55; Assert.IsNull(admission.TryEnter(), "Only twelve in the rolling minute.");
        clock.Seconds = 60;
        for (var i = 0; i < 4; i++) { using var lease = admission.TryEnter(); Assert.IsNotNull(lease); }
        Assert.IsNull(admission.TryEnter(), "Burst is still four after idle/refill.");
        clock.Seconds = 61; Assert.IsNull(admission.TryEnter());
    }

    [TestMethod]
    public void Control_has_bounded_message_and_byte_buckets()
    {
        var clock = new WifiSecurityRegressionTests.Clock(); var rate = new WifiBenchSession.ControlRate(clock);
        for (var i = 0; i < 480; i++) rate.Record(1);
        Assert.ThrowsException<InvalidDataException>(() => rate.Record(1));
        clock.Seconds = 1; for (var i = 0; i < 240; i++) rate.Record(1);
        Assert.ThrowsException<InvalidDataException>(() => rate.Record(1));
        rate = new(clock); rate.Record(2 * 1024 * 1024);
        Assert.ThrowsException<InvalidDataException>(() => rate.Record(1));
    }

    [DataTestMethod]
    [DataRow("desktop_start")]
    [DataRow("stream_deck_invoke")]
    [DataRow("media_play_pause")]
    public async Task Real_listener_pipeline_requires_mtls_wifi_limits_and_refuses_every_command(string command)
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(fixture.State.ServerDer, fixture.State.ServerKey);
        using var persisted = fixture.CreateDeviceKey();
        using var key = new ECDsaCng(persisted);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var client = leaf.CopyWithPrivateKey(key);
        using var registry = new WifiPeerRegistry(fixture.Store); registry.Issue(leaf, 31);
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var deviceTls = new SslStream(right);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var handling = WifiBenchListener.AuthenticateAndServeAsync(left, server, root, registry, deadline.Token);
        await deviceTls.AuthenticateAsClientAsync(WifiTls.ClientOptions(root, WifiCertificates.HostId(root), client, false), deadline.Token);
        await using var device = new MultiplexedTransportSession(deviceTls, PlmxRole.Accessory, limits: CarrierLimits.WifiV1);
        await device.StartAsync(deadline.Token);
        await using var control = await device.OpenChannelAsync(PanelChannelId.Control, deadline.Token);
        await using var json = new JsonLineConnection(control);
        await json.WriteAsync(new HelloMessage { DeviceName = "Synthetic bench" }, deadline.Token);
        var ack = PanelJson.Deserialize<HelloAckMessage>((await json.ReadLineAsync(deadline.Token))!);
        Assert.AreEqual(0, ack.Features!.Length, "No Desktop, touch or Stream Deck advertisement.");
        StringAssert.Contains((await json.ReadLineAsync(deadline.Token))!, "Synthetic bench");
        await json.WriteAsync(new PingMessage(), deadline.Token);
        Assert.AreEqual(MessageTypes.Pong, PanelJson.Deserialize<MessageEnvelope>((await json.ReadLineAsync(deadline.Token))!).Type);
        await using var video = await device.OpenChannelAsync(PanelChannelId.Video, deadline.Token);
        Assert.AreEqual(0, await video.ReadAsync(new byte[1], deadline.Token));
        await json.WriteAsync(new CommandMessage { Command = command }, deadline.Token);
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => handling);
        StringAssert.Contains(error.Message, "operation refused");
        Assert.IsTrue(registry.IsAllowed(leaf), "Refusing a command closes the session, not the pairing grant.");
    }

    [TestMethod]
    public async Task Missing_panel_capability_never_reaches_the_control_dispatcher()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var unused = right;
        await using var session = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1);
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => WifiBenchSession.RunAsync(session,
            new("synthetic", 1, 2, CancellationToken.None), left.Dispose, CancellationToken.None));
        StringAssert.Contains(error.Message, "capability required");
    }

    [TestMethod]
    public async Task Heartbeat_cancels_after_fifteen_seconds_even_without_a_completed_control_message()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var unused = right;
        await using var session = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1);
        var clock = new TimerClock(); var disconnected = false;
        var handling = WifiBenchSession.RunAsync(session, new("synthetic", 1, 1, CancellationToken.None),
            () => { disconnected = true; left.Dispose(); }, CancellationToken.None, clock);
        Assert.AreEqual(TimeSpan.FromSeconds(15), clock.Due);
        clock.Fire();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => handling);
        Assert.IsTrue(disconnected);
    }

    private sealed class TimerClock : TimeProvider
    {
        public TimeSpan Due;
        private TimerCallback? _callback; private object? _state;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; Due = dueTime; return new Timer(this); }
        public void Fire() => _callback!(_state);
        private sealed class Timer(TimerClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) { clock.Due = dueTime; return true; }
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
