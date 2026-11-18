using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace PanelHost.Security.Wifi;

// Why a mutual-TLS client was refused. Reported to an optional local sink only (counters); never logged with identity.
internal enum WifiTlsRefusal { NoCertificate, InvalidCertificate, Revoked, NotAuthorized }

internal static class WifiTls
{
    public const string SessionAlpn = "panelos-session/1";
    public const string PairAlpn = "panelos-pair/1";

    public static void RequireHost(bool enabled, bool windows11, bool elevated)
    {
        if (!enabled || !windows11 || elevated) throw new InvalidOperationException("WiFi requires opt-in, Windows 11+ and a non-elevated host.");
    }

    // No bind/connect operation is part of this core. The caller supplies an
    // already established stream and must pass all future LAN admission gates.
    public static SslServerAuthenticationOptions ServerOptions(X509Certificate2 server,
        X509Certificate2 root, WifiPeerRegistry? peers, bool pairing, Action<WifiTlsRefusal>? refused = null)
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = server,
            ClientCertificateRequired = !pairing,
            EnabledSslProtocols = SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            CertificateChainPolicy = Policy(root, false),
            ApplicationProtocols = [new(pairing ? PairAlpn : SessionAlpn)],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (pairing) return true;
                if (certificate is not X509Certificate2 client || peers is null) { refused?.Invoke(WifiTlsRefusal.NoCertificate); return false; }
                if (!WifiCertificates.Validate(client, root, WifiCertificates.HostId(root), false))
                { refused?.Invoke(WifiTlsRefusal.InvalidCertificate); return false; }
                if (peers.IsAllowed(client)) return true;
                refused?.Invoke(peers.IsRevoked(client) ? WifiTlsRefusal.Revoked : WifiTlsRefusal.NotAuthorized);
                return false;
            },
        };
    }

    public static SslClientAuthenticationOptions ClientOptions(X509Certificate2 root,
        byte[] hostId, X509Certificate2? client, bool pairing) => new()
    {
        TargetHost = WifiCertificates.ServerName(root),
        EnabledSslProtocols = SslProtocols.Tls13,
        AllowRenegotiation = false,
        AllowTlsResume = false,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        CertificateChainPolicy = Policy(root, true),
        ClientCertificates = client is null ? null : new X509CertificateCollection { client },
        ApplicationProtocols = [new(pairing ? PairAlpn : SessionAlpn)],
        RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 server &&
            WifiCertificates.Validate(server, root, hostId, true),
    };

    private static X509ChainPolicy Policy(X509Certificate2? root, bool server)
    {
        var policy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true };
        if (root is not null) policy.CustomTrustStore.Add(root);
        policy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid(server ? WifiCertificates.ServerEku : WifiCertificates.ClientEku));
        return policy;
    }

    // The QR supplies the public root DER and its full SPKI hash. Neither
    // endpoint installs it in an OS store or relies on a root sent by TLS.
    public static SslClientAuthenticationOptions PairClientOptions(WifiPairingOffer offer)
    {
        using var root = new X509Certificate2(offer.RootDer);
        if (!WifiCertificates.ValidateRoot(root, offer.HostId)) throw new InvalidDataException("Invalid host pin/root.");
        // Options hold their own certificate instance for the handshake lifetime.
        var ownedRoot = new X509Certificate2(root);
        return ClientOptions(ownedRoot, offer.HostId.ToArray(), null, true);
    }

    public static void CheckNegotiated(SslStream stream, bool pairing)
    {
        if (!stream.IsAuthenticated || !stream.IsEncrypted || stream.SslProtocol != SslProtocols.Tls13 ||
            stream.NegotiatedApplicationProtocol != new SslApplicationProtocol(pairing ? PairAlpn : SessionAlpn) ||
            !pairing && (!stream.IsMutuallyAuthenticated || stream.RemoteCertificate is null))
            throw new AuthenticationException("WiFi TLS profile was not negotiated.");
    }

    public static async Task<AuthorizedWifiConnection> AuthenticateSessionAsync(Stream physical,
        X509Certificate2 server, X509Certificate2 root, WifiPeerRegistry peers, bool locallyEnabled,
        CancellationToken cancellationToken, Action<WifiTlsRefusal>? refused = null)
    {
        using var identity = WindowsIdentity.GetCurrent();
        RequireHost(locallyEnabled, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
        var tls = new SslStream(physical, leaveInnerStreamOpen: false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await tls.AuthenticateAsServerAsync(ServerOptions(server, root, peers, false, refused), deadline.Token);
            CheckNegotiated(tls, false);
            using var certificate = new X509Certificate2(tls.RemoteCertificate!);
            var authorized = peers.Activate(certificate);
            return new AuthorizedWifiConnection(tls, peers, authorized);
        }
        catch { await tls.DisposeAsync(); throw; }
    }
}

internal sealed class AuthorizedWifiConnection : IDisposable
{
    private readonly SslStream _stream;
    private readonly WifiPeerRegistry _registry;
    private readonly CancellationTokenRegistration _revocation;
    public AuthorizedWifiPeer Peer { get; }
    public Stream Stream { get { Peer.Revoked.ThrowIfCancellationRequested(); return _stream; } }
    internal AuthorizedWifiConnection(SslStream stream, WifiPeerRegistry registry, AuthorizedWifiPeer peer)
    {
        _stream = stream; _registry = registry; Peer = peer;
        _revocation = peer.Revoked.Register(stream.Dispose);
    }
    public void Dispose() { _revocation.Dispose(); _stream.Dispose(); _registry.Release(Peer); }
}
