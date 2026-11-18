using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;
using PanelHost.WifiBenchClient;
using PanelOS.Windows.Wifi;

namespace PanelHost.Tests;

// The Control Center's side of pairing: a user-only pipe in front of WifiAdminBackend, with a real pairing listener on
// 127.0.0.1 (suite-only). The pipe name is unique per test, so nothing here touches a running PanelHost.
[TestClass]
public sealed class WifiAdminPipeTests
{
    private sealed class AdminRig : IAsyncDisposable
    {
        public readonly WifiCoreTests.IdentityFixture Fixture = new();
        public readonly CancellationTokenSource Window = new();
        public readonly string PipeName = "PanelOS.Wifi.Admin.test." + Guid.NewGuid().ToString("N");
        public readonly WifiBenchCounters Counters = new();
        public WifiPeerRegistry Registry = null!;
        public WifiAdminBackend Backend = null!;
        public IWifiAdminClient Client = null!;
        private Task _server = Task.CompletedTask;
        private X509RootsHolder _roots = null!;

        private sealed class X509RootsHolder : IDisposable
        {
            public System.Security.Cryptography.X509Certificates.X509Certificate2 Root = null!, Server = null!;
            public void Dispose() { Root.Dispose(); Server.Dispose(); }
        }

        public static AdminRig Start()
        {
            var rig = new AdminRig();
            rig.Registry = new WifiPeerRegistry(rig.Fixture.Store);
            rig._roots = new X509RootsHolder
            {
                Root = WifiIdentityStore.OpenCertificate(rig.Fixture.State.RootDer, rig.Fixture.State.RootKey),
                Server = WifiIdentityStore.OpenCertificate(rig.Fixture.State.ServerDer, rig.Fixture.State.ServerKey),
            };
            rig.Backend = new WifiAdminBackend(rig.Registry, rig._roots.Root, rig._roots.Server, new WifiHandshakeAdmission(), rig.Counters,
                IPAddress.Parse("192.168.1.2"), 40002, 40001, 180, () => new TcpListener(IPAddress.Loopback, 0), _ => true, rig.Window.Token);
            rig._server = new WifiAdminPipeServer(rig.Backend, rig.PipeName).RunAsync(rig.Window.Token);
            rig.Client = new WifiAdminClient(rig.PipeName);
            return rig;
        }

        public Task<WifiAdminResponse> Send(string type, string? mode = null, string? fingerprint = null) =>
            Client.SendAsync(new WifiAdminRequest(WifiAdminProtocol.Version, type, mode, fingerprint), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            Window.Cancel();
            try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            Backend.Dispose(); Registry.Dispose(); _roots.Dispose(); Window.Dispose(); Fixture.Dispose();
        }
    }

    private static async Task<T> Eventually<T>(Func<Task<T?>> read, string because) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            if (await read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail(because);
            await Task.Delay(20);
        }
    }

    private static string Fingerprint(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    [TestMethod]
    public async Task Status_without_an_invitation_shows_the_window_and_no_secret()
    {
        await using var rig = AdminRig.Start();
        var response = await rig.Send(WifiAdminProtocol.Status);
        Assert.IsTrue(response.Ok);
        var status = response.Status!;
        Assert.IsTrue(status.Running);
        Assert.IsTrue(status.WindowSecondsLeft is > 170 and <= 180);
        Assert.AreEqual("192.168.1.2", status.BindAddress);
        Assert.AreEqual(40002, status.SessionPort);
        Assert.IsNull(status.Pairing);
        Assert.AreEqual(0, status.Devices.Count);
        // The PC's own fingerprint, for the operator to compare with what the tablet shows for "the PC": the SHA-256 of the root key.
        Assert.IsNotNull(status.HostId); Assert.AreEqual(64, status.HostId!.Length);
        using var root = WifiIdentityStore.OpenCertificate(rig.Fixture.State.RootDer, rig.Fixture.State.RootKey);
        Assert.AreEqual(Convert.ToHexString(WifiCertificates.HostId(root)), status.HostId);
    }

    [TestMethod]
    public async Task Control_center_pairs_a_device_end_to_end_and_can_then_revoke_it()
    {
        await using var rig = AdminRig.Start();
        var opened = await rig.Send(WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeQr);
        Assert.IsTrue(opened.Ok, opened.Error);
        var pairing = opened.Status!.Pairing!;
        Assert.AreEqual("Pending", pairing.Outcome);
        Assert.IsNotNull(pairing.Qr); Assert.IsNull(pairing.Code);
        Assert.IsTrue(pairing.ExpiresInSeconds is > 590 and <= 600, "Ten minutes.");
        using var offer = WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(pairing.Qr!));
        Assert.AreEqual("192.168.1.2", offer.Ip); Assert.AreEqual(40002, offer.SessionPort);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = WifiPairingClient.PairWithOfferAsync(offer, new IPEndPoint(IPAddress.Loopback, offer.PairPort), key, "Tablet da sala", CancellationToken.None);
        var pending = await Eventually(async () => (await rig.Send(WifiAdminProtocol.Status)).Status!.Pairing?.Pending, "request never reached the Control Center");
        Assert.AreEqual("Tablet da sala", pending.Label);
        Assert.AreEqual(Fingerprint(key), pending.Fingerprint);

        var wrong = await rig.Send(WifiAdminProtocol.Approve, fingerprint: new string('C', 64));
        Assert.IsFalse(wrong.Ok); Assert.AreEqual(WifiAdminProtocol.ErrorNoRequest, wrong.Error);
        Assert.IsFalse(device.IsCompleted);
        var approved = await rig.Send(WifiAdminProtocol.Approve, fingerprint: pending.Fingerprint);
        Assert.IsTrue(approved.Ok, approved.Error);
        using var leaf = await device;

        var status = (await Eventually(async () =>
        {
            var current = (await rig.Send(WifiAdminProtocol.Status)).Status!;
            return current.Pairing is { Outcome: "Approved" } ? current : null;
        }, "the invitation never showed Approved"));
        Assert.IsNull(status.Pairing!.Qr, "The secret is gone once the invitation is used.");
        var listed = status.Devices.Single();
        Assert.AreEqual(Fingerprint(key), listed.Fingerprint); Assert.AreEqual("Tablet da sala", listed.Label);
        Assert.AreEqual("Issued", listed.State); Assert.IsFalse(listed.Connected); Assert.IsNotNull(listed.CreatedAt);

        var revoked = await rig.Send(WifiAdminProtocol.Revoke, fingerprint: listed.Fingerprint.ToLowerInvariant());
        Assert.IsTrue(revoked.Ok, revoked.Error);
        Assert.AreEqual("Revoked", revoked.Status!.Devices.Single().State);
        Assert.IsFalse(rig.Registry.IsAllowed(leaf), "A revoked device is refused immediately.");
    }

    [TestMethod]
    public async Task Denying_through_the_pipe_refuses_the_device_and_issues_nothing()
    {
        await using var rig = AdminRig.Start();
        var pairing = (await rig.Send(WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeQr)).Status!.Pairing!;
        using var offer = WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(pairing.Qr!));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = WifiPairingClient.PairWithOfferAsync(offer, new IPEndPoint(IPAddress.Loopback, offer.PairPort), key, "Tablet", CancellationToken.None);
        var pending = await Eventually(async () => (await rig.Send(WifiAdminProtocol.Status)).Status!.Pairing?.Pending, "no request");
        Assert.IsTrue((await rig.Send(WifiAdminProtocol.Deny, fingerprint: pending.Fingerprint)).Ok);
        try { await device; Assert.Fail("denied device must not get a certificate"); } catch (InvalidDataException) { }
        var status = (await rig.Send(WifiAdminProtocol.Status)).Status!;
        Assert.AreEqual(0, status.Devices.Count);
        Assert.AreEqual("Denied", status.Pairing!.Outcome);
    }

    [TestMethod]
    public async Task One_invitation_at_a_time_cancel_frees_it_and_code_mode_hides_the_qr()
    {
        await using var rig = AdminRig.Start();
        Assert.IsTrue((await rig.Send(WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeQr)).Ok);
        var second = await rig.Send(WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeCode);
        Assert.IsFalse(second.Ok); Assert.AreEqual(WifiAdminProtocol.ErrorPairingOpen, second.Error);
        var cancelled = await rig.Send(WifiAdminProtocol.CancelPairing);
        Assert.AreEqual("Cancelled", cancelled.Status!.Pairing!.Outcome);
        Assert.IsNull(cancelled.Status.Pairing.Qr);
        var code = await rig.Send(WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeCode);
        Assert.IsTrue(code.Ok, code.Error);
        Assert.IsNull(code.Status!.Pairing!.Qr); Assert.IsNotNull(code.Status.Pairing.Code);
        var fields = WifiTypedCode.Decode(code.Status.Pairing.Code!);
        Assert.AreEqual("192.168.1.2", fields.Ip); Assert.AreEqual(40002, fields.SessionPort);
    }

    [TestMethod]
    public async Task Invalid_requests_get_stable_error_codes_and_never_echo_their_content()
    {
        await using var rig = AdminRig.Start();
        Assert.AreEqual(WifiAdminProtocol.ErrorUnsupported, (await rig.Send("shutdown")).Error);
        Assert.AreEqual(WifiAdminProtocol.ErrorInvalid, (await rig.Send(WifiAdminProtocol.OpenPairing, mode: "pin")).Error);
        Assert.AreEqual(WifiAdminProtocol.ErrorInvalid, (await rig.Send(WifiAdminProtocol.Revoke, fingerprint: "abc")).Error);
        Assert.AreEqual(WifiAdminProtocol.ErrorUnknownDevice, (await rig.Send(WifiAdminProtocol.Revoke, fingerprint: new string('A', 64))).Error);
        var oldVersion = await rig.Client.SendAsync(new WifiAdminRequest(99, WifiAdminProtocol.Status), CancellationToken.None);
        Assert.AreEqual(WifiAdminProtocol.ErrorInvalid, oldVersion.Error);
        // Raw garbage and an oversized line.
        foreach (var payload in new[] { "not json\n", new string('x', 6000) + "\n", "[]\n" })
        {
            await using var pipe = new NamedPipeClientStream(".", rig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(2000);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(payload));
            var reply = await new PanelOS.Windows.Protocol.BoundedUtf8LineReader(pipe).ReadLineAsync(CancellationToken.None);
            if (reply is not null)
            {
                Assert.IsFalse(reply.Contains("not json") || reply.Contains("xxxx"), "No request content comes back.");
                Assert.AreEqual(WifiAdminProtocol.ErrorInvalid, WifiAdminProtocol.DeserializeResponse(reply)!.Error);
            }
        }
        Assert.IsTrue((await rig.Send(WifiAdminProtocol.Status)).Ok, "The pipe keeps serving after bad input.");
    }

    [TestMethod]
    public async Task When_the_window_closes_no_new_invitation_can_open_and_status_says_not_running()
    {
        await using var rig = AdminRig.Start();
        rig.Window.Cancel();
        var status = await rig.Send(WifiAdminProtocol.Status);
        if (status.Ok) Assert.IsFalse(status.Status!.Running);
        var opened = rig.Backend.Handle(new WifiAdminRequest(WifiAdminProtocol.Version, WifiAdminProtocol.OpenPairing, WifiAdminProtocol.ModeQr));
        Assert.AreEqual(WifiAdminProtocol.ErrorWindowClosed, opened.Error);
        Assert.IsFalse(opened.Status!.Running);
    }

    [TestMethod]
    public async Task A_missing_server_is_reported_as_unavailable_not_as_an_exception()
    {
        var client = new WifiAdminClient("PanelOS.Wifi.Admin.test.absent." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300));
        var response = await client.SendAsync(new WifiAdminRequest(WifiAdminProtocol.Version, WifiAdminProtocol.Status), CancellationToken.None);
        Assert.IsFalse(response.Ok); Assert.AreEqual(WifiAdminProtocol.ErrorUnavailable, response.Error); Assert.IsNull(response.Status);
    }

    [TestMethod]
    public void The_pairing_port_is_optional_validated_and_never_the_session_or_adb_port()
    {
        var valid = new WifiBenchOptions(true, "192.168.1.2", 40002, 180, true);
        Assert.AreEqual(0, valid.PairPort, "Pairing administration is off unless asked for.");
        valid.Validate();
        (valid with { PairPort = 40001 }).Validate();
        foreach (var bad in new[] { 40002, 80, 8765, 8766, 8767, 70000, -1 })
            Assert.ThrowsException<ArgumentException>(() => (valid with { PairPort = bad }).Validate(), bad.ToString());
        var command = new PanelHost.Hosting.PanelHostCommandLine((_, _) => Task.FromResult(0));
        var bound = command.Bind(command.Parse(["--wifi-bench", "--wifi-bind", "192.168.1.2", "--wifi-session-port", "40002",
            "--wifi-pair-port", "40001", "--wifi-window-seconds", "180", "--wifi-cable-disconnected"])).WifiBench;
        Assert.AreEqual(new WifiBenchOptions(true, "192.168.1.2", 40002, 180, true, 40001), bound);
    }}
