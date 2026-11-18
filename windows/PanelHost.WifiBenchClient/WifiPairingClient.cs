using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PanelHost.Security.Wifi;

namespace PanelHost.WifiBenchClient;

// A reference .NET device for the pairing protocol, used by the suite to prove the PC side against real sockets
// (the Android app is a separate, independent implementation of the same wire). It never listens.
internal static class WifiPairingClient
{
    private static byte[] Json(object message) => JsonSerializer.SerializeToUtf8Bytes(message);

    private static byte[] Decode(string text, int exact = -1)
    {
        var bytes = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + new string('=', (4 - text.Length % 4) % 4));
        if (WifiPairingOffer.B64(bytes) != text || exact >= 0 && bytes.Length != exact) throw new InvalidDataException("pairing_failed");
        return bytes;
    }

    // QR mode: the root came in the QR, so the TLS server certificate is validated during the handshake.
    public static async Task<X509Certificate2> PairWithOfferAsync(WifiPairingOffer offer, IPEndPoint endpoint, ECDsa deviceKey,
        string label, CancellationToken token, TimeSpan? approvalWait = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(WifiTls.PairClientOptions(offer), token).ConfigureAwait(false);
        WifiTls.CheckNegotiated(ssl, pairing: true);
        return await ExchangeAfterRootAsync(new WifiPairingWire(ssl), offer.HostId, offer.RootDer, offer.PairId, offer.Token, deviceKey,
            label, token, approvalWait).ConfigureAwait(false);
    }

    // PIN mode: only the host id was typed. The handshake is allowed to complete with whatever certificate the server
    // shows, nothing secret has been sent yet, and the certificate is then validated against the root the host
    // sends next, which must hash to the typed host id. A failure closes the connection before the PairRequest.
    public static async Task<X509Certificate2> PairWithCodeAsync(WifiTypedCodeFields code, IPEndPoint endpoint, ECDsa deviceKey,
        string label, CancellationToken token, TimeSpan? approvalWait = null)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false);
        await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        X509Certificate2? presented = null;
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = WifiCertificates.ServerName(code.HostId),
            EnabledSslProtocols = SslProtocols.Tls13,
            AllowRenegotiation = false, AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            ApplicationProtocols = [new(WifiTls.PairAlpn)],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            { presented = certificate is null ? null : new X509Certificate2(certificate); return presented is not null; },
        }, token).ConfigureAwait(false);
        WifiTls.CheckNegotiated(ssl, pairing: true);
        var wire = new WifiPairingWire(ssl);
        byte[] rootDer;
        using (var hostRoot = await wire.ReadAsync(token).ConfigureAwait(false))
        {
            if (hostRoot.RootElement.GetProperty("type").GetString() != "host_root") throw new InvalidDataException("pairing_failed");
            rootDer = Decode(hostRoot.RootElement.GetProperty("rootCa").GetString()!);
        }
        using var root = new X509Certificate2(rootDer);
        if (!WifiCertificates.ValidateRoot(root, code.HostId) || presented is null ||
            !WifiCertificates.Validate(presented, root, code.HostId, server: true))
            throw new AuthenticationException("The PC does not match the typed host id.");
        return await ExchangeAfterRootAsync(wire, code.HostId, rootDer, code.PairId, WifiPairingOffer.TokenFromPin(code.Pin), deviceKey, label,
            token, approvalWait).ConfigureAwait(false);
    }

    private static async Task<X509Certificate2> ExchangeAfterRootAsync(WifiPairingWire wire, byte[] hostId, byte[] rootDer, byte[] pairId,
        byte[] pairToken, ECDsa deviceKey, string label, CancellationToken token, TimeSpan? approvalWait)
    {
        byte[] challenge;
        using (var message = await wire.ReadAsync(token).ConfigureAwait(false))
        {
            if (message.RootElement.GetProperty("type").GetString() != "challenge") throw new InvalidDataException("pairing_failed");
            challenge = Decode(message.RootElement.GetProperty("challenge").GetString()!, 32);
        }
        var spki = deviceKey.ExportSubjectPublicKeyInfo();
        var signature = deviceKey.SignData(WifiProof.Message(challenge, hostId, spki, pairId, pairToken), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        await wire.WriteAsync(Json(new { type = "pair_request", pairId = WifiPairingOffer.B64(pairId), token = WifiPairingOffer.B64(pairToken),
            spki = WifiPairingOffer.B64(spki), label, signature = WifiPairingOffer.B64(signature) }), token).ConfigureAwait(false);
        using var result = await wire.ReadAsync(token, approvalWait ?? TimeSpan.FromSeconds(70)).ConfigureAwait(false);
        var status = result.RootElement.GetProperty("status").GetString();
        if (status != "approved") throw new InvalidDataException(status ?? "pairing_failed");
        if (!Decode(result.RootElement.GetProperty("rootCa").GetString()!).AsSpan().SequenceEqual(rootDer)) throw new InvalidDataException("pairing_failed");
        var leaf = new X509Certificate2(Decode(result.RootElement.GetProperty("certificate").GetString()!));
        using var root = new X509Certificate2(rootDer);
        if (!leaf.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(spki) || !WifiCertificates.Validate(leaf, root, hostId, server: false))
        { leaf.Dispose(); throw new InvalidDataException("pairing_failed"); }
        return leaf;
    }
}
