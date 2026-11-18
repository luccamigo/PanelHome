using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;

namespace PanelHost.Security.Wifi;

internal sealed record WifiPeer(string KeyId, string CertificateHash, string CertificateDer,
    string State, DateTimeOffset Expires, DateTimeOffset IssuedUntil, long Generation, int Capabilities,
    string? Label = null, DateTimeOffset? CreatedAt = null);
internal sealed record WifiIdentity(string RootKey, string ServerKey, string RootDer, string ServerDer,
    WifiPeer[] Peers, int Version = 1, bool WifiEnabled = false, string[]? RevokedKeys = null)
{
    // Never compact these fingerprints with the certificate allowlist.
    public WifiIdentity RememberRevokedKeys(IEnumerable<string> keys) => this with
    { RevokedKeys = (RevokedKeys ?? []).Concat(Peers.Where(p => p.State == "Revoked").Select(p => p.KeyId))
        .Concat(keys).Distinct(StringComparer.Ordinal).ToArray() };
}

internal enum WifiRecoveryScope { None, OnePeer, AllPeers }
internal sealed record WifiRecoveryPlan(WifiRecoveryScope Scope, string? KeyId);

// Constructed only by explicit local actions/tests. Merely loading PanelHost
// neither creates credentials nor enables a transport.
internal sealed class WifiIdentityStore
{
    // The real identity (kept between windows, F4) and every test use DIFFERENT key-name prefixes in the same user key store,
    // so no cleanup, mutation run or parallel test can ever mistake the real keys for test leftovers or the reverse.
    public const string DefaultKeyPrefix = "PanelOS-Wifi-";
    public const string TestKeyPrefix = "PanelOS-WifiTest-";
    private static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PanelOS", "Wifi");
    private readonly string _directory;
    private readonly string _keyPrefix;
    public string KeyPrefix => _keyPrefix;
    private string StatePath => Path.Combine(_directory, "identity.dpapi");
    private string GuardPath => Path.Combine(_directory, "authorization.pending");
    public WifiIdentityStore(string? directory = null, string keyPrefix = DefaultKeyPrefix)
    {
        _directory = directory ?? DefaultDirectory;
        _keyPrefix = keyPrefix;
    }

    public WifiIdentity Create()
    {
        // Only the default directory may create default-prefix keys, and only a non-default directory may create test keys.
        var isDefault = string.Equals(Path.GetFullPath(_directory), Path.GetFullPath(DefaultDirectory), StringComparison.OrdinalIgnoreCase);
        if (isDefault != (_keyPrefix == DefaultKeyPrefix))
            throw new InvalidOperationException("A store outside the default directory must use its own key prefix, and the default directory only the real one.");
        if (File.Exists(StatePath)) throw new InvalidOperationException("WiFi identity already exists.");
        Directory.CreateDirectory(_directory);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new CryptographicException("No Windows account.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(acl);
        var prefix = _keyPrefix + Guid.NewGuid().ToString("N");
        using var rootKey = CreateKey(prefix + "-root", sid);
        try
        {
            using var serverKey = CreateKey(prefix + "-server", sid);
            try
            {
                using var rootSigner = new ECDsaCng(rootKey);
                using var root = WifiCertificates.CreateRoot(rootSigner, DateTimeOffset.UtcNow);
                using var serverSigner = new ECDsaCng(serverKey);
                using var server = WifiCertificates.Issue(root, serverSigner.ExportSubjectPublicKeyInfo(), true, DateTimeOffset.UtcNow);
                var identity = new WifiIdentity(rootKey.KeyName!, serverKey.KeyName!,
                    Convert.ToBase64String(root.RawData), Convert.ToBase64String(server.RawData), []);
                Save(identity);
                return identity;
            }
            catch { serverKey.Delete(); throw; }
        }
        catch { rootKey.Delete(); throw; }
    }

    private static CngKey CreateKey(string name, SecurityIdentifier sid)
    {
        var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.Signing,
        });
        try
        {
            var descriptor = new RawSecurityDescriptor($"O:{sid.Value}G:{sid.Value}D:P(A;;GA;;;{sid.Value})");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            key.SetProperty(new CngProperty("Security Descr", bytes, (CngPropertyOptions)4));
            return key;
        }
        catch { key.Delete(); key.Dispose(); throw; }
    }

    public bool Exists => File.Exists(StatePath);

    public WifiIdentity Load()
    {
        if (new FileInfo(StatePath).Length > 65536) throw new InvalidDataException("WiFi state is too large.");
        var clear = WifiDpapi.Transform(File.ReadAllBytes(StatePath), protect: false);
        try
        {
            var state = JsonSerializer.Deserialize<WifiIdentity>(clear) ?? throw new InvalidDataException("Invalid WiFi state.");
            if (state.Version != 1 || state.WifiEnabled || state.Peers.Length > 8 ||
                state.Peers.Select(peer => peer.KeyId).Distinct().Count() != state.Peers.Length ||
                state.RevokedKeys is { } revoked && (revoked.Distinct().Count() != revoked.Length ||
                    revoked.Any(key => key.Length != 64 || !key.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))))
                throw new InvalidDataException("Unsupported WiFi state.");
            using var root = OpenCertificate(state.RootDer, state.RootKey);
            using var server = OpenCertificate(state.ServerDer, state.ServerKey);
            if (!WifiCertificates.Validate(server, root, WifiCertificates.HostId(root), true))
                throw new CryptographicException("WiFi identity mismatch.");
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(WifiIdentity state)
    {
        if (state.Peers.Length > 8 || state.WifiEnabled) throw new InvalidDataException("Invalid WiFi state.");
        var clear = JsonSerializer.SerializeToUtf8Bytes(state);
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            if (clear.Length > 48000) throw new InvalidDataException("WiFi state is too large.");
            var encrypted = WifiDpapi.Transform(clear, protect: true);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { file.Write(encrypted); file.Flush(true); }
            File.Move(temporary, StatePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private const string OwnerMarker = "owner-v2";
    private const string RevokeMarker = "revoke-v2:";

    // Exclusive owner, with durable intent before modifying any revoked grant.
    public FileStream BeginAuthorization()
    {
        try
        {
            var guard = new FileStream(GuardPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.Read, 1, FileOptions.WriteThrough);
            try { WriteGuard(guard, null); return guard; }
            catch { guard.Dispose(); throw; }
        }
        catch (IOException error)
        {
            // Either another WiFi process owns the store right now, or an unclean exit left the guard behind.
            throw new InvalidDataException("The WiFi store is in use by another process or has a pending guard from an unclean exit; " +
                "stop the other PanelHost, then preview with --wifi-bench-recover.", error);
        }
    }

    public static void WriteGuard(FileStream guard, string? revokingKey)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(revokingKey is null ? OwnerMarker : RevokeMarker + revokingKey);
        guard.Position = 0;
        guard.SetLength(0);
        guard.Write(bytes);
        guard.Flush(true);
    }

    public void EndAuthorization(FileStream guard, bool clean)
    {
        guard.Dispose();
        if (clean) File.Delete(GuardPath);
    }

    // Read-only preview of what RecoverAuthorizationLocally would do. Fails (IOException) while the
    // owning host still has the guard open, so it also tells the operator the owner must stop first.
    public WifiRecoveryPlan PlanRecovery()
    {
        using var guard = new FileStream(GuardPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return Plan(Load(), ReadMarker(guard));
    }

    private static string ReadMarker(FileStream guard) =>
        guard.Length <= 128 ? new StreamReader(guard, leaveOpen: true).ReadToEnd() : "";

    private static WifiRecoveryPlan Plan(WifiIdentity state, string marker)
    {
        if (marker == OwnerMarker) return new(WifiRecoveryScope.None, null);
        var key = marker.StartsWith(RevokeMarker, StringComparison.Ordinal) ? marker[RevokeMarker.Length..] : "";
        var validKey = key.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
        if (validKey && state.Peers.Any(peer => peer.KeyId == key)) return new(WifiRecoveryScope.OnePeer, key);
        // A completed revocation whose peer was later compacted leaves its hash in RevokedKeys: that marker
        // is understood and already effective, so it must not be mistaken for an unreadable one.
        if (validKey && (state.RevokedKeys ?? []).Contains(key)) return new(WifiRecoveryScope.None, key);
        return new(WifiRecoveryScope.AllPeers, null);
    }

    // Explicit local recovery, never an automatic startup fallback. An unreadable
    // or legacy marker cannot identify intent and therefore revokes every grant.
    public WifiRecoveryPlan RecoverAuthorizationLocally()
    {
        WifiRecoveryPlan plan;
        using (var guard = new FileStream(GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var state = Load();
            plan = Plan(state, ReadMarker(guard));
            var recovered = state with { Peers = state.Peers.Select(peer =>
                plan.Scope == WifiRecoveryScope.AllPeers || plan.Scope == WifiRecoveryScope.OnePeer && peer.KeyId == plan.KeyId
                    ? peer with { State = "Revoked", Generation = checked(peer.Generation + 1) } : peer).ToArray() };
            Save(recovered.RememberRevokedKeys([]));
        }
        File.Delete(GuardPath);
        return plan;
    }

    public static X509Certificate2 OpenCertificate(string der, string keyName)
    {
        using var certificate = new X509Certificate2(Convert.FromBase64String(der));
        using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        if (key.IsMachineKey || key.ExportPolicy != CngExportPolicies.None) throw new CryptographicException("Invalid key policy.");
        using var signer = new ECDsaCng(key);
        return certificate.CopyWithPrivateKey(signer);
    }
}
