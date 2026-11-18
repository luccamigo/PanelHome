using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;
using PanelHost.WifiBenchClient;

namespace PanelHost.Tests;

// Authorised exception (suite only): a real TcpListener on 127.0.0.1 driving the production accept loop
// (WifiBenchListener.ServeLoopAsync) and mutual-TLS pipeline. Production code never accepts loopback:
// WifiBenchOptions.Validate and WifiLanBinding refuse it, and RunAsync always passes the RFC1918 source filter.
[TestClass]
public sealed class WifiBenchLoopbackTests
{
    // Every test here starts the real Wi-Fi bench code path, which the host refuses in an elevated process.
    [TestInitialize]
    public void RequireNonElevatedHost() => ElevatedRunnerGuard.RequireNonElevatedProcess();

    private sealed class Rig : IAsyncDisposable
    {
        public readonly WifiCoreTests.IdentityFixture Fixture = new();
        public readonly WifiBenchCounters Counters = new();
        public readonly ConcurrentBag<string> Used = [];
        public WifiPeerRegistry Registry = null!;
        public WifiBenchOffer Offer = null!;
        public X509Certificate2 Device = null!;
        public IPEndPoint Endpoint = null!;
        public string KeyId = "";
        public CancellationTokenSource Window = new();
        public Task Loop = Task.CompletedTask;
        public X509Certificate2 Root => _root;
        private CngKey _key = null!;
        public CngKey Key => _key;
        private TcpListener _listener = null!;
        private X509Certificate2 _root = null!, _server = null!;

        public static Rig Start(Func<IPAddress, bool>? allow = null, TimeProvider? admissionClock = null)
        {
            var rig = new Rig(); Last = rig.Counters;
            rig._key = rig.Fixture.CreateDeviceKey();
            using (var signer = new ECDsaCng(rig._key))
            {
                var spki = signer.ExportSubjectPublicKeyInfo();
                rig.KeyId = Convert.ToHexString(SHA256.HashData(spki));
                var spkiPath = Path.Combine(rig.Fixture.Directory, "device.spki");
                File.WriteAllBytes(spkiPath, spki);
                var issue = WifiBenchAdmin.Run(new(WifiAdminCommand.IssuePeer, spkiPath, rig.KeyId,
                    Path.Combine(rig.Fixture.Directory, "offer.json"), "192.168.1.2", 40002), rig.Fixture.Store, TextWriter.Null, TextWriter.Null);
                Assert.AreEqual(WifiBenchAdmin.Ok, issue);
            }
            rig.Offer = WifiBenchOffer.Decode(File.ReadAllBytes(Path.Combine(rig.Fixture.Directory, "offer.json")));
            rig.Device = WifiBenchClientRunner.DeviceCertificate(rig.Offer, rig._key);
            rig.Registry = new WifiPeerRegistry(rig.Fixture.Store);
            rig._root = WifiIdentityStore.OpenCertificate(rig.Fixture.State.RootDer, rig.Fixture.State.RootKey);
            rig._server = WifiIdentityStore.OpenCertificate(rig.Fixture.State.ServerDer, rig.Fixture.State.ServerKey);
            rig._listener = new TcpListener(IPAddress.Loopback, 0); rig._listener.Start(4);
            rig.Endpoint = (IPEndPoint)rig._listener.LocalEndpoint;
            rig.Loop = WifiBenchListener.ServeLoopAsync(rig._listener, allow ?? (_ => true), new WifiHandshakeAdmission(admissionClock), rig._server,
                rig._root, rig.Registry, rig.Counters, rig.Used.Add, rig.Window.Token);
            return rig;
        }

        public async Task StopAsync()
        {
            Window.Cancel();
            await Loop.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Registry.Dispose(); Device.Dispose(); _root.Dispose(); _server.Dispose(); _key.Dispose(); Window.Dispose();
            Fixture.Dispose();
        }
    }

    private static WifiBenchCounters? Last;
    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > deadline) Assert.Fail(because + " " + Last?.Format()); await Task.Delay(10); }
    }

    [TestMethod]
    public async Task Positive_session_over_a_real_socket_uses_mtls_plmx_and_stays_read_only()
    {
        await using var rig = Rig.Start();
        var result = await WifiBenchClientRunner.PositiveAsync(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.Completed) == 1, "session did not end");
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.Authenticated));
        CollectionAssert.AreEqual(new[] { rig.KeyId }, rig.Used.ToArray());
    }

    [TestMethod]
    public async Task No_client_certificate_is_refused_and_counted()
    {
        await using var rig = Rig.Start();
        var result = await WifiBenchClientRunner.NoCertificateAsync(rig.Offer, rig.Endpoint, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.TlsNoCertificate) == 1, "no_certificate was not counted");
        Assert.AreEqual(0, rig.Counters.Get(WifiBenchEvent.Authenticated));
    }

    [TestMethod]
    public async Task Certificate_from_another_root_is_refused_and_counted()
    {
        await using var rig = Rig.Start();
        using var key = rig.Fixture.CreateDeviceKey();
        using var foreign = WifiBenchClientRunner.ForeignRootCertificate(key);
        var result = await WifiBenchClientRunner.WrongRootAsync(rig.Offer, rig.Endpoint, foreign, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.TlsInvalidCertificate) == 1, "invalid_certificate was not counted");
        Assert.AreEqual(0, rig.Counters.Get(WifiBenchEvent.Authenticated));
    }

    [TestMethod]
    public async Task Revoked_peer_is_refused_before_any_application_byte_and_counted_as_revoked()
    {
        await using var rig = Rig.Start();
        rig.Registry.Revoke(rig.KeyId);
        var result = await WifiBenchClientRunner.RevokedOrUnknownAsync(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.TlsRevoked) == 1, "revoked was not counted");
        Assert.AreEqual(0, rig.Counters.Get(WifiBenchEvent.Authenticated));
    }

    [DataTestMethod]
    [DataRow("tls12")]
    [DataRow("wrong-alpn")]
    public async Task Wrong_protocol_or_alpn_is_refused_and_counted_as_tls_other(string kind)
    {
        await using var rig = Rig.Start();
        var result = kind == "tls12"
            ? await WifiBenchClientRunner.Tls12Async(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None)
            : await WifiBenchClientRunner.WrongAlpnAsync(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.TlsOther) == 1, "tls_other was not counted");
        Assert.AreEqual(0, rig.Counters.Get(WifiBenchEvent.Authenticated));
    }

    [DataTestMethod]
    [DataRow("desktop_start")]
    [DataRow("stream_deck_invoke")]
    public async Task Desktop_and_stream_deck_commands_close_the_session_without_effect(string command)
    {
        await using var rig = Rig.Start();
        var result = await WifiBenchClientRunner.CommandAsync(rig.Offer, rig.Endpoint, rig.Device, command, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.CommandRefused) == 1, "command_refused was not counted");
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.Authenticated));
    }

    [TestMethod]
    public async Task Slowloris_is_closed_by_the_five_second_handshake_deadline()
    {
        await using var rig = Rig.Start();
        var result = await WifiBenchClientRunner.SlowlorisAsync(rig.Endpoint, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.TlsOther) == 1, "stalled handshake was not counted");
    }

    [TestMethod]
    public async Task Fifth_simultaneous_connection_is_refused_by_admission_before_tls()
    {
        await using var rig = Rig.Start();
        var result = await WifiBenchClientRunner.FloodAsync(rig.Endpoint, 5, CancellationToken.None);
        Assert.IsTrue(result.Passed, result.ToString());
        Assert.AreEqual(1, rig.Counters.Get(WifiBenchEvent.AdmissionRefused));
    }

    [TestMethod]
    public async Task Sources_outside_the_allowed_subnet_are_closed_and_counted()
    {
        await using var rig = Rig.Start(allow: _ => false);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(rig.Endpoint);
        var buffer = new byte[1];
        var read = await Task.Run(async () =>
        {
            try { return await tcp.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (IOException) { return 0; }
        });
        Assert.AreEqual(0, read);
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.OriginRefused) == 1, "origin_refused was not counted");
    }

    [TestMethod]
    public async Task Ending_the_window_closes_sessions_and_the_port_within_a_second()
    {
        await using var rig = Rig.Start();
        await using var connection = await WifiBenchClientRunner.EstablishAsync(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None);
        var started = DateTime.UtcNow;
        rig.Window.Cancel();
        await rig.Loop.WaitAsync(TimeSpan.FromSeconds(1));
        string? line = "still open";
        try { line = await connection.Json.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException
            or PanelHost.Transport.PanelTransportDisconnectedException) { line = null; }
        Assert.IsNull(line, "Session must be closed by the end of the window.");
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        await Assert.ThrowsExceptionAsync<SocketException>(async () => { using var late = new TcpClient(); await late.ConnectAsync(rig.Endpoint); });
    }

    [TestMethod]
    public async Task Revoking_a_connected_peer_closes_its_session_within_a_second()
    {
        await using var rig = Rig.Start();
        await using var connection = await WifiBenchClientRunner.EstablishAsync(rig.Offer, rig.Endpoint, rig.Device, CancellationToken.None);
        rig.Registry.Revoke(rig.KeyId);
        string? line = "still open";
        try { line = await connection.Json.ReadLineAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException
            or PanelHost.Transport.PanelTransportDisconnectedException) { line = null; }
        Assert.IsNull(line);
        await Eventually(() => rig.Counters.Get(WifiBenchEvent.SessionRevoked) == 1, "session_revoked was not counted");
    }

    // The exact sequence of docs/wifi-bench.md, with the admission clock advanced instead of waiting six seconds
    // between cases. The final line is the documented example of what the host prints at the end of the window.
    [TestMethod]
    public async Task Documented_bench_sequence_ends_with_the_documented_counters()
    {
        var clock = new WifiSecurityRegressionTests.Clock();
        await using var rig = Rig.Start(admissionClock: clock);
        using var secondKey = rig.Fixture.CreateDeviceKey();
        using var secondSigner = new ECDsaCng(secondKey);
        using var secondLeaf = WifiCertificates.Issue(rig.Root, secondSigner.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        rig.Registry.Issue(secondLeaf, 1); rig.Registry.Release(rig.Registry.Activate(secondLeaf));
        rig.Registry.Revoke(Convert.ToHexString(SHA256.HashData(secondSigner.ExportSubjectPublicKeyInfo())));
        using var revokedDevice = secondLeaf.CopyWithPrivateKey(secondSigner);
        using var foreign = WifiBenchClientRunner.ForeignRootCertificate(rig.Key);
        using var valid = new WifiBenchClientProgram.Peer(rig.Offer, rig.Device, foreign);
        using var revoked = new WifiBenchClientProgram.Peer(rig.Offer, revokedDevice, revokedDevice);
        // The client's own window function; its pauses advance the admission clock instead of sleeping.
        var results = await WifiBenchClientProgram.RunWindowAsync(valid, revoked, rig.Endpoint, (pause, _) =>
        { clock.Seconds += (long)pause.TotalSeconds; return Task.CompletedTask; }, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "positive", "revoked-or-unknown-peer", "no-certificate", "wrong-root", "tls-1.2", "wrong-alpn",
            "command-desktop_start", "command-stream_deck_invoke", "slowloris", "flood" }, results.Select(r => r.Case).ToArray());
        foreach (var result in results) Assert.IsTrue(result.Passed, result.ToString());
        const string expected = "[WiFi bench] counters: authenticated=3 completed=3 origin_refused=0 admission_refused=4 " +
            "tls_no_certificate=1 tls_invalid_certificate=1 tls_revoked=1 tls_not_authorized=0 tls_other=7 " +
            "protocol_refused=0 control_rate=0 command_refused=2 heartbeat_timeout=0 session_revoked=0 " +
            "pairing_approved=0 pairing_denied=0 pairing_failed=0";
        await Eventually(() => rig.Counters.Format() == expected, "counters differ from the documented example");
    }

    [TestMethod]
    public void Counter_log_line_holds_only_names_and_numbers()
    {
        var counters = new WifiBenchCounters();
        counters.Increment(WifiBenchEvent.AdmissionRefused); counters.Increment(WifiTlsRefusal.Revoked); counters.Increment(WifiTlsRefusal.Revoked);
        var line = counters.Format();
        StringAssert.Matches(line, new Regex(@"^\[WiFi bench\] counters: ([a-z_]+=\d+)( [a-z_]+=\d+)*$"));
        StringAssert.Contains(line, "admission_refused=1");
        StringAssert.Contains(line, "tls_revoked=2");
        Assert.AreEqual(Enum.GetValues<WifiBenchEvent>().Length, line.Split(' ').Count(part => part.Contains('=')));
    }
}
