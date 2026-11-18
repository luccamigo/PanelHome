using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PanelHost.Security.Wifi;

internal sealed record AuthorizedWifiPeer(string KeyId, long Generation, int Capabilities, CancellationToken Revoked);

// Distinct from the other refusals so the operator is told "device limit reached" instead of "denied".
internal sealed class WifiPairLimitReachedException() : InvalidOperationException("Pair limit reached.");

internal sealed class WifiPeerRegistry : IDisposable
{
    private readonly WifiIdentityStore _store;
    private WifiIdentity _state;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _sessions = new();
    private bool _failed;
    private bool _disposed;
    private readonly FileStream _guard;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, long> _issued = new();

    public WifiPeerRegistry(WifiIdentityStore store, TimeProvider? clock = null)
    { _store = store; _clock = clock ?? TimeProvider.System; _state = store.Load(); _guard = store.BeginAuthorization(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    // The label is an untrusted display name (already normalised by the pairing flow); it is never a credential.
    public void Issue(X509Certificate2 certificate, int locallyApprovedCapabilities, string? label = null)
    {
        lock (_gate)
        {
            using var root = new X509Certificate2(Convert.FromBase64String(_state.RootDer));
            var issuedAt = _clock.GetTimestamp();
            if (!WifiCertificates.Validate(certificate, root, WifiCertificates.HostId(root), false, _clock.GetUtcNow().UtcDateTime) ||
                locallyApprovedCapabilities is < 1 or > 31)
                throw new InvalidDataException("Invalid local approval.");
            var keyId = Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo());
            var now = _clock.GetUtcNow();
            var retained = _state.Peers.Where(peer => CurrentGrant(peer, now)).ToArray();
            var state = _state.RememberRevokedKeys([]);
            if (retained.Length >= 8) throw new WifiPairLimitReachedException();
            if (retained.Any(peer => peer.KeyId == keyId) || (state.RevokedKeys ?? []).Contains(keyId))
                throw new InvalidOperationException("New pairing key required.");
            Persist(state with { Peers = [.. retained, new WifiPeer(keyId, Hash(certificate.RawData),
                Convert.ToBase64String(certificate.RawData), "Issued", certificate.NotAfter.ToUniversalTime(),
                _clock.GetUtcNow().AddSeconds(60), 1, locallyApprovedCapabilities, label, _clock.GetUtcNow())] });
            foreach (var old in _issued.Keys.Except(retained.Select(peer => peer.KeyId)).ToArray()) _issued.Remove(old);
            _issued[keyId] = issuedAt;
        }
    }

    public bool IsAllowed(X509Certificate2 certificate)
    {
        lock (_gate) return !_failed && Find(certificate) is not null;
    }

    // Read-only views for the operator's device list. Neither exposes certificates or any key material.
    public IReadOnlyList<WifiPeer> Peers() { lock (_gate) return [.. _state.Peers]; }
    public bool IsConnected(string keyId) { lock (_gate) return _sessions.ContainsKey(keyId); }

    // True when this key hash is on the permanent revoked list (or a Revoked row). Used only to report a more
    // precise refusal reason; admission itself still goes through IsAllowed.
    public bool IsRevoked(X509Certificate2 certificate)
    {
        var keyId = Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        lock (_gate) return (_state.RevokedKeys ?? []).Contains(keyId) || _state.Peers.Any(peer => peer.KeyId == keyId && peer.State == "Revoked");
    }

    private WifiPeer? Find(X509Certificate2 certificate)
    {
        var now = _clock.GetUtcNow();
        return _state.Peers.SingleOrDefault(peer => peer.KeyId == Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo()) &&
            !(_state.RevokedKeys ?? []).Contains(peer.KeyId) && peer.CertificateHash == Hash(certificate.RawData) && CurrentGrant(peer, now));
    }

    private bool CurrentGrant(WifiPeer peer, DateTimeOffset now) => peer.Expires > now &&
        (peer.State == "Active" || peer.State == "Issued" && _issued.TryGetValue(peer.KeyId, out var issuedAt) &&
            _clock.GetElapsedTime(issuedAt) < TimeSpan.FromSeconds(60));

    public AuthorizedWifiPeer Activate(X509Certificate2 certificate)
    {
        lock (_gate)
        {
            var peer = !_failed ? Find(certificate) : null;
            if (peer is null || _sessions.Count != 0) throw new InvalidOperationException("Peer unavailable or session already active.");
            if (peer.State == "Issued")
                Persist(_state with { Peers = _state.Peers.Select(item => item == peer ? item with { State = "Active" } : item).ToArray() });
            _issued.Remove(peer.KeyId);
            var cancellation = new CancellationTokenSource();
            _sessions.Add(peer.KeyId, cancellation);
            return new AuthorizedWifiPeer(peer.KeyId, peer.Generation, peer.Capabilities, cancellation.Token);
        }
    }

    public void Release(AuthorizedWifiPeer peer)
    {
        lock (_gate)
            if (_sessions.TryGetValue(peer.KeyId, out var cancellation) && cancellation.Token == peer.Revoked)
            { _sessions.Remove(peer.KeyId); cancellation.Cancel(); cancellation.Dispose(); }
    }

    public void Revoke(string keyId)
    {
        lock (_gate)
        {
            if (!_state.Peers.Any(peer => peer.KeyId == keyId)) throw new InvalidOperationException("Unknown peer.");
            try
            {
                WifiIdentityStore.WriteGuard(_guard, keyId);
                // Close immediately; persistence may be slow. Success is still
                // returned only after the revoked generation reaches disk.
                if (_sessions.TryGetValue(keyId, out var active)) active.Cancel();
                Persist(_state.RememberRevokedKeys([keyId]) with { Peers = _state.Peers.Select(peer => peer.KeyId == keyId
                    ? peer with { State = "Revoked", Generation = checked(peer.Generation + 1) } : peer).ToArray() });
                WifiIdentityStore.WriteGuard(_guard, null);
            }
            catch
            {
                _failed = true;
                foreach (var cancellation in _sessions.Values) cancellation.Cancel();
                throw;
            }
            finally
            {
                if (_sessions.Remove(keyId, out var cancellation)) { cancellation.Cancel(); cancellation.Dispose(); }
                _issued.Remove(keyId);
            }
        }
    }

    private void Persist(WifiIdentity state)
    {
        if (_failed) throw new InvalidOperationException("WiFi storage unavailable.");
        try { _store.Save(state); _state = state; }
        catch
        {
            _failed = true;
            foreach (var cancellation in _sessions.Values) cancellation.Cancel();
            throw;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; var clean = !_failed; _failed = true;
            foreach (var cancellation in _sessions.Values) { cancellation.Cancel(); cancellation.Dispose(); }
            _sessions.Clear();
            _store.EndAuthorization(_guard, clean);
        }
    }
}
