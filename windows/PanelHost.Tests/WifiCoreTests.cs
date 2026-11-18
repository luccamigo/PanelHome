using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiCoreTests
{
    [TestMethod]
    public void Proof_binds_every_byte_and_rejects_other_curves_and_trailing_der()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var message = WifiProof.Message(new byte[32], new byte[32], spki, new byte[16], new byte[32]);
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(WifiProof.Verify(spki, message, signature));
        for (var index = 0; index < message.Length; index++)
        {
            var changed = message.ToArray(); changed[index] ^= 1;
            Assert.IsFalse(WifiProof.Verify(spki, changed, signature), $"mutation {index}");
        }
        Assert.IsFalse(WifiProof.Verify(spki, message, [.. signature, 0]));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.ThrowsException<InvalidDataException>(() => WifiProof.ReadKey(other.ExportSubjectPublicKeyInfo()));
        Assert.ThrowsException<InvalidDataException>(() => WifiProof.ReadKey([.. spki, 0]));
    }

    [TestMethod]
    public void Cng_dpapi_storage_is_user_scoped_nonexportable_and_corruption_fails_closed()
    {
        using var fixture = new IdentityFixture();
        var state = fixture.Store.Load();
        Assert.IsFalse(state.WifiEnabled);
        Assert.AreEqual(fixture.State.RootKey, state.RootKey);
        using var key = CngKey.Open(state.RootKey, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        Assert.IsFalse(key.IsMachineKey);
        Assert.AreEqual(CngExportPolicies.None, key.ExportPolicy);
        var descriptor = new System.Security.AccessControl.RawSecurityDescriptor(
            key.GetProperty("Security Descr", (CngPropertyOptions)4).GetValue(), 0);
        Assert.AreEqual(1, descriptor.DiscretionaryAcl!.Count);
        Assert.AreEqual(System.Security.Principal.WindowsIdentity.GetCurrent().User,
            ((System.Security.AccessControl.CommonAce)descriptor.DiscretionaryAcl[0]).SecurityIdentifier);
        Assert.ThrowsException<CryptographicException>(() => key.Export(CngKeyBlobFormat.EccPrivateBlob));
        using var server = WifiIdentityStore.OpenCertificate(state.ServerDer, state.ServerKey);
        using var root = new X509Certificate2(Convert.FromBase64String(state.RootDer));
        Assert.IsTrue(WifiCertificates.Validate(server, root, WifiCertificates.HostId(root), true));
        Assert.IsFalse(WifiCertificates.Validate(server, root, new byte[32], true));
        Assert.IsFalse(WifiCertificates.Validate(server, root, WifiCertificates.HostId(root), false));
        var path = Path.Combine(fixture.Directory, "identity.dpapi");
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.ThrowsException<CryptographicException>(() => fixture.Store.Load());
        Assert.ThrowsException<InvalidOperationException>(() => fixture.Store.Create());
    }

    [TestMethod]
    public async Task Schannel_tls13_mtls_and_alpn_work_over_memory_without_listeners()
    {
        ElevatedRunnerGuard.RequireNonElevatedProcess();
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(fixture.State.ServerDer, fixture.State.ServerKey);
        using var persisted = fixture.CreateDeviceKey();
        using var clientKey = new ECDsaCng(persisted);
        using var registry = new WifiPeerRegistry(fixture.Store);
        using var offer = WifiPairingOffer.Create(root, "192.168.1.2", 40001, 40002);
        using var invitation = new WifiPairingInvitation(offer); using var attempt = invitation.BeginAttempt();
        var spki = clientKey.ExportSubjectPublicKeyInfo();
        var proof = clientKey.SignData(WifiProof.Message(attempt.Challenge, offer.HostId, spki, offer.PairId, offer.Token),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(attempt.Reserve(offer.PairId, offer.Token, spki, proof, "Synthetic device"));
        Assert.AreEqual(0, fixture.Store.Load().Peers.Length);
        using var leaf = invitation.ApproveLocally(root, registry, 31);
        using var client = leaf.CopyWithPrivateKey(clientKey);
        Assert.AreEqual("Issued", fixture.Store.Load().Peers.Single().State);
        Assert.IsTrue(registry.IsAllowed(client));
        var (left, right) = MemoryDuplex.CreatePair();
        using var device = new SslStream(right);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hostTask = WifiTls.AuthenticateSessionAsync(left, server, root, registry, true, deadline.Token);
        await Task.WhenAll(hostTask, device.AuthenticateAsClientAsync(
            WifiTls.ClientOptions(root, WifiCertificates.HostId(root), client, false), deadline.Token));
        WifiTls.CheckNegotiated(device, false);
        using var connection = await hostTask;
        var authorized = connection.Peer;
        Assert.IsTrue(fixture.Store.Load().Peers.Single().State == "Active");
        registry.Revoke(authorized.KeyId);
        Assert.IsTrue(authorized.Revoked.IsCancellationRequested);
        Assert.IsFalse(registry.IsAllowed(client));
        Assert.ThrowsException<OperationCanceledException>(() => _ = connection.Stream);
        registry.Dispose();
        using var restarted = new WifiPeerRegistry(fixture.Store);
        Assert.IsFalse(restarted.IsAllowed(client));
    }

    [TestMethod]
    public async Task Pairing_tls_uses_only_the_qr_root_without_modifying_os_ca_stores()
    {
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(fixture.State.ServerDer, fixture.State.ServerKey);
        var (left, right) = MemoryDuplex.CreatePair();
        using var host = new SslStream(left); using var device = new SslStream(right);
        using var offer = WifiPairingOffer.Create(root, "192.168.1.2", 40001, 40002);
        Console.WriteLine($"Measured QR UTF-8 bytes: {offer.Encode().Length}; public root DER bytes: {root.RawData.Length}");
        Assert.IsTrue(offer.Encode().Length <= 1024);
        var options = WifiTls.PairClientOptions(offer);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.AuthenticateAsServerAsync(WifiTls.ServerOptions(server, root, null, true), deadline.Token),
            device.AuthenticateAsClientAsync(options, deadline.Token));
        WifiTls.CheckNegotiated(host, true); WifiTls.CheckNegotiated(device, true);
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.CertificateAuthority, location); store.Open(OpenFlags.ReadOnly);
            Assert.AreEqual(0, store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false).Count,
                $"{location} CA store must remain unchanged");
        }
    }

    [TestMethod]
    public void Shared_java_fixture_has_identical_transcript_and_valid_der_signature()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "protocol-fixtures", "wifi", "proof-v1.json"))) directory = directory.Parent;
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory!.FullName, "protocol-fixtures", "wifi", "proof-v1.json")));
        byte[] Field(string name) => Convert.FromHexString(fixture.RootElement.GetProperty(name).GetString()!);
        var message = WifiProof.Message(Field("challenge"), Field("hostId"), Field("spki"), Field("pairId"), Field("token"));
        CollectionAssert.AreEqual(Field("message"), message);
        Assert.IsTrue(WifiProof.Verify(Field("spki"), message, Field("signature")));
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("unregistered")]
    [DataRow("alpn")]
    [DataRow("tls12")]
    [DataRow("wrong-root")]
    [DataRow("copied-public-cert")]
    public async Task Tls_negatives_never_authenticate_an_authorized_session(string scenario)
    {
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(fixture.State.ServerDer, fixture.State.ServerKey);
        using var persisted = fixture.CreateDeviceKey(); using var key = new ECDsaCng(persisted);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var client = leaf.CopyWithPrivateKey(key);
        using var registry = new WifiPeerRegistry(fixture.Store);
        if (scenario != "unregistered") registry.Issue(client, 31);
        var (left, right) = MemoryDuplex.CreatePair();
        using var host = new SslStream(left); using var device = new SslStream(right);
        var clientOptions = WifiTls.ClientOptions(root, scenario == "wrong-root" ? new byte[32] : WifiCertificates.HostId(root),
            scenario == "missing" ? null : scenario == "copied-public-cert" ? leaf : client, false);
        if (scenario == "alpn") clientOptions.ApplicationProtocols = [new("different/1")];
        if (scenario == "tls12") clientOptions.EnabledSslProtocols = SslProtocols.Tls12;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var a = host.AuthenticateAsServerAsync(WifiTls.ServerOptions(server, root, registry, false), deadline.Token);
        var b = device.AuthenticateAsClientAsync(clientOptions, deadline.Token);
        var first = await Task.WhenAny(a, b);
        if (first.IsFaulted || first.IsCanceled)
        {
            // Let the peer surface its own verdict before tearing the pipe down.
            await Task.WhenAny(Task.WhenAll(a, b).ContinueWith(_ => { }), Task.Delay(500));
            host.Dispose(); device.Dispose();
        }
        try { await Task.WhenAll(a, b); } catch { }
        var causes = new[] { a, b }.Where(task => task.IsFaulted).Select(task => task.Exception!.GetBaseException()).ToArray();
        var rejected = false;
        try { await Task.WhenAll(a, b); WifiTls.CheckNegotiated(host, false); WifiTls.CheckNegotiated(device, false); }
        catch (Exception error) when (error is AuthenticationException or IOException or OperationCanceledException or ObjectDisposedException)
        { rejected = true; }
        Assert.IsTrue(rejected, scenario);
        Assert.IsTrue(causes.Any(cause => cause is AuthenticationException),
            $"{scenario}: {string.Join(" | ", causes.Select(cause => cause.GetType().Name + ": " + cause.Message))}");
        var peers = fixture.Store.Load().Peers;
        Assert.AreEqual(scenario == "unregistered" ? 0 : 1, peers.Length);
        if (peers.Length == 1) Assert.AreEqual("Issued", peers[0].State);
    }

    [TestMethod]
    public void Invalid_root_signature_expired_and_future_certificates_are_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var root = WifiCertificates.CreateRoot(key, DateTimeOffset.UtcNow.AddYears(-2));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var valid = WifiCertificates.Issue(root, serverKey.ExportSubjectPublicKeyInfo(), true, DateTimeOffset.UtcNow);
        var corrupt = root.RawData; corrupt[^1] ^= 1; using var changedRoot = new X509Certificate2(corrupt);
        Assert.IsFalse(WifiCertificates.Validate(valid, changedRoot, WifiCertificates.HostId(root), true));
        using var expired = WifiCertificates.Issue(root, serverKey.ExportSubjectPublicKeyInfo(), true, DateTimeOffset.UtcNow.AddYears(-1).AddDays(-2));
        using var future = WifiCertificates.Issue(root, serverKey.ExportSubjectPublicKeyInfo(), true, DateTimeOffset.UtcNow.AddDays(1));
        Assert.IsFalse(WifiCertificates.Validate(expired, root, WifiCertificates.HostId(root), true));
        Assert.IsFalse(WifiCertificates.Validate(future, root, WifiCertificates.HostId(root), true));
    }

    [TestMethod]
    public void Revocation_storage_failure_cancels_and_poisoned_registry_never_authorizes()
    {
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var registry = new WifiPeerRegistry(fixture.Store); registry.Issue(leaf, 31);
        var authorized = registry.Activate(leaf);
        var statePath = Path.Combine(fixture.Directory, "identity.dpapi");
        File.SetAttributes(statePath, FileAttributes.ReadOnly);
        try
        {
            var failed = false;
            try { registry.Revoke(authorized.KeyId); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Assert.IsTrue(failed); Assert.IsTrue(authorized.Revoked.IsCancellationRequested);
            Assert.IsFalse(registry.IsAllowed(leaf));
        }
        finally { File.SetAttributes(statePath, FileAttributes.Normal); registry.Release(authorized); }
    }

    [TestMethod]
    public void Issued_expiration_and_simultaneous_sessions_are_refused()
    {
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var registry = new WifiPeerRegistry(fixture.Store); registry.Issue(leaf, 31);
        var state = fixture.Store.Load();
        fixture.Store.Save(state with { Peers = [state.Peers[0] with { IssuedUntil = DateTimeOffset.UtcNow.AddSeconds(-1) }] });
        registry.Dispose();
        using (var expired = new WifiPeerRegistry(fixture.Store)) Assert.IsFalse(expired.IsAllowed(leaf));
        fixture.Store.Save(state);
        using var active = new WifiPeerRegistry(fixture.Store);
        Assert.IsFalse(active.IsAllowed(leaf), "Issued leases cannot survive a process restart.");
        active.Issue(leaf, 31);
        var peer = active.Activate(leaf);
        Assert.ThrowsException<InvalidOperationException>(() => active.Activate(leaf));
        active.Release(peer);
        var next = active.Activate(leaf);
        active.Release(peer); Assert.IsFalse(next.Revoked.IsCancellationRequested); active.Release(next);
    }

    [TestMethod]
    public async Task Persistent_guard_blocks_a_fresh_test_process_after_failed_revocation()
    {
        const string variable = "PANELOS_TEST_WIFI_RESTART_DIRECTORY";
        var childDirectory = Environment.GetEnvironmentVariable(variable);
        if (childDirectory is not null)
        {
            var path = Path.GetFullPath(childDirectory);
            Assert.IsTrue(path.StartsWith(Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-"), StringComparison.OrdinalIgnoreCase));
            Assert.ThrowsException<InvalidDataException>(() => new WifiPeerRegistry(new WifiIdentityStore(path)));
            return;
        }
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using (var registry = new WifiPeerRegistry(fixture.Store))
        {
            registry.Issue(leaf, 31); var peer = registry.Activate(leaf);
            var statePath = Path.Combine(fixture.Directory, "identity.dpapi");
            File.SetAttributes(statePath, FileAttributes.ReadOnly);
            try
            {
                Assert.ThrowsException<UnauthorizedAccessException>(() => registry.Revoke(peer.KeyId));
                Assert.IsTrue(peer.Revoked.IsCancellationRequested);
            }
            finally { File.SetAttributes(statePath, FileAttributes.Normal); }
        }
        Assert.AreEqual("Active", fixture.Store.Load().Peers.Single().State);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "windows/PanelHost.Tests/PanelHost.Tests.csproj"))) directory = directory.Parent;
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
          WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, WorkingDirectory = directory!.FullName };
        foreach (var argument in new[] { "test", "windows/PanelHost.Tests/PanelHost.Tests.csproj", "--no-build", "--configuration", typeof(WifiCoreTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false).Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration, "--filter",
            "FullyQualifiedName~Persistent_guard_blocks_a_fresh_test_process_after_failed_revocation" }) start.ArgumentList.Add(argument);
        start.Environment[variable] = fixture.Directory;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.AreEqual(0, process.ExitCode, await output + await error);
        fixture.Store.RecoverAuthorizationLocally();
        using var recovered = new WifiPeerRegistry(fixture.Store);
        Assert.IsFalse(recovered.IsAllowed(leaf));
    }

    [TestMethod]
    public void Eight_live_pairs_are_the_limit_but_revoked_records_compact_without_reauthorizing_old_hashes()
    {
        using var fixture = new IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var registry = new WifiPeerRegistry(fixture.Store);
        var certificates = new List<X509Certificate2>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
                certificates.Add(leaf); registry.Issue(leaf, 31);
                var peer = registry.Activate(leaf); registry.Release(peer);
            }
            using var ninthKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var ninth = WifiCertificates.Issue(root, ninthKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
            Assert.ThrowsException<WifiPairLimitReachedException>(() => registry.Issue(ninth, 31));
            foreach (var peer in fixture.Store.Load().Peers) registry.Revoke(peer.KeyId);
            registry.Issue(ninth, 31);
            Assert.AreEqual(1, fixture.Store.Load().Peers.Length);
            foreach (var certificate in certificates) Assert.IsFalse(registry.IsAllowed(certificate));
            registry.Dispose();
            using var restarted = new WifiPeerRegistry(fixture.Store);
            foreach (var certificate in certificates) Assert.IsFalse(restarted.IsAllowed(certificate));
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    [TestMethod]
    public void Default_old_windows_and_elevation_refuse_wifi()
    {
        Assert.ThrowsException<InvalidOperationException>(() => WifiTls.RequireHost(false, true, false));
        Assert.ThrowsException<InvalidOperationException>(() => WifiTls.RequireHost(true, false, false));
        Assert.ThrowsException<InvalidOperationException>(() => WifiTls.RequireHost(true, true, true));
        WifiTls.RequireHost(true, true, false);
    }

    internal sealed class IdentityFixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-" + Guid.NewGuid().ToString("N"));
        public WifiIdentityStore Store { get; }
        public WifiIdentity State { get; }
        private readonly List<string> _deviceKeys = [];
        public IdentityFixture() { Store = new(Directory, WifiIdentityStore.TestKeyPrefix); State = Store.Create(); }
        public CngKey CreateDeviceKey()
        {
            var name = WifiIdentityStore.TestKeyPrefix + Guid.NewGuid().ToString("N");
            var key = CngKey.Create(CngAlgorithm.ECDsaP256, name,
                new CngKeyCreationParameters { ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing });
            _deviceKeys.Add(name); return key;
        }
        public void Dispose()
        {
            foreach (var name in new[] { State.RootKey, State.ServerKey }.Concat(_deviceKeys))
            { using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider); key.Delete(); }
            System.IO.Directory.Delete(Directory, true);
        }
    }

    internal sealed class MemoryDuplex(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
    {
        private byte[]? _current; private int _offset;
        public static (MemoryDuplex, MemoryDuplex) CreatePair()
        {
            var a = Channel.CreateBounded<byte[]>(16); var b = Channel.CreateBounded<byte[]>(16);
            return (new(a, b), new(b, a));
        }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            { if (!await incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
              if (!incoming.Reader.TryRead(out _current)) continue; _offset = 0; }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            outgoing.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        public override int Read(byte[] b, int o, int n) => ReadAsync(b.AsMemory(o, n)).AsTask().GetAwaiter().GetResult();
        public override void Write(byte[] b, int o, int n) => WriteAsync(b.AsMemory(o, n)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long l) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { outgoing.Writer.TryComplete(); incoming.Writer.TryComplete(); } base.Dispose(disposing); }
    }
}
