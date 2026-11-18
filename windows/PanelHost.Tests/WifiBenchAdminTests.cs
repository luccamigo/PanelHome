using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;
using PanelHost.Security.Wifi;

namespace PanelHost.Tests;

// Local operator actions only: no socket is created by any test here.
[TestClass]
public sealed class WifiBenchAdminTests
{
    private sealed record Run(int Code, string Output, string Error);

    private static Run Execute(WifiBenchAdminOptions options, WifiIdentityStore store, DateTimeOffset? now = null)
    {
        var output = new StringWriter(); var error = new StringWriter();
        var code = WifiBenchAdmin.Run(options, store, output, error, now);
        return new(code, output.ToString(), error.ToString());
    }

    private static string Fingerprint(byte[] spki) => Convert.ToHexString(SHA256.HashData(spki));

    private sealed class Peer : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-Admin-" + Guid.NewGuid().ToString("N"));
        public readonly string SpkiPath, OfferPath;
        public readonly byte[] Spki;
        public Peer(CngKey key)
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var signer = new ECDsaCng(key);
            Spki = signer.ExportSubjectPublicKeyInfo();
            SpkiPath = Path.Combine(Directory, "device.spki"); OfferPath = Path.Combine(Directory, "offer.json");
            File.WriteAllBytes(SpkiPath, Spki);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private static WifiBenchAdminOptions Issue(Peer peer, string? fingerprint = null) => new(WifiAdminCommand.IssuePeer, peer.SpkiPath,
        fingerprint ?? Fingerprint(peer.Spki), peer.OfferPath, "192.168.1.2", 40002);

    [TestMethod]
    public void Init_creates_the_identity_once_and_prints_only_the_public_host_id()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-Admin-" + Guid.NewGuid().ToString("N"));
        var store = new WifiIdentityStore(directory, WifiIdentityStore.TestKeyPrefix);
        try
        {
            var first = Execute(new(WifiAdminCommand.Init), store);
            Assert.AreEqual(WifiBenchAdmin.Ok, first.Code, first.Error);
            var identity = store.Load();
            using var root = new X509Certificate2(Convert.FromBase64String(identity.RootDer));
            StringAssert.Contains(first.Output, Convert.ToHexString(WifiCertificates.HostId(root)));
            Assert.IsFalse(first.Output.Contains(identity.RootKey) || first.Output.Contains(identity.ServerKey));
            var second = Execute(new(WifiAdminCommand.Init), store);
            Assert.AreEqual(WifiBenchAdmin.Failed, second.Code);
            StringAssert.Contains(second.Error, "already exists");
        }
        finally
        {
            if (File.Exists(Path.Combine(directory, "identity.dpapi")))
            {
                var identity = store.Load();
                foreach (var name in new[] { identity.RootKey, identity.ServerKey })
                { using var key = CngKey.Open(name, CngProvider.MicrosoftSoftwareKeyStorageProvider); key.Delete(); }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void Issue_peer_requires_the_matching_fingerprint_and_writes_public_data_only()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var key = fixture.CreateDeviceKey();
        using var peer = new Peer(key);
        var wrong = Execute(Issue(peer, new string('A', 64)), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.UsageError, wrong.Code);
        Assert.IsFalse(File.Exists(peer.OfferPath));
        Assert.AreEqual(0, fixture.Store.Load().Peers.Length, "A wrong fingerprint must issue nothing.");

        var ok = Execute(Issue(peer), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.Ok, ok.Code, ok.Error);
        var state = fixture.Store.Load();
        Assert.AreEqual("Active", state.Peers.Single().State);
        Assert.AreEqual(WifiBenchSession.PanelReadCapability, state.Peers.Single().Capabilities);
        Assert.AreEqual(Fingerprint(peer.Spki), state.Peers.Single().KeyId);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Directory, "authorization.pending")), "Exits cleanly.");

        var text = File.ReadAllText(peer.OfferPath);
        foreach (var forbidden in new[] { "token", "pairId", "private", "PRIVATE", fixture.State.RootKey, fixture.State.ServerKey })
            Assert.IsFalse(text.Contains(forbidden), forbidden);
        var offer = WifiBenchOffer.Decode(File.ReadAllBytes(peer.OfferPath));
        Assert.AreEqual("192.168.1.2", offer.Ip); Assert.AreEqual(40002, offer.SessionPort);
        using var leaf = new X509Certificate2(offer.LeafDer);
        using var registry = new WifiPeerRegistry(fixture.Store);
        Assert.IsTrue(registry.IsAllowed(leaf));

        var again = Execute(Issue(peer), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.UsageError, again.Code, "An existing offer file is never overwritten.");
    }

    [TestMethod]
    public void Issue_peer_refuses_incomplete_or_unsafe_arguments()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var key = fixture.CreateDeviceKey();
        using var peer = new Peer(key);
        foreach (var options in new[] { Issue(peer) with { BindAddress = "8.8.8.8" }, Issue(peer) with { BindAddress = "127.0.0.1" },
            Issue(peer) with { BindAddress = "::1" }, Issue(peer) with { SessionPort = 8765 }, Issue(peer) with { SessionPort = 80 },
            Issue(peer) with { OfferOut = "" }, Issue(peer) with { PeerSpki = Path.Combine(peer.Directory, "missing") } })
            Assert.AreEqual(WifiBenchAdmin.UsageError, Execute(options, fixture.Store).Code);
        File.WriteAllBytes(peer.SpkiPath, new byte[300]);
        Assert.AreEqual(WifiBenchAdmin.UsageError, Execute(Issue(peer, new string('A', 64)), fixture.Store).Code);
        File.WriteAllBytes(peer.SpkiPath, new byte[] { 1, 2, 3 });
        var garbage = Execute(Issue(peer, Fingerprint([1, 2, 3])), fixture.Store);
        Assert.AreNotEqual(WifiBenchAdmin.Ok, garbage.Code, "A file that is not a P-256 public key is never issued.");
        Assert.AreEqual(0, fixture.Store.Load().Peers.Length);
    }

    [TestMethod]
    public void Offer_decoder_rejects_expired_public_foreign_and_extended_offers()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var key = fixture.CreateDeviceKey();
        using var peer = new Peer(key);
        Assert.AreEqual(WifiBenchAdmin.Ok, Execute(Issue(peer), fixture.Store).Code);
        var original = File.ReadAllBytes(peer.OfferPath);
        WifiBenchOffer.Decode(original);
        WifiBenchOffer.Decode(original, DateTimeOffset.UtcNow.AddMinutes(9));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(original, DateTimeOffset.UtcNow.AddMinutes(11)), "Valid for 10 minutes, not 1 h.");
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(original, DateTimeOffset.UtcNow.AddHours(2)));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode([]));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(new byte[5000]));
        var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(original)!;
        byte[] Mutate(string field, object value) { var copy = node.ToDictionary(p => p.Key, p => (object)p.Value); copy[field] = value; return JsonSerializer.SerializeToUtf8Bytes(copy); }
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("ip", "8.8.8.8")));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("ip", "127.0.0.1")));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("sessionPort", 8766)));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("type", "PanelOSPair/1")));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("extra", "x")));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("hostId", WifiPairingOffer.B64(new byte[32]))));
        Assert.ThrowsException<InvalidDataException>(() => WifiBenchOffer.Decode(Mutate("expiresAt", DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds())));
    }

    [TestMethod]
    public void Revoke_refuses_the_peer_permanently_and_reports_unknown_fingerprints()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var key = fixture.CreateDeviceKey();
        using var peer = new Peer(key);
        Assert.AreEqual(WifiBenchAdmin.Ok, Execute(Issue(peer), fixture.Store).Code);
        var offer = WifiBenchOffer.Decode(File.ReadAllBytes(peer.OfferPath));
        var bad = Execute(new(WifiAdminCommand.Revoke, Fingerprint: "xyz"), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.UsageError, bad.Code);
        var unknown = Execute(new(WifiAdminCommand.Revoke, Fingerprint: new string('A', 64)), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.Failed, unknown.Code);
        var revoked = Execute(new(WifiAdminCommand.Revoke, Fingerprint: Fingerprint(peer.Spki).ToLowerInvariant()), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.Ok, revoked.Code, revoked.Error);
        using var leaf = new X509Certificate2(offer.LeafDer);
        using var registry = new WifiPeerRegistry(fixture.Store);
        Assert.IsFalse(registry.IsAllowed(leaf));
        CollectionAssert.Contains(fixture.Store.Load().RevokedKeys, Fingerprint(peer.Spki));
    }

    [TestMethod]
    public void Recover_previews_by_default_and_applies_only_the_confirmed_scope()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var root = WifiIdentityStore.OpenCertificate(fixture.State.RootDer, fixture.State.RootKey);
        var guardPath = Path.Combine(fixture.Directory, "authorization.pending");
        Assert.AreEqual(WifiBenchAdmin.Ok, Execute(new(WifiAdminCommand.Recover), fixture.Store).Code, "No guard: nothing to do.");
        X509Certificate2 a, b;
        using (var registry = new WifiPeerRegistry(fixture.Store))
        {
            a = Active(registry, root); b = Active(registry, root);
        }
        using (a) using (b)
        {
            File.WriteAllText(guardPath, "owner-v2");
            var preview = Execute(new(WifiAdminCommand.Recover), fixture.Store);
            Assert.AreEqual(WifiBenchAdmin.ConfirmationRequired, preview.Code);
            StringAssert.Contains(preview.Output, "NO peer");
            Assert.IsTrue(File.Exists(guardPath), "Preview changes nothing.");
            Assert.AreEqual(WifiBenchAdmin.ConfirmationRequired, Execute(new(WifiAdminCommand.Recover, RecoverConfirm: "all"), fixture.Store).Code,
                "A mismatching confirmation never widens the scope.");
            Assert.IsTrue(File.Exists(guardPath));
            Assert.AreEqual(WifiBenchAdmin.Ok, Execute(new(WifiAdminCommand.Recover, RecoverConfirm: "none"), fixture.Store).Code);
            Assert.IsFalse(File.Exists(guardPath));
            using (var check = new WifiPeerRegistry(fixture.Store)) { Assert.IsTrue(check.IsAllowed(a)); Assert.IsTrue(check.IsAllowed(b)); }

            File.WriteAllText(guardPath, "revoke-v2:" + KeyId(a));
            var one = Execute(new(WifiAdminCommand.Recover), fixture.Store);
            StringAssert.Contains(one.Output, "ONE peer");
            Assert.AreEqual(WifiBenchAdmin.Ok, Execute(new(WifiAdminCommand.Recover, RecoverConfirm: "one"), fixture.Store).Code);
            using (var check = new WifiPeerRegistry(fixture.Store)) { Assert.IsFalse(check.IsAllowed(a)); Assert.IsTrue(check.IsAllowed(b)); }

            File.WriteAllText(guardPath, "garbage");
            StringAssert.Contains(Execute(new(WifiAdminCommand.Recover), fixture.Store).Output, "ALL peers");
            Assert.AreEqual(WifiBenchAdmin.Ok, Execute(new(WifiAdminCommand.Recover, RecoverConfirm: "all"), fixture.Store).Code);
            using (var check = new WifiPeerRegistry(fixture.Store)) Assert.IsFalse(check.IsAllowed(b));
        }
    }

    // F2: without an identity the actions report it clearly (exit 5) instead of leaking FileNotFoundException.
    [TestMethod]
    public void Actions_without_an_identity_say_so_instead_of_crashing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-NoIdentity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WifiIdentityStore(directory);
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var spki = key.ExportSubjectPublicKeyInfo();
            Directory.CreateDirectory(directory);
            var spkiPath = Path.Combine(directory, "d.spki"); File.WriteAllBytes(spkiPath, spki);
            var issue = Execute(new(WifiAdminCommand.IssuePeer, spkiPath, Fingerprint(spki), Path.Combine(directory, "o.json"), "192.168.1.2", 40002), store);
            Assert.AreEqual(WifiBenchAdmin.Failed, issue.Code);
            StringAssert.Contains(issue.Error, WifiBenchAdmin.NoIdentityMessage);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "o.json")));
            var revoke = Execute(new(WifiAdminCommand.Revoke, Fingerprint: Fingerprint(spki)), store);
            Assert.AreEqual(WifiBenchAdmin.Failed, revoke.Code);
            StringAssert.Contains(revoke.Error, "--wifi-bench-init");
            var recover = Execute(new(WifiAdminCommand.Recover), store);
            Assert.AreEqual(WifiBenchAdmin.Ok, recover.Code);
            StringAssert.Contains(recover.Output, "no WiFi bench identity");
            Assert.IsFalse(File.Exists(Path.Combine(directory, "authorization.pending")), "Nothing was created.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void A_store_held_by_another_process_is_reported_as_in_use_not_as_a_recovery_problem()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var key = fixture.CreateDeviceKey();
        using var peer = new Peer(key);
        using var owner = new WifiPeerRegistry(fixture.Store);
        var run = Execute(Issue(peer), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.Failed, run.Code);
        StringAssert.Contains(run.Error, "in use by another process");
        StringAssert.Contains(run.Error, "--wifi-bench-recover");
        Assert.IsFalse(File.Exists(peer.OfferPath));
    }

    [TestMethod]
    public void Recover_is_refused_while_the_owner_still_holds_the_guard()
    {
        using var fixture = new WifiCoreTests.IdentityFixture();
        using var registry = new WifiPeerRegistry(fixture.Store);
        var run = Execute(new(WifiAdminCommand.Recover, RecoverConfirm: "none"), fixture.Store);
        Assert.AreEqual(WifiBenchAdmin.Failed, run.Code, "A live owner must be stopped, not recovered under.");
    }

    [TestMethod]
    public void Command_line_selects_exactly_one_admin_action_and_never_together_with_the_listener()
    {
        var command = new PanelHostCommandLine((_, _) => Task.FromResult(0));
        Assert.IsFalse(command.Bind(command.Parse([])).WifiAdmin.Enabled);
        var issue = command.Bind(command.Parse(["--wifi-bench-issue-peer", "--wifi-peer-spki", "a.spki", "--wifi-peer-fingerprint", "AB",
            "--wifi-offer-out", "o.json", "--wifi-bind", "192.168.1.2", "--wifi-session-port", "40002"])).WifiAdmin;
        Assert.AreEqual(new WifiBenchAdminOptions(WifiAdminCommand.IssuePeer, "a.spki", "AB", "o.json", "192.168.1.2", 40002), issue);
        Assert.AreEqual(WifiAdminCommand.Recover, command.Bind(command.Parse(["--wifi-bench-recover", "--wifi-recover-confirm", "one"])).WifiAdmin.Command);
        var check = command.Bind(command.Parse(["--wifi-bench-check", "--wifi-bind", "192.168.1.2"])).WifiAdmin;
        Assert.AreEqual(new WifiBenchAdminOptions(WifiAdminCommand.Check, BindAddress: "192.168.1.2"), check);
        foreach (var args in new[] { new[] { "--wifi-bench-init", "--wifi-bench-revoke" }, new[] { "--wifi-bench-init", "--wifi-bench" } })
        {
            var ambiguous = command.Bind(command.Parse(args)).WifiAdmin;
            Assert.IsTrue(ambiguous.Enabled);
            Assert.AreEqual(WifiAdminCommand.None, ambiguous.Command);
            var run = Execute(ambiguous, new WifiIdentityStore(Path.Combine(Path.GetTempPath(), "PanelOS-Wifi-Test-Never-" + Guid.NewGuid().ToString("N"))));
            Assert.AreEqual(WifiBenchAdmin.UsageError, run.Code);
        }
    }

    // F5: the read-only interface check. Pure judgement over injected facts, so no machine state is assumed.
    private static readonly WifiNetworkFacts Ready = new(true, "id", "Ethernet", "Up", 24, true, "Private", true);

    [TestMethod]
    public void Check_reports_ready_only_for_a_physical_up_private_interface_and_changes_nothing()
    {
        var calls = 0;
        var output = new StringWriter();
        var code = WifiBenchAdmin.Run(new(WifiAdminCommand.Check, BindAddress: "192.168.1.2"), new WifiIdentityStore("C:\\does\\not\\exist"),
            output, TextWriter.Null, inspect: _ => { calls++; return Ready; });
        Assert.AreEqual(WifiBenchAdmin.Ok, code);
        StringAssert.Contains(output.ToString(), "READY");
        StringAssert.Contains(output.ToString(), "profile=Private");
        Assert.AreEqual(1, calls);
        Assert.IsFalse(Directory.Exists("C:\\does\\not\\exist"), "The check never creates or touches the WiFi store.");
    }

    [DataTestMethod]
    [DataRow("unassigned", "not assigned")]
    [DataRow("wrongtype", "interface type is Loopback")]
    [DataRow("down", "not Up")]
    [DataRow("virtual", "does not report the adapter as physical")]
    [DataRow("public", "profile is Public, not Private")]
    [DataRow("domain", "profile is DomainAuthenticated")]
    [DataRow("unknown", "profile is unknown")]
    [DataRow("prefix", "prefix length /31")]
    public void Check_names_the_first_reason_the_bench_would_refuse(string kind, string expected)
    {
        var facts = kind switch
        {
            "unassigned" => WifiNetworkFacts.NotAssigned,
            "wrongtype" => Ready with { InterfaceType = "Loopback" },
            "down" => Ready with { Status = "Down" },
            "virtual" => Ready with { Physical = false },
            "public" => Ready with { Category = "Public", Private = false },
            "domain" => Ready with { Category = "DomainAuthenticated", Private = false },
            "unknown" => Ready with { Category = "unknown", Private = false },
            _ => Ready with { PrefixLength = 31 },
        };
        var output = new StringWriter();
        var code = WifiBenchAdmin.Run(new(WifiAdminCommand.Check, BindAddress: "192.168.1.2"), new WifiIdentityStore("C:\\does\\not\\exist"),
            output, TextWriter.Null, inspect: _ => facts);
        Assert.AreEqual(WifiBenchAdmin.NotReady, code);
        StringAssert.Contains(output.ToString(), "NOT READY");
        StringAssert.Contains(output.ToString(), expected);
    }

    [TestMethod]
    public void Check_refuses_non_rfc1918_addresses_and_reads_the_real_nic_list_only_for_an_unassigned_address()
    {
        foreach (var bind in new[] { "", "8.8.8.8", "127.0.0.1", "::1", "0.0.0.0" })
            Assert.AreEqual(WifiBenchAdmin.UsageError, Execute(new(WifiAdminCommand.Check, BindAddress: bind), new WifiIdentityStore("C:\\does\\not\\exist")).Code, bind);
        // 10.255.255.254 is not expected on any local interface; the real inspection only enumerates NICs and asks WMI nothing.
        var real = Execute(new(WifiAdminCommand.Check, BindAddress: "10.255.255.254"), new WifiIdentityStore("C:\\does\\not\\exist"));
        Assert.AreEqual(WifiBenchAdmin.NotReady, real.Code, real.Output);
        StringAssert.Contains(real.Output, "not assigned");
    }

    private static string KeyId(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static X509Certificate2 Active(WifiPeerRegistry registry, X509Certificate2 root)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = WifiCertificates.Issue(root, key.ExportSubjectPublicKeyInfo(), false, DateTimeOffset.UtcNow);
        registry.Issue(certificate, 31); registry.Release(registry.Activate(certificate));
        return certificate;
    }

    // K1: the real identity and the tests must never share a key-name prefix, so no cleanup can touch the real keys.
    [TestMethod]
    public void Test_keys_and_the_real_identity_use_prefixes_that_can_never_match_each_other()
    {
        Assert.AreEqual("PanelOS-Wifi-", WifiIdentityStore.DefaultKeyPrefix);
        Assert.AreEqual("PanelOS-WifiTest-", WifiIdentityStore.TestKeyPrefix);
        Assert.IsFalse(WifiIdentityStore.TestKeyPrefix.StartsWith(WifiIdentityStore.DefaultKeyPrefix, StringComparison.Ordinal), "a PanelOS-Wifi-* cleanup would not touch test keys");
        Assert.IsFalse(WifiIdentityStore.DefaultKeyPrefix.StartsWith(WifiIdentityStore.TestKeyPrefix, StringComparison.Ordinal));
        Assert.AreEqual(WifiIdentityStore.DefaultKeyPrefix, new WifiIdentityStore().KeyPrefix, "production default");
        using var fixture = new WifiCoreTests.IdentityFixture();
        Assert.IsTrue(fixture.State.RootKey.StartsWith(WifiIdentityStore.TestKeyPrefix, StringComparison.Ordinal));
        Assert.IsTrue(fixture.State.ServerKey.StartsWith(WifiIdentityStore.TestKeyPrefix, StringComparison.Ordinal));
        using var device = fixture.CreateDeviceKey();
        Assert.IsTrue(device.KeyName!.StartsWith(WifiIdentityStore.TestKeyPrefix, StringComparison.Ordinal));
    }

    [TestMethod]
    public void Creating_keys_with_the_wrong_prefix_for_the_directory_is_refused_before_any_key_exists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "PanelOS-WifiTest-Dir-" + Guid.NewGuid().ToString("N"));
        // A temporary directory with the real prefix would be a way for a test to mint real-looking keys.
        Assert.ThrowsException<InvalidOperationException>(() => new WifiIdentityStore(temp).Create());
        // And the real directory with a test prefix would hide the real identity from every cleanup rule.
        Assert.ThrowsException<InvalidOperationException>(() => new WifiIdentityStore(null, WifiIdentityStore.TestKeyPrefix).Create());
        Assert.IsFalse(Directory.Exists(temp));
    }}
