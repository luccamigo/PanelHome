using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiRevokedKeyTests
{
    [TestMethod]
    public void Compaction_and_restart_never_allow_reissuing_a_revoked_key()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var oldKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var old = WifiCertificates.Issue(root, oldKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var reissued = WifiCertificates.Issue(root, oldKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var next = WifiCertificates.Issue(root, newKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using (var registry = new WifiPeerRegistry(fixture.Store))
        {
            registry.Issue(old, 31); var peer = registry.Activate(old); registry.Revoke(peer.KeyId);
            registry.Issue(next, 31);
            Assert.AreEqual(1, fixture.Store.Load().Peers.Length);
            Assert.ThrowsException<InvalidOperationException>(() => registry.Issue(reissued, 31));
        }
        using var restarted = new WifiPeerRegistry(fixture.Store);
        Assert.ThrowsException<InvalidOperationException>(() => restarted.Issue(reissued, 31));
        Assert.IsFalse(restarted.IsAllowed(old));
    }
}
