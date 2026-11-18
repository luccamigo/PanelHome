using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PanelHost.Protocol;
using PanelHost.Security.Wifi;
using PanelHost.Server;
using PanelHost.Transport;

namespace PanelHost.WifiBenchClient;

internal sealed record BenchResult(string Case, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")} {Case}: {Detail}";
}

// Test-side behaviour for the WiFi bench listener. It only ever connects out; it never listens.
// TLS 1.3 may finish the client handshake before the server rejects the client certificate, so a refused client
// is recognised by "the server closed before sending any application byte", never by the handshake call alone.
internal static class WifiBenchClientRunner
{
    private enum Wire { Bytes, Closed, Silent }

    public static X509Certificate2 DeviceCertificate(WifiBenchOffer offer, CngKey key)
    {
        using var leaf = new X509Certificate2(offer.LeafDer);
        using var signer = new ECDsaCng(key);
        return leaf.CopyWithPrivateKey(signer);
    }

    // A certificate for the same device key, but issued by a root the host does not trust.
    public static X509Certificate2 ForeignRootCertificate(CngKey key)
    {
        using var signer = new ECDsaCng(key);
        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var foreignRoot = WifiCertificates.CreateRoot(foreignKey, DateTimeOffset.UtcNow);
        using var leaf = WifiCertificates.Issue(foreignRoot, signer.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        return leaf.CopyWithPrivateKey(signer);
    }

    private static async Task<TcpClient> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var tcp = new TcpClient();
        try { await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false); return tcp; }
        catch { tcp.Dispose(); throw; }
    }

    private static async Task<SslStream> HandshakeAsync(TcpClient tcp, WifiBenchOffer offer, X509Certificate2? client,
        Action<SslClientAuthenticationOptions>? tweak, CancellationToken token)
    {
        using var root = new X509Certificate2(offer.RootDer);
        var options = WifiTls.ClientOptions(root, offer.HostId, client, pairing: false);
        tweak?.Invoke(options);
        var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        try { await ssl.AuthenticateAsClientAsync(options, token).ConfigureAwait(false); return ssl; }
        catch { ssl.Dispose(); throw; }
    }

    private static async Task<Wire> ReadOneAsync(Stream stream, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        try { return await stream.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false) > 0 ? Wire.Bytes : Wire.Closed; }
        catch (OperationCanceledException) { return Wire.Silent; }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or AuthenticationException)
        { return Wire.Closed; }
    }

    private static string Name(Exception error) => error.GetType().Name;

    internal sealed class Established : IAsyncDisposable
    {
        public required MultiplexedTransportSession Session;
        public required Stream Control;
        public required JsonLineConnection Json;
        public string Telemetry = "";
        public async ValueTask DisposeAsync() { await Json.DisposeAsync(); await Session.DisposeAsync(); }
    }

    // Handshake, PLMX, hello/ack and the synthetic telemetry line. Throws if any step is not as specified.
    internal static async Task<Established> EstablishAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device,
        CancellationToken token)
    {
        // The socket belongs to the returned connection: disposing the SslStream/session closes it.
        var tcp = await ConnectAsync(endpoint, token).ConfigureAwait(false);
        try
        {
            var ssl = await HandshakeAsync(tcp, offer, device, null, token).ConfigureAwait(false);
            if (ssl.SslProtocol != SslProtocols.Tls13 || ssl.NegotiatedApplicationProtocol != new SslApplicationProtocol(WifiTls.SessionAlpn) ||
                !ssl.IsMutuallyAuthenticated)
            { ssl.Dispose(); throw new AuthenticationException("TLS profile mismatch."); }
            var session = new MultiplexedTransportSession(ssl, PlmxRole.Accessory, limits: CarrierLimits.WifiV1, log: TextWriter.Null);
            try
            {
                await session.StartAsync(token).ConfigureAwait(false);
                var control = await session.OpenChannelAsync(PanelChannelId.Control, token).ConfigureAwait(false);
                var json = new JsonLineConnection(control);
                await json.WriteAsync(new HelloMessage { DeviceName = "Bench client" }, token).ConfigureAwait(false);
                var ack = PanelJson.Deserialize<HelloAckMessage>(await json.ReadLineAsync(token).ConfigureAwait(false) ?? throw new EndOfStreamException("No ack."));
                if (ack.Features is not { Length: 0 }) throw new InvalidDataException("Host advertised features on the bench.");
                var telemetry = await json.ReadLineAsync(token).ConfigureAwait(false) ?? throw new EndOfStreamException("No telemetry.");
                if (!telemetry.Contains("Synthetic bench", StringComparison.Ordinal)) throw new InvalidDataException("Telemetry is not marked synthetic.");
                return new Established { Session = session, Control = control, Json = json, Telemetry = telemetry };
            }
            catch { await session.DisposeAsync(); throw; }
        }
        catch { tcp.Dispose(); throw; }
    }

    public static async Task<BenchResult> PositiveAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await EstablishAsync(offer, endpoint, device, timeout.Token).ConfigureAwait(false);
            await connection.Json.WriteAsync(new PingMessage(), timeout.Token).ConfigureAwait(false);
            var pong = PanelJson.Deserialize<MessageEnvelope>(await connection.Json.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? "");
            if (pong.Type != MessageTypes.Pong) return new("positive", false, "No pong.");
            await using var video = await connection.Session.OpenChannelAsync(PanelChannelId.Video, timeout.Token).ConfigureAwait(false);
            if (await video.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false) != 0) return new("positive", false, "Video was not refused with FIN.");
            return new("positive", true, "TLS 1.3 mTLS + ALPN, PLMX hello, zero features, synthetic telemetry, pong, Video FIN.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new("positive", false, Name(error)); }
    }

    private static async Task<BenchResult> RefusedAsync(string label, WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2? client,
        Action<SslClientAuthenticationOptions>? tweak, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var tcp = await ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            SslStream ssl;
            try { ssl = await HandshakeAsync(tcp, offer, client, tweak, timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is AuthenticationException or IOException)
            { return new(label, true, "Handshake refused (" + Name(error) + ")."); }
            await using (ssl)
            {
                return await ReadOneAsync(ssl, TimeSpan.FromSeconds(5)).ConfigureAwait(false) switch
                {
                    Wire.Closed => new(label, true, "Server closed before any application byte."),
                    Wire.Bytes => new(label, false, "Server accepted the connection and sent application data."),
                    _ => new(label, false, "Server neither answered nor closed within 5 s."),
                };
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new(label, false, Name(error)); }
    }

    public static Task<BenchResult> RevokedOrUnknownAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, CancellationToken token) =>
        RefusedAsync("revoked-or-unknown-peer", offer, endpoint, device, null, token);
    public static Task<BenchResult> NoCertificateAsync(WifiBenchOffer offer, IPEndPoint endpoint, CancellationToken token) =>
        RefusedAsync("no-certificate", offer, endpoint, null, null, token);
    public static Task<BenchResult> WrongRootAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 foreignLeaf, CancellationToken token) =>
        RefusedAsync("wrong-root", offer, endpoint, foreignLeaf, null, token);
    public static Task<BenchResult> Tls12Async(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, CancellationToken token) =>
        RefusedAsync("tls-1.2", offer, endpoint, device, options => options.EnabledSslProtocols = SslProtocols.Tls12, token);
    public static Task<BenchResult> WrongAlpnAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, CancellationToken token) =>
        RefusedAsync("wrong-alpn", offer, endpoint, device,
            options => options.ApplicationProtocols = [new SslApplicationProtocol(WifiTls.PairAlpn)], token);

    public static async Task<BenchResult> CommandAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, string command,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using var connection = await EstablishAsync(offer, endpoint, device, timeout.Token).ConfigureAwait(false);
            await connection.Json.WriteAsync(new CommandMessage { Command = command }, timeout.Token).ConfigureAwait(false);
            try
            {
                var reply = await connection.Json.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                return reply is null ? new("command-" + command, true, "Session closed without any effect or reply.")
                    : new("command-" + command, false, "Host answered a command that the bench must refuse.");
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or PanelTransportDisconnectedException)
            { return new("command-" + command, true, "Session closed (" + Name(error) + ")."); }
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new("command-" + command, false, Name(error)); }
    }

    // After a valid hello, silence must end the session at about the 15 s heartbeat.
    public static async Task<BenchResult> IdleAsync(WifiBenchOffer offer, IPEndPoint endpoint, X509Certificate2 device, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            await using var connection = await EstablishAsync(offer, endpoint, device, timeout.Token).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            try { await connection.Json.ReadLineAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or PanelTransportDisconnectedException) { }
            return clock.Elapsed is { } elapsed && elapsed > TimeSpan.FromSeconds(13) && elapsed < TimeSpan.FromSeconds(20)
                ? new("heartbeat-idle", true, $"Closed after {elapsed.TotalSeconds:0.0} s of silence.")
                : new("heartbeat-idle", false, $"Closed after {clock.Elapsed.TotalSeconds:0.0} s; expected about 15 s.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new("heartbeat-idle", false, Name(error)); }
    }

    // One partial TLS record byte and then silence: the host's 5 s handshake deadline must close the socket.
    public static async Task<BenchResult> SlowlorisAsync(IPEndPoint endpoint, CancellationToken token)
    {
        try
        {
            using var tcp = await ConnectAsync(endpoint, token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 0x16 }, token).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var wire = await ReadOneAsync(stream, TimeSpan.FromSeconds(9)).ConfigureAwait(false);
            return wire == Wire.Closed && clock.Elapsed < TimeSpan.FromSeconds(8)
                ? new("slowloris", true, $"Host closed the stalled handshake after {clock.Elapsed.TotalSeconds:0.0} s.")
                : new("slowloris", false, $"Result {wire} after {clock.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new("slowloris", false, Name(error)); }
    }

    // Many silent connections at once: at most four may be held, the rest must be closed by admission.
    public static async Task<BenchResult> FloodAsync(IPEndPoint endpoint, int connections, CancellationToken token)
    {
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i < connections; i++) clients.Add(await ConnectAsync(endpoint, token).ConfigureAwait(false));
            await Task.Delay(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
            var closed = clients.Count(c => c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0);
            var held = connections - closed;
            return held <= 4 && closed >= connections - 4
                ? new("flood", true, $"{held} held (limit 4), {closed} closed by admission.")
                : new("flood", false, $"{held} held, {closed} closed; expected at most 4 held.");
        }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { return new("flood", false, Name(error)); }
        finally { foreach (var client in clients) client.Dispose(); }
    }
}
