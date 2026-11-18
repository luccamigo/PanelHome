using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

// The C# half of scripts/run-cross-language-wifi-pairing.ps1: the production pairing session on two loopback listeners (one
// QR, one typed code), approved automatically, while the Kotlin production client pairs against them. Skipped (inconclusive)
// in the normal suite; the script sets PANELOS_WIFI_LIVE_MANIFEST. Loopback only, test-process only.
[TestClass]
public sealed class CrossLanguageWifiPairingHostTests
{
    private const string ManifestEnvironment = "PANELOS_WIFI_LIVE_MANIFEST";

    [TestMethod]
    [TestCategory("CrossLanguageWifiHost")]
    public async Task Production_pairing_sessions_serve_the_kotlin_client_and_approve_it()
    {
        var manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironment);
        if (string.IsNullOrWhiteSpace(manifestPath)) Assert.Inconclusive("Run scripts/run-cross-language-wifi-pairing.ps1.");

        await using var qr = WifiPairingSessionTests.PairRig.Start();
        await using var code = WifiPairingSessionTests.PairRig.Start(pinMode: true);
        // A third identity serves the whole path: pairing AND the mutual-TLS session listener (the bench window's loop),
        // so the Kotlin transport can pair and then connect, with the 15 s heartbeat of the real host watching it.
        await using var live = WifiPairingSessionTests.PairRig.Start();
        var sessionListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        sessionListener.Start(4);
        using var stop = new CancellationTokenSource();
        var sessionLoop = WifiBenchListener.ServeLoopAsync(sessionListener, _ => true, new WifiHandshakeAdmission(), live.Server, live.Root,
            live.Registry, live.Counters, _ => { }, stop.Token);
        async Task Approve(WifiPairingSessionTests.PairRig rig)
        {
            while (!stop.IsCancellationRequested)
            {
                if (rig.Session.Pending is { } pending) { rig.Session.Approve(pending.Fingerprint); return; }
                await Task.Delay(20);
            }
        }
        var approvals = Task.WhenAll(Approve(qr), Approve(code), Approve(live));

        var manifest = new
        {
            qrText = qr.Session.QrText, qrPort = qr.Endpoint.Port, qrHostId = Convert.ToHexString(qr.Offer.HostId),
            codeText = code.Session.TypedCode, codePort = code.Endpoint.Port, codeHostId = Convert.ToHexString(code.Offer.HostId),
            liveText = live.Session.QrText, livePairPort = live.Endpoint.Port, liveSessionPort = ((System.Net.IPEndPoint)sessionListener.LocalEndpoint).Port,
        };
        var temporary = manifestPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(manifest));
        File.Move(temporary, manifestPath!);

        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (!File.Exists(manifestPath + ".done") && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.IsTrue(File.Exists(manifestPath + ".done"), "the Kotlin client never finished");
        await approvals.WaitAsync(TimeSpan.FromSeconds(5));
        // What the real host saw of the Kotlin session, before it is torn down.
        Assert.AreEqual("Active", live.Fixture.Store.Load().Peers.Single().State, "first mutual TLS activated the paired peer");
        Assert.IsTrue(live.Counters.Get(WifiBenchEvent.Authenticated) >= 1);
        foreach (var refusal in new[] { WifiBenchEvent.HeartbeatTimeout, WifiBenchEvent.CommandRefused, WifiBenchEvent.ProtocolRefused,
            WifiBenchEvent.TlsRevoked, WifiBenchEvent.TlsNotAuthorized, WifiBenchEvent.TlsInvalidCertificate, WifiBenchEvent.ControlRate })
            Assert.AreEqual(0, live.Counters.Get(refusal), refusal.ToString());
        stop.Cancel();
        await sessionLoop.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var (rig, expected) in new[] { (qr, "Kotlin QR device"), (code, "Kotlin code device"), (live, "Kotlin session device") })
        {
            Assert.AreEqual(WifiPairingOutcome.Approved, rig.Session.Outcome, expected);
            var peer = rig.Fixture.Store.Load().Peers.Single();
            Assert.AreEqual(expected, peer.Label);
            Assert.AreEqual(WifiBenchSession.PanelReadCapability, peer.Capabilities);
        }
        File.WriteAllText(manifestPath + ".host-passed", "ok");
    }
}