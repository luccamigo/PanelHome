using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiSecurityRegressionTests
{
    internal sealed class Clock : TimeProvider
    {
        public long Seconds;
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    [TestMethod]
    public void Exact_certificate_hash_and_absolute_expiry_are_independent_registry_gates()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new Clock();
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, clock.Utc);
        using var reissued = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, clock.Utc);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherPeer = WifiCertificates.Issue(root, otherKey.ExportSubjectPublicKeyInfo(), false, clock.Utc);
        var raw = leaf.RawData; raw[^1] ^= 1;
        using var changedBit = new X509Certificate2(raw);
        using var registry = new WifiPeerRegistry(fixture.Store, clock);
        registry.Issue(leaf, 31); var active = registry.Activate(leaf); registry.Release(active);
        Assert.IsTrue(registry.IsAllowed(leaf));
        Assert.IsFalse(registry.IsAllowed(reissued), "Same SPKI with different serial is not the paired certificate.");
        Assert.IsFalse(registry.IsAllowed(changedBit), "One changed certificate bit is not authorized.");
        Assert.IsFalse(registry.IsAllowed(otherPeer), "Another peer is not authorized.");
        clock.Utc = new DateTimeOffset(leaf.NotAfter.ToUniversalTime()).AddSeconds(1);
        Assert.IsFalse(registry.IsAllowed(leaf), "Active grants still expire absolutely.");
    }

    [TestMethod]
    public void Issued_lease_uses_elapsed_time_despite_wall_clock_changes()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clock = new Clock();
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, clock.Utc);
        using var registry = new WifiPeerRegistry(fixture.Store, clock);
        registry.Issue(leaf, 31);
        clock.Seconds = 59; clock.Utc = clock.Utc.AddHours(2);
        Assert.IsTrue(registry.IsAllowed(leaf), "Forward wall clock changes cannot end the 60-second lease early.");
        clock.Seconds = 61; clock.Utc = clock.Utc.AddHours(-2);
        Assert.IsFalse(registry.IsAllowed(leaf), "A wall clock rollback cannot extend the lease.");
    }

    [DataTestMethod]
    [DataRow(59, true)]
    [DataRow(61, false)]
    public void Approval_has_its_own_sixty_second_window(int seconds, bool accepted)
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var offer = WifiPairingOffer.Create(root, "192.168.1.2", 40001, 40002);
        var clock = new Clock();
        using var registry = new WifiPeerRegistry(fixture.Store, clock);
        using var invitation = new WifiPairingInvitation(offer, clock);
        using var attempt = invitation.BeginAttempt();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = key.ExportSubjectPublicKeyInfo();
        var signature = key.SignData(WifiProof.Message(attempt.Challenge, offer.HostId, spki, offer.PairId, offer.Token),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(attempt.Reserve(offer.PairId, offer.Token, spki, signature, "Device"));
        clock.Seconds = seconds;
        if (accepted) { using var leaf = invitation.ApproveLocally(root, registry, 31); Assert.IsTrue(registry.IsAllowed(leaf)); }
        else Assert.AreEqual("pairing_failed", Assert.ThrowsException<InvalidOperationException>(
            () => invitation.ApproveLocally(root, registry, 31)).Message);
    }

    [TestMethod]
    public void Tls_requires_chain_and_registry_independently_and_disables_resumption()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        using var server = WifiIdentityStore.OpenCertificate(fixture.State.ServerDer, fixture.State.ServerKey);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var leaf = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        using var registry = new WifiPeerRegistry(fixture.Store); registry.Issue(leaf, 31);
        var options = WifiTls.ServerOptions(server, root, registry, false);
        Assert.IsFalse(options.AllowTlsResume);
        Assert.IsFalse(WifiTls.ClientOptions(root, WifiCertificates.HostId(root), leaf, false).AllowTlsResume);
        Assert.IsFalse(WifiTls.ClientOptions(root, WifiCertificates.HostId(root), null, true).AllowTlsResume);
        Assert.IsTrue(options.RemoteCertificateValidationCallback!(this, leaf, null, SslPolicyErrors.None));
        Assert.IsFalse(options.RemoteCertificateValidationCallback(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
        using var unregisteredKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var unregistered = WifiCertificates.Issue(root, unregisteredKey.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        Assert.IsFalse(options.RemoteCertificateValidationCallback(this, unregistered, null, SslPolicyErrors.None));
        using var rootKeyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rootB = WifiCertificates.CreateRoot(rootKeyB, DateTimeOffset.UtcNow);
        var wrongAnchor = WifiTls.ServerOptions(server, rootB, registry, false);
        Assert.IsTrue(registry.IsAllowed(leaf), "Registry permits the exact leaf independently of the TLS anchor.");
        Assert.IsFalse(wrongAnchor.RemoteCertificateValidationCallback!(this, leaf, null, SslPolicyErrors.None), "TLS must independently reject another anchor.");
    }

    [TestMethod]
    public void Labels_reject_bidi_controls_and_invalid_unicode_and_normalize_nfc()
    {
        foreach (var label in new[] { "x\u202ey", "x\u2066y", "x\u200fy", "x\ny", "x\u2028y", "\ud800", "", new string('x',65) })
            Assert.IsNull(WifiPairingInvitation.NormalizeLabel(label));
        Assert.AreEqual("Café 📱", WifiPairingInvitation.NormalizeLabel("Cafe\u0301 📱"));
    }

    [TestMethod]
    public void Five_abandoned_attempts_close_invitation_and_dispose_is_idempotent()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var root = WifiCertificates.CreateRoot(key, DateTimeOffset.UtcNow);
        using var offer = WifiPairingOffer.Create(root,"192.168.1.2",40001,40002);
        using var invitation = new WifiPairingInvitation(offer);
        for (var i=0;i<5;i++) { var attempt=invitation.BeginAttempt(); attempt.Dispose(); attempt.Dispose(); }
        Assert.AreEqual("pairing_failed", Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt()).Message);
    }

    [TestMethod]
    public void Every_malformed_qr_exposes_one_error_type_and_message()
    {
        foreach (var data in new byte[][] { [], "[]"u8.ToArray(), "null"u8.ToArray(), "{"u8.ToArray(), [0xff], new byte[1025], null! })
            Assert.AreEqual("Invalid pairing QR.", Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(data)).Message);
    }
}
