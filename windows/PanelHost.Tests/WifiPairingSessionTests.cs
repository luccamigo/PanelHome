using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;
using PanelHost.WifiBenchClient;

namespace PanelHost.Tests;

// A clock whose timers fire only when the test advances it: the 60 s approval and the 10 minute invitation are
// proven without waiting. Sockets in this file are on 127.0.0.1 (suite-only exception; the product never accepts it).
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private long _milliseconds;
    private readonly DateTimeOffset _origin = DateTimeOffset.UtcNow; // certificates are issued at this wall time
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() { lock (_gate) return _milliseconds; }
    public override DateTimeOffset GetUtcNow() { lock (_gate) return _origin.AddMilliseconds(_milliseconds); }

    public void Advance(TimeSpan by)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _milliseconds += (long)by.TotalMilliseconds;
            due = _timers.Where(timer => timer.Due >= 0 && timer.Due <= _milliseconds).ToList();
            foreach (var timer in due) timer.Due = -1;
        }
        foreach (var timer in due) timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due = -1;
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? -1 : owner._milliseconds + (long)dueTime.TotalMilliseconds;
            return true;
        }
        public void Dispose() { lock (owner._gate) { Due = -1; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

[TestClass]
public sealed class WifiPairingSessionTests
{
    internal sealed class PairRig : IAsyncDisposable
    {
        public readonly WifiCoreTests.IdentityFixture Fixture = new();
        public readonly FakeTimeProvider Clock = new();
        public readonly WifiBenchCounters Counters = new();
        public WifiPeerRegistry Registry = null!;
        public WifiPairingSession Session = null!;
        public WifiPairingOffer Offer = null!;
        public string? Pin;
        public IPEndPoint Endpoint = null!;
        public Task Run = Task.CompletedTask;
        private X509Certificate2 _root = null!, _server = null!;
        private TcpListener _listener = null!;
        public X509Certificate2 Root => _root;
        public X509Certificate2 Server => _server;
        public readonly CancellationTokenSource Window = new();

        public static PairRig Start(bool pinMode = false, Func<IPAddress, bool>? allow = null)
        {
            var rig = new PairRig();
            rig.Registry = new WifiPeerRegistry(rig.Fixture.Store);
            rig._root = WifiIdentityStore.OpenCertificate(rig.Fixture.State.RootDer, rig.Fixture.State.RootKey);
            rig._server = WifiIdentityStore.OpenCertificate(rig.Fixture.State.ServerDer, rig.Fixture.State.ServerKey);
            if (pinMode) rig.Offer = WifiPairingOffer.CreateWithPin(rig._root, "192.168.1.2", 40001, 40002, out rig.Pin);
            else rig.Offer = WifiPairingOffer.Create(rig._root, "192.168.1.2", 40001, 40002);
            rig._listener = new TcpListener(IPAddress.Loopback, 0); rig._listener.Start(4);
            rig.Endpoint = (IPEndPoint)rig._listener.LocalEndpoint;
            // A fresh offer object is handed to the session (it zeroes the token on disposal); keep a copy for clients.
            var copy = WifiPairingOffer.Decode(rig.Offer.Encode());
            rig.Session = new WifiPairingSession(rig.Offer, rig.Pin, rig._root, rig._server, rig.Registry,
                new WifiHandshakeAdmission(rig.Clock), allow ?? (_ => true), rig.Counters, rig.Clock);
            rig.Offer = copy;
            rig.Run = rig.Session.RunAsync(rig._listener, rig.Window.Token);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            Session.Cancel();
            await Run.WaitAsync(TimeSpan.FromSeconds(5));
            Session.Dispose(); Registry.Dispose(); _root.Dispose(); _server.Dispose(); Window.Dispose(); Offer.Dispose();
            Fixture.Dispose();
        }

        public async Task<bool> PortIsClosedAsync()
        {
            try { using var probe = new TcpClient(); await probe.ConnectAsync(Endpoint); return false; }
            catch (SocketException) { return true; }
        }
    }

    private static async Task<T> Eventually<T>(Func<T?> read, string because) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            if (read() is { } value) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail(because);
            await Task.Delay(10);
        }
    }

    private static async Task ExpectFailureAsync(Task task, string because)
    {
        try { await task; }
        catch (Exception error) when (error is not AssertFailedException) { return; }
        Assert.Fail(because);
    }

    private static string Fingerprint(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    [TestMethod]
    public async Task Qr_pairing_issues_one_panel_read_certificate_only_after_local_approval()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet da sala", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request shown to the operator");
        Assert.AreEqual("Tablet da sala", pending.Label);
        Assert.AreEqual(Fingerprint(key), pending.Fingerprint, "The full SHA-256 of the device key is what the operator sees.");
        Assert.IsFalse(pairing.IsCompleted, "Nothing is issued before the local approval.");
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);

        Assert.IsTrue(rig.Session.Approve(pending.Fingerprint));
        using var leaf = await pairing;
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Approved, rig.Session.Outcome);
        var peer = rig.Fixture.Store.Load().Peers.Single();
        Assert.AreEqual("Issued", peer.State);
        Assert.AreEqual(WifiBenchSession.PanelReadCapability, peer.Capabilities, "Only reading the panel: no Desktop, touch or Stream Deck.");
        Assert.AreEqual("Tablet da sala", peer.Label);
        Assert.IsNotNull(peer.CreatedAt);
        Assert.AreEqual(Fingerprint(key), peer.KeyId);
        Assert.IsTrue(rig.Registry.IsAllowed(leaf), "The certificate can open the session listener within its 60 s.");
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.PairingApproved));
        Assert.IsTrue(await rig.PortIsClosedAsync(), "The pairing listener closes as soon as the credential is delivered.");
    }

    [TestMethod]
    public async Task Approving_a_different_fingerprint_does_nothing_and_a_denial_issues_no_credential()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        Assert.IsFalse(rig.Session.Approve(new string('A', 64)), "An approval of another request must not apply.");
        Assert.IsFalse(rig.Session.Deny(new string('B', 64)));
        Assert.IsTrue(rig.Session.Deny(pending.Fingerprint));
        await ExpectFailureAsync(pairing, "A denied request must not return a certificate.");
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Denied, rig.Session.Outcome);
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.PairingDenied));
        Assert.IsTrue(await rig.PortIsClosedAsync());
    }

    [TestMethod]
    public async Task Approval_that_does_not_come_within_sixty_seconds_expires_the_request()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        Assert.AreEqual(60, pending.ApproveSecondsLeft);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.AreEqual(30, rig.Session.Pending!.ApproveSecondsLeft);
        rig.Clock.Advance(TimeSpan.FromSeconds(31));
        await ExpectFailureAsync(pairing, "The request must be refused after 60 s without approval.");
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Expired, rig.Session.Outcome);
        Assert.IsFalse(rig.Session.Approve(pending.Fingerprint), "A late approval is ignored.");
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);
    }

    [TestMethod]
    public async Task An_app_that_gives_up_while_the_operator_is_being_asked_ends_the_request_as_abandoned()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var giveUp = new CancellationTokenSource();
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet", giveUp.Token);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        giveUp.Cancel();
        await ExpectFailureAsync(pairing, "A cancelled client gets no certificate.");
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Abandoned, rig.Session.Outcome, "Not 'denied': the operator never refused anything.");
        Assert.IsNull(rig.Session.Pending, "The request must disappear instead of waiting for a pointless approval.");
        Assert.IsFalse(rig.Session.Approve(pending.Fingerprint), "A late approval of an abandoned request does nothing.");
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length, "Nothing was issued for a client that left.");
        Assert.IsTrue(await rig.PortIsClosedAsync());
    }

    [TestMethod]
    public async Task Approving_when_the_pc_already_holds_the_maximum_devices_reports_the_limit_and_issues_nothing()
    {
        await using var rig = PairRig.Start();
        for (var index = 0; index < 8; index++)
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var existing = WifiCertificates.Issue(rig.Root, other.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
            rig.Registry.Issue(existing, 31);
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Nono", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        Assert.IsTrue(rig.Session.Approve(pending.Fingerprint));
        await ExpectFailureAsync(pairing, "Over the limit nothing may be issued.");
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.LimitReached, rig.Session.Outcome);
        Assert.AreEqual(8, rig.Fixture.Store.Load().Peers.Length);
        Assert.IsFalse(rig.Fixture.Store.Load().Peers.Any(peer => peer.KeyId == Fingerprint(key)));
    }

    // U1: ApproveLocally closes the invitation before it touches the registry, so a registry failure after the
    // approval can never let the same invitation be approved or attempted again.
    [TestMethod]
    public void An_invitation_is_not_reused_when_the_registry_fails_after_the_approval()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var offer = WifiPairingOffer.Create(root, "192.168.1.2", 40001, 40002);
        using var registry = new WifiPeerRegistry(fixture.Store);
        for (var index = 0; index < 8; index++)
        {
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var existing = WifiCertificates.Issue(root, other.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
            registry.Issue(existing, 31);
        }
        using var invitation = new WifiPairingInvitation(offer);
        using var attempt = invitation.BeginAttempt();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignData(WifiProof.Message(attempt.Challenge, offer.HostId, spki, offer.PairId, offer.Token),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(attempt.Reserve(offer.PairId, offer.Token, spki, signature, "Device"));
        Assert.ThrowsException<WifiPairLimitReachedException>(() => invitation.ApproveLocally(root, registry, 31));
        Assert.IsTrue(invitation.IsClosed, "A failed issuance still spends the invitation.");
        Assert.AreEqual("pairing_failed", Assert.ThrowsException<InvalidOperationException>(() => invitation.ApproveLocally(root, registry, 31)).Message);
        Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt());
        Assert.AreEqual(8, fixture.Store.Load().Peers.Length);
    }
    [TestMethod]
    public async Task The_invitation_lasts_ten_minutes_then_closes_without_any_client()
    {
        await using var rig = PairRig.Start();
        Assert.AreEqual(600, rig.Session.ExpiresInSeconds);
        rig.Clock.Advance(TimeSpan.FromSeconds(599));
        Assert.AreEqual(1, rig.Session.ExpiresInSeconds);
        Assert.IsFalse(rig.Run.IsCompleted);
        rig.Clock.Advance(TimeSpan.FromSeconds(2));
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Expired, rig.Session.Outcome);
        Assert.AreEqual(0, rig.Session.ExpiresInSeconds);
        Assert.IsTrue(await rig.PortIsClosedAsync());
    }

    [TestMethod]
    public async Task Wrong_token_is_refused_and_five_invalid_attempts_close_the_invitation_for_good()
    {
        await using var rig = PairRig.Start();
        using var wrong = new WifiPairingOffer { HostId = rig.Offer.HostId, RootDer = rig.Offer.RootDer, PairId = rig.Offer.PairId,
            Token = RandomNumberGenerator.GetBytes(32), Ip = rig.Offer.Ip, PairPort = rig.Offer.PairPort, SessionPort = rig.Offer.SessionPort,
            ExpiresAt = rig.Offer.ExpiresAt };
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await ExpectFailureAsync(WifiPairingClient.PairWithOfferAsync(wrong, rig.Endpoint, key, "x", CancellationToken.None),
                $"attempt {attempt} with a wrong token must fail");
            rig.Clock.Advance(TimeSpan.FromSeconds(6)); // stay inside the shared handshake admission
        }
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.TooManyInvalid, rig.Session.Outcome);
        Assert.IsTrue(await rig.PortIsClosedAsync(), "After the fifth invalid attempt the listener is gone.");
        Assert.AreEqual(5, rig.Counters.Get(WifiBenchEvent.PairingFailed));
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);
        // Even the right token cannot use a closed invitation.
        using var late = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ExpectFailureAsync(WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, late, "late", CancellationToken.None), "closed");
    }

    [TestMethod]
    public async Task A_request_signed_for_another_connection_challenge_is_a_replay_and_fails()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        byte[]? captured = null;
        // Connection 1: read challenge 1, sign it, send the request with the right token but tamper nothing else.
        // We capture the exact bytes, then withhold them (close) so they are never used on connection 1.
        for (var round = 1; round <= 2; round++)
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(rig.Endpoint);
            await using var ssl = new System.Net.Security.SslStream(tcp.GetStream());
            await ssl.AuthenticateAsClientAsync(WifiTls.PairClientOptions(rig.Offer));
            var wire = new WifiPairingWire(ssl);
            using var challenge = await wire.ReadAsync(CancellationToken.None);
            var challengeBytes = Convert.FromBase64String(challenge.RootElement.GetProperty("challenge").GetString()!.Replace('-', '+').Replace('_', '/') + "=");
            if (round == 1)
            {
                var signature = key.SignData(WifiProof.Message(challengeBytes, rig.Offer.HostId, spki, rig.Offer.PairId, rig.Offer.Token),
                    HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                captured = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { type = "pair_request", pairId = WifiPairingOffer.B64(rig.Offer.PairId),
                    token = WifiPairingOffer.B64(rig.Offer.Token), spki = WifiPairingOffer.B64(spki), label = "replay", signature = WifiPairingOffer.B64(signature) });
            }
            else
            {
                await wire.WriteAsync(captured!, CancellationToken.None); // signed over challenge 1, sent on connection 2
                using var result = await wire.ReadAsync(CancellationToken.None);
                Assert.AreEqual("pairing_failed", result.RootElement.GetProperty("status").GetString());
            }
            rig.Clock.Advance(TimeSpan.FromSeconds(6));
        }
        Assert.IsNull(rig.Session.Pending, "A replayed proof never becomes a request for the operator.");
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);
    }

    [TestMethod]
    public async Task A_second_device_cannot_take_over_while_a_request_is_waiting_for_approval()
    {
        await using var rig = PairRig.Start();
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, first, "first", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await ExpectFailureAsync(WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, second, "second", CancellationToken.None),
            "One candidate per invitation: the second device is refused.");
        Assert.AreEqual(Fingerprint(first), rig.Session.Pending!.Fingerprint);
        Assert.IsTrue(rig.Session.Approve(pending.Fingerprint));
        using var leaf = await pairing;
        Assert.AreEqual(Fingerprint(first), Convert.ToHexString(SHA256.HashData(leaf.PublicKey.ExportSubjectPublicKeyInfo())));
        Assert.AreEqual(1, rig.Fixture.Store.Load().Peers.Length, "Exactly one credential per invitation.");
    }

    [TestMethod]
    public async Task Cancelling_the_invitation_refuses_the_waiting_device_and_closes_the_port()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet", CancellationToken.None);
        await Eventually(() => rig.Session.Pending, "no pending request");
        rig.Session.Cancel();
        await ExpectFailureAsync(pairing, "Cancelling must refuse the waiting device.");
        await rig.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(WifiPairingOutcome.Cancelled, rig.Session.Outcome);
        Assert.IsTrue(await rig.PortIsClosedAsync());
        Assert.AreEqual(0, rig.Fixture.Store.Load().Peers.Length);
    }

    [TestMethod]
    public async Task Labels_with_bidi_or_control_characters_are_not_accepted()
    {
        await using var rig = PairRig.Start();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ExpectFailureAsync(WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet‮evil", CancellationToken.None),
            "A right-to-left override in the label must be refused.");
        Assert.IsNull(rig.Session.Pending);
    }

    [TestMethod]
    public async Task Pin_mode_pairs_with_only_the_typed_code_and_validates_the_pc_before_sending_the_pin()
    {
        await using var rig = PairRig.Start(pinMode: true);
        Assert.IsNull(rig.Session.QrText, "PIN mode shows a typed code, not a QR.");
        var code = WifiTypedCode.Decode(rig.Session.TypedCode!);
        Assert.AreEqual(rig.Pin, code.Pin);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = WifiPairingClient.PairWithCodeAsync(code, rig.Endpoint, key, "Tablet", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request in PIN mode");
        Assert.IsTrue(rig.Session.Approve(pending.Fingerprint));
        using var leaf = await pairing;
        Assert.IsTrue(rig.Registry.IsAllowed(leaf));
    }

    [TestMethod]
    public async Task Pin_mode_with_a_wrong_pin_fails_and_with_a_wrong_host_id_aborts_before_any_request()
    {
        await using var rig = PairRig.Start(pinMode: true);
        var code = WifiTypedCode.Decode(rig.Session.TypedCode!);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrongPin = code with { Pin = code.Pin == "000000" ? "000001" : "000000" };
        await ExpectFailureAsync(WifiPairingClient.PairWithCodeAsync(wrongPin, rig.Endpoint, key, "Tablet", CancellationToken.None), "wrong PIN");
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        // A different PC (typed host id that does not match the one answering) is detected before the PIN leaves the device.
        var attacker = code with { HostId = RandomNumberGenerator.GetBytes(32) };
        var failedBefore = rig.Counters.Get(WifiBenchEvent.PairingFailed);
        await ExpectFailureAsync(WifiPairingClient.PairWithCodeAsync(attacker, rig.Endpoint, key, "Tablet", CancellationToken.None), "wrong host id");
        Assert.IsNull(rig.Session.Pending, "No pair_request ever reached the host.");
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.PairingFailed) == failedBefore + 1 ? "seen" : null,
            "The host should have counted the abandoned connection.");
        // An abandoned attempt counts toward the five allowed (A6), but one early abort leaves the invitation usable.
        Assert.IsFalse(rig.Run.IsCompleted);
        Assert.AreEqual(WifiPairingOutcome.Pending, rig.Session.Outcome);
    }

    [TestMethod]
    public async Task Pairing_port_refuses_tls12_and_the_session_alpn_and_serves_nothing_else()
    {
        await using var rig = PairRig.Start();
        foreach (var tweak in new Action<System.Net.Security.SslClientAuthenticationOptions>[]
        {
            options => options.EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12,
            options => options.ApplicationProtocols = [new(WifiTls.SessionAlpn)],
        })
        {
            using var tcp = new TcpClient(); await tcp.ConnectAsync(rig.Endpoint);
            await using var ssl = new System.Net.Security.SslStream(tcp.GetStream());
            var options = WifiTls.PairClientOptions(rig.Offer); tweak(options);
            await ExpectFailureAsync(ssl.AuthenticateAsClientAsync(options), "TLS profile violations are refused");
            rig.Clock.Advance(TimeSpan.FromSeconds(6));
        }
        Assert.IsNull(rig.Session.Pending);
        Assert.IsTrue(rig.Counters.Get(WifiBenchEvent.TlsOther) >= 2);
    }

    [TestMethod]
    public async Task Sources_outside_the_allowed_subnet_never_reach_the_pairing_wire()
    {
        await using var rig = PairRig.Start(allow: _ => false);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ExpectFailureAsync(WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, key, "Tablet", CancellationToken.None), "origin refused");
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.OriginRefused));
    }

    [TestMethod]
    public async Task A_paired_certificate_opens_the_session_listener_once_and_is_active_afterwards()
    {
        ElevatedRunnerGuard.RequireNonElevatedProcess();
        await using var rig = PairRig.Start();
        using var key = rig.Fixture.CreateDeviceKey();
        using var signer = new ECDsaCng(key);
        var pairing = WifiPairingClient.PairWithOfferAsync(rig.Offer, rig.Endpoint, signer, "Tablet", CancellationToken.None);
        var pending = await Eventually(() => rig.Session.Pending, "no pending request");
        rig.Session.Approve(pending.Fingerprint);
        using var leaf = await pairing;
        using var device = leaf.CopyWithPrivateKey(signer);
        // The same registry instance serves the session listener in the bench process: mTLS activates the Issued peer.
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var serverRoot = WifiIdentityStore.OpenCertificate(rig.Fixture.State.RootDer, rig.Fixture.State.RootKey);
        using var serverCert = WifiIdentityStore.OpenCertificate(rig.Fixture.State.ServerDer, rig.Fixture.State.ServerKey);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handling = WifiBenchListener.AuthenticateAndServeAsync(left, serverCert, serverRoot, rig.Registry, deadline.Token);
        await using var tls = new System.Net.Security.SslStream(right);
        await tls.AuthenticateAsClientAsync(WifiTls.ClientOptions(serverRoot, rig.Offer.HostId, device, false), deadline.Token);
        // TLS 1.3 finishes the client handshake before the server has activated the peer, so wait for the state.
        await Eventually(() => rig.Fixture.Store.Load().Peers.Single().State == "Active" ? "active" : null, "the Issued peer must become Active on the first mTLS");
        deadline.Cancel();
        try { await handling; } catch (Exception) { }
    }
}
