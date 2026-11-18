using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Security.Wifi;
using PanelHost.WifiBenchClient;

namespace PanelHost.Tests;

// Argument handling of the bench client; opens no socket.
[TestClass]
public sealed class WifiBenchClientTests
{
    private static WifiBenchOffer Offer(WifiCoreTests.IdentityFixture fixture, out CngKey key)
    {
        key = fixture.CreateDeviceKey();
        using var signer = new ECDsaCng(key);
        var spki = signer.ExportSubjectPublicKeyInfo();
        var spkiPath = Path.Combine(fixture.Directory, "device.spki"); var offerPath = Path.Combine(fixture.Directory, "offer.json");
        File.WriteAllBytes(spkiPath, spki);
        Assert.AreEqual(WifiBenchAdmin.Ok, WifiBenchAdmin.Run(new(WifiAdminCommand.IssuePeer, spkiPath, Convert.ToHexString(SHA256.HashData(spki)),
            offerPath, "192.168.1.2", 40002), fixture.Store, TextWriter.Null, TextWriter.Null));
        return WifiBenchOffer.Decode(File.ReadAllBytes(offerPath));
    }

    [TestMethod]
    public void Options_are_strict_pairs_and_a_missing_value_is_an_error()
    {
        Assert.AreEqual(new ClientOptions("192.168.1.9", 40003, "AB"),
            WifiBenchClientProgram.ParseOptions(["--ip", "192.168.1.9", "--port", "40003", "--expect-host-id", "AB"]));
        Assert.AreEqual(new ClientOptions(), WifiBenchClientProgram.ParseOptions([]));
        foreach (var bad in new[] { new[] { "--ip" }, new[] { "--port" }, new[] { "--ip", "--port", "1" }, new[] { "--ip", "1.2.3.4", "--port" },
            new[] { "--bogus", "x" }, new[] { "--ip", "10.0.0.1", "--ip", "10.0.0.2" }, new[] { "--port", "abc" }, new[] { "--port", "-1" },
            new[] { "loose" } })
            Assert.ThrowsException<ArgumentException>(() => WifiBenchClientProgram.ParseOptions(bad), string.Join(' ', bad));
    }

    [TestMethod]
    public void Ip_override_accepts_only_the_offer_address_or_rfc1918_and_the_port_is_validated()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        var offer = Offer(fixture, out var key);
        using (key)
        {
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("192.168.1.2"), 40002), WifiBenchClientProgram.ResolveEndpoint(offer, new()));
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("10.1.2.3"), 40003), WifiBenchClientProgram.ResolveEndpoint(offer, new("10.1.2.3", 40003)));
            Assert.AreEqual(new IPEndPoint(IPAddress.Parse("172.16.0.5"), 40002), WifiBenchClientProgram.ResolveEndpoint(offer, new("172.16.0.5")));
            foreach (var ip in new[] { "8.8.8.8", "127.0.0.1", "0.0.0.0", "::1", "192.168.001.9", "172.32.0.1", "example.com", "" })
                Assert.ThrowsException<ArgumentException>(() => WifiBenchClientProgram.ResolveEndpoint(offer, new(ip)), ip);
            foreach (var port in new[] { 0, 80, 8765, 8767, 70000 })
                Assert.ThrowsException<ArgumentException>(() => WifiBenchClientProgram.ResolveEndpoint(offer, new(null, port)), port.ToString());
        }
    }

    [TestMethod]
    public void Offer_summary_shows_the_host_id_and_peer_fingerprint_to_compare()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        var offer = Offer(fixture, out var key);
        using (key)
        {
            using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
            using var signer = new ECDsaCng(key);
            var text = WifiBenchClientProgram.Describe(offer, new IPEndPoint(IPAddress.Parse("192.168.1.2"), 40002));
            StringAssert.Contains(text, Convert.ToHexString(WifiCertificates.HostId(root)), "must equal the host id printed by --wifi-bench-init");
            StringAssert.Contains(text, Convert.ToHexString(SHA256.HashData(signer.ExportSubjectPublicKeyInfo())), "must equal the keygen fingerprint");
            StringAssert.Contains(text, "192.168.1.2:40002");
        }
    }

    [TestMethod]
    public void Usage_lists_every_case_including_all_negatives_and_window()
    {
        foreach (var word in new[] { "all-negatives", "window", "positive", "revoked", "no-cert", "wrong-root", "tls12", "wrong-alpn", "desktop",
            "stream-deck", "slowloris", "flood", "idle", "--expect-host-id", "--ip", "--port", "keygen", "cleanup" })
            StringAssert.Contains(WifiBenchClientProgram.Usage, word);
    }

    [TestMethod]
    public void Window_pacing_gives_flood_a_fresh_rolling_minute_and_other_cases_six_seconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(60), WifiBenchClientProgram.PauseBefore("flood"));
        foreach (var name in new[] { "revoked", "no-cert", "slowloris" })
            Assert.AreEqual(TimeSpan.FromSeconds(6), WifiBenchClientProgram.PauseBefore(name));
        CollectionAssert.AreEqual(new[] { "no-cert", "wrong-root", "tls12", "wrong-alpn", "desktop", "stream-deck", "slowloris", "flood" },
            WifiBenchClientProgram.AllNegatives);
    }
}
