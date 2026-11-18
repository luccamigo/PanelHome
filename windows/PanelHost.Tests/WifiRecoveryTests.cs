using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiRecoveryTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Killed_owner_recovers_only_an_interrupted_revocation(bool revoking)
    {
        const string directoryVariable = "PANELOS_TEST_WIFI_KILLED_DIRECTORY";
        const string revokeVariable = "PANELOS_TEST_WIFI_KILLED_REVOKE";
        var childDirectory = Environment.GetEnvironmentVariable(directoryVariable);
        if (childDirectory is not null)
        {
            Assert.IsTrue(Path.GetFullPath(childDirectory).StartsWith(
                Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-"), StringComparison.OrdinalIgnoreCase));
            var store = new WifiIdentityStore(childDirectory);
            using var registry = new WifiPeerRegistry(store);
            if (Environment.GetEnvironmentVariable(revokeVariable) == "yes")
            {
                // Simulate a stalled/failed state commit, then kill the actual owner before Dispose.
                var statePath = Path.Combine(childDirectory, "identity.dpapi");
                using var locked = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.ThrowsException<UnauthorizedAccessException>(() => registry.Revoke(store.Load().Peers[0].KeyId));
            }
            File.WriteAllText(Path.Combine(childDirectory, "child.ready"), "ready");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return;
        }
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var keyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var a = WifiCertificates.Issue(root, keyA.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var b = WifiCertificates.Issue(root, keyB.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using (var registry = new WifiPeerRegistry(fixture.Store))
            foreach (var certificate in new[] { a, b })
            { registry.Issue(certificate, 31); registry.Release(registry.Activate(certificate)); }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "windows/PanelHost.Tests/PanelHost.Tests.csproj"))) directory = directory.Parent;
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = directory!.FullName };
        foreach (var arg in new[] { "test", "windows/PanelHost.Tests/PanelHost.Tests.csproj", "--no-build", "--configuration", typeof(WifiCoreTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false).Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration, "--filter",
            "FullyQualifiedName~Killed_owner_recovers_only_an_interrupted_revocation" }) start.ArgumentList.Add(arg);
        start.Environment[directoryVariable] = fixture.Directory;
        start.Environment[revokeVariable] = revoking ? "yes" : "no";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(Path.Combine(fixture.Directory, "child.ready")))
            {
                if (process.HasExited) Assert.Fail(await output + await error);
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        Assert.ThrowsException<InvalidDataException>(() => new WifiPeerRegistry(fixture.Store));
        fixture.Store.RecoverAuthorizationLocally();
        using var recovered = new WifiPeerRegistry(fixture.Store);
        Assert.AreEqual(!revoking, recovered.IsAllowed(a));
        Assert.IsTrue(recovered.IsAllowed(b), "A dirty exit must not revoke unrelated peers.");
    }

    private static string KeyId(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static string GuardPath(WifiCoreTests.IdentityFixture fixture) =>
        Path.Combine(fixture.Directory, "authorization.pending");

    private static string ReadGuard(WifiCoreTests.IdentityFixture fixture)
    {
        using var file = new FileStream(GuardPath(fixture), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new StreamReader(file).ReadToEnd();
    }

    private static X509Certificate2 ActivePeer(WifiPeerRegistry registry, X509Certificate2 root)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        registry.Issue(certificate, 31);
        registry.Release(registry.Activate(certificate));
        return certificate;
    }

    // Protects the WriteGuard(_guard, null) after a completed revocation: without it a later dirty exit
    // looks like an interrupted revocation of a peer that is already gone.
    [TestMethod]
    public void Completed_revocation_returns_the_guard_to_the_owner_marker()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var registry = new WifiPeerRegistry(fixture.Store);
        using var a = ActivePeer(registry, root);
        Assert.AreEqual("owner-v2", ReadGuard(fixture));
        registry.Revoke(KeyId(a));
        Assert.AreEqual("owner-v2", ReadGuard(fixture));
    }

    [TestMethod]
    public void Marker_of_an_already_compacted_revocation_does_not_revoke_every_peer()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        X509Certificate2 a, b, c;
        using (var registry = new WifiPeerRegistry(fixture.Store))
        {
            a = ActivePeer(registry, root); b = ActivePeer(registry, root);
            registry.Revoke(KeyId(a));
            c = ActivePeer(registry, root); // Issue compacts the revoked peer out of Peers.
        }
        using (a) using (b) using (c)
        {
            var state = fixture.Store.Load();
            Assert.IsFalse(state.Peers.Any(peer => peer.KeyId == KeyId(a)), "precondition: compacted");
            CollectionAssert.Contains(state.RevokedKeys, KeyId(a));
            File.WriteAllText(GuardPath(fixture), "revoke-v2:" + KeyId(a));
            Assert.AreEqual(new WifiRecoveryPlan(WifiRecoveryScope.None, KeyId(a)), fixture.Store.PlanRecovery());
            Assert.AreEqual(WifiRecoveryScope.None, fixture.Store.RecoverAuthorizationLocally().Scope);
            using var recovered = new WifiPeerRegistry(fixture.Store);
            Assert.IsTrue(recovered.IsAllowed(b)); Assert.IsTrue(recovered.IsAllowed(c));
            Assert.IsFalse(recovered.IsAllowed(a));
        }
    }

    [TestMethod]
    public void Marker_naming_an_unknown_key_stays_fail_closed_for_every_peer()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        X509Certificate2 a, b;
        using (var registry = new WifiPeerRegistry(fixture.Store)) { a = ActivePeer(registry, root); b = ActivePeer(registry, root); }
        using (a) using (b)
        {
            File.WriteAllText(GuardPath(fixture), "revoke-v2:" + new string('A', 64));
            Assert.AreEqual(WifiRecoveryScope.AllPeers, fixture.Store.PlanRecovery().Scope);
            fixture.Store.RecoverAuthorizationLocally();
            using var recovered = new WifiPeerRegistry(fixture.Store);
            Assert.IsFalse(recovered.IsAllowed(a)); Assert.IsFalse(recovered.IsAllowed(b));
        }
    }

    // Protects RememberRevokedKeys in the recovery save: the hash must outlive compaction of the peer row.
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Recovery_persists_the_revoked_hashes_permanently(bool oneTarget)
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        X509Certificate2 a, b;
        using (var registry = new WifiPeerRegistry(fixture.Store)) { a = ActivePeer(registry, root); b = ActivePeer(registry, root); }
        using (a) using (b)
        {
            File.WriteAllText(GuardPath(fixture), oneTarget ? "revoke-v2:" + KeyId(a) : "broken");
            Assert.AreEqual(oneTarget ? WifiRecoveryScope.OnePeer : WifiRecoveryScope.AllPeers, fixture.Store.RecoverAuthorizationLocally().Scope);
            var revoked = fixture.Store.Load().RevokedKeys;
            CollectionAssert.Contains(revoked, KeyId(a));
            Assert.AreEqual(oneTarget, !revoked!.Contains(KeyId(b)));
            using var registry = new WifiPeerRegistry(fixture.Store);
            using var reuse = WifiCertificates.Issue(root, a.PublicKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
            Assert.ThrowsException<InvalidOperationException>(() => registry.Issue(reuse, 31));
        }
    }

    // Protects `_failed = true` in Revoke's own catch: a failure before Persist (here a generation overflow)
    // must still close every session and refuse everything, not just the target.
    [TestMethod]
    public void Revocation_failure_before_persistence_fails_the_whole_registry_closed()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        X509Certificate2 a, b;
        using (var registry = new WifiPeerRegistry(fixture.Store)) { a = ActivePeer(registry, root); b = ActivePeer(registry, root); }
        using (a) using (b)
        {
            var state = fixture.Store.Load();
            fixture.Store.Save(state with { Peers = state.Peers.Select(peer => peer.KeyId == KeyId(a)
                ? peer with { Generation = long.MaxValue } : peer).ToArray() });
            using var registry = new WifiPeerRegistry(fixture.Store);
            var session = registry.Activate(b);
            Assert.ThrowsException<OverflowException>(() => registry.Revoke(KeyId(a)));
            Assert.IsTrue(session.Revoked.IsCancellationRequested, "Unrelated sessions close on a revocation failure.");
            Assert.IsFalse(registry.IsAllowed(b));
            Assert.ThrowsException<InvalidOperationException>(() => registry.Activate(b));
            using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var fresh = WifiCertificates.Issue(root, newKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
            Assert.ThrowsException<InvalidOperationException>(() => registry.Issue(fresh, 31));
        }
    }

    [TestMethod]
    public void Unreadable_guard_revokes_every_peer_during_explicit_recovery()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using (var registry = new WifiPeerRegistry(fixture.Store))
        { registry.Issue(leaf, 31); registry.Release(registry.Activate(leaf)); }
        File.WriteAllText(Path.Combine(fixture.Directory, "authorization.pending"), "broken");
        Assert.ThrowsException<InvalidDataException>(() => new WifiPeerRegistry(fixture.Store));
        fixture.Store.RecoverAuthorizationLocally();
        using var recovered = new WifiPeerRegistry(fixture.Store);
        Assert.IsFalse(recovered.IsAllowed(leaf));
    }
}
