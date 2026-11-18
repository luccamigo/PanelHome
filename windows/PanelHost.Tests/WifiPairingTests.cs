using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiPairingTests
{
    private static WifiPairingOffer Offer(X509Certificate2? root = null)
    {
        if (root is not null) return WifiPairingOffer.Create(root, "192.168.1.2", 40001, 40002);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var generated = WifiCertificates.CreateRoot(key, DateTimeOffset.UtcNow);
        return Offer(generated);
    }
    [TestMethod]
    public void Qr_roundtrip_is_strict_and_does_not_connect()
    {
        var offer = Offer(); var json = offer.Encode();
        Assert.IsTrue(json.Length <= 1024, $"Actual QR length {json.Length}");
        var read = WifiPairingOffer.Decode(json);
        CollectionAssert.AreEqual(offer.Token, read.Token); CollectionAssert.AreEqual(offer.HostId, read.HostId);
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(new byte[1025]));
        var text = Encoding.UTF8.GetString(json);
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(
            text.Replace(WifiPairingOffer.B64(offer.HostId), WifiPairingOffer.B64(new byte[32])))));
        var corruptRoot = offer.RootDer.ToArray(); corruptRoot[^1] ^= 1;
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(
            text.Replace(WifiPairingOffer.B64(offer.RootDer), WifiPairingOffer.B64(corruptRoot)))));
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(text.Replace("{", "{\"version\":1,"))));
        foreach (var address in new[] { "127.0.0.1", "8.8.8.8", "::1", "https://192.168.1.2", "192.168.001.2" })
            Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Decode(Encoding.UTF8.GetBytes(text.Replace(offer.Ip, address))));
        Assert.ThrowsException<InvalidDataException>(() => WifiPairingOffer.Binary(WifiPairingOffer.B64(offer.Token) + "=", 32));
    }
    [TestMethod]
    public void A_proven_key_consumes_the_invitation_once_and_only_local_approval_issues()
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var root = WifiCertificates.CreateRoot(rootKey, DateTimeOffset.UtcNow);
        var offer = Offer(root);
        using var invitation = new WifiPairingInvitation(offer);
        using var attempt = invitation.BeginAttempt();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var spki = key.ExportSubjectPublicKeyInfo();
        var message = WifiProof.Message(attempt.Challenge, offer.HostId, spki, offer.PairId, offer.Token);
        var signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.IsTrue(attempt.Reserve(offer.PairId, offer.Token, spki, signature, "Synthetic device"));
        Assert.IsFalse(attempt.Reserve(offer.PairId, offer.Token, spki, signature, "Synthetic device"));
        Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt());
    }
    [TestMethod]
    public void Five_failures_cancel_and_old_attempt_cannot_release_a_new_candidate()
    {
        var offer = Offer(); using var invitation = new WifiPairingInvitation(offer);
        var previous = invitation.BeginAttempt();
        Assert.IsFalse(previous.Reserve(offer.PairId, offer.Token, [], [], "test"));
        using (var next = invitation.BeginAttempt())
        {
            previous.Dispose();
            Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt());
            Assert.IsFalse(next.Reserve(offer.PairId, offer.Token, [], [], "test"));
        }
        for (var i = 0; i < 3; i++)
        { using var attempt = invitation.BeginAttempt(); Assert.IsFalse(attempt.Reserve(offer.PairId, offer.Token, [], [], "test")); }
        Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt());
    }
    [TestMethod]
    public void Expiration_is_monotonic_and_challenge_replay_fails()
    {
        var clock = new ManualClock(); var offer = Offer(); using var invitation = new WifiPairingInvitation(offer, clock);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var spki = key.ExportSubjectPublicKeyInfo();
        using var first = invitation.BeginAttempt();
        var signature = key.SignData(WifiProof.Message(first.Challenge, offer.HostId, spki, offer.PairId, offer.Token),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        first.Dispose(); using var second = invitation.BeginAttempt();
        Assert.IsFalse(second.Reserve(offer.PairId, offer.Token, spki, signature, "test"));
        second.Dispose();
        clock.Seconds = WifiPairingInvitation.LifetimeSeconds - 1;
        using (invitation.BeginAttempt()) { }
        clock.Seconds = WifiPairingInvitation.LifetimeSeconds;
        Assert.ThrowsException<InvalidOperationException>(() => invitation.BeginAttempt());
        Assert.AreEqual(600, WifiPairingInvitation.LifetimeSeconds, "Ten minutes, as decided by the owner.");
    }
    [TestMethod]
    public async Task Pairing_prefix_and_duplicate_json_limits_are_enforced_before_body_allocation()
    {
        var oversized = new WifiPairingWire(new MemoryStream(new byte[] { 0, 0, 64, 1 }));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => oversized.ReadAsync(CancellationToken.None));
        var wire = new WifiPairingWire(new MemoryStream());
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => wire.WriteAsync("{\"a\":1,\"a\":2}"u8.ToArray(), CancellationToken.None));
        var good = new WifiPairingWire(new MemoryStream());
        for (var i = 0; i < 4; i++) await good.WriteAsync("{}"u8.ToArray(), CancellationToken.None);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => good.WriteAsync("{}"u8.ToArray(), CancellationToken.None));
    }
    private sealed class ManualClock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
}
