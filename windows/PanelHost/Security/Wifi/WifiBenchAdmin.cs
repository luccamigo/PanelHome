using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;

namespace PanelHost.Security.Wifi;

internal enum WifiAdminCommand { None, Init, IssuePeer, Revoke, Recover, Check }

internal sealed record WifiBenchAdminOptions(WifiAdminCommand Command = WifiAdminCommand.None, string PeerSpki = "",
    string Fingerprint = "", string OfferOut = "", string BindAddress = "", int SessionPort = 0, string RecoverConfirm = "", bool Ambiguous = false)
{
    public bool Enabled => Command != WifiAdminCommand.None || Ambiguous;
}

// Explicit, local, one-shot operator actions. None of them binds a socket or touches the cable path, so they
// run before the PanelHost single-instance mutex and do not require the production host to be stopped.
// Every output line is public (fingerprints are hashes of public keys); no key, token or payload is printed.
internal static class WifiBenchAdmin
{
    public const int Ok = 0;
    public const int UsageError = 2;
    public const int NotReady = 3;
    public const int ConfirmationRequired = 4;
    public const int Failed = 5;
    public const string NoIdentityMessage = "No WiFi bench identity exists on this PC. Run --wifi-bench-init first.";

    public static int Run(WifiBenchAdminOptions options, WifiIdentityStore store, TextWriter output, TextWriter error,
        DateTimeOffset? now = null, Func<IPAddress, WifiNetworkFacts>? inspect = null)
    {
        try
        {
            if (options.Ambiguous) throw new ArgumentException("Use exactly one WiFi bench action, and not together with --wifi-bench.");
            using var account = WindowsIdentity.GetCurrent();
            WifiTls.RequireHost(true, OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                new WindowsPrincipal(account).IsInRole(WindowsBuiltInRole.Administrator));
            // Every action except init needs an identity; say so instead of letting the store throw FileNotFoundException.
            if (options.Command is WifiAdminCommand.IssuePeer or WifiAdminCommand.Revoke && !store.Exists)
                throw new InvalidOperationException(NoIdentityMessage);
            return options.Command switch
            {
                WifiAdminCommand.Init => Init(store, output),
                WifiAdminCommand.IssuePeer => IssuePeer(options, store, output, now ?? DateTimeOffset.UtcNow),
                WifiAdminCommand.Revoke => Revoke(options, store, output),
                WifiAdminCommand.Recover => Recover(options, store, output),
                WifiAdminCommand.Check => Check(options, output, inspect ?? WifiWindowsNetwork.Inspect),
                _ => throw new ArgumentException("No WiFi bench action."),
            };
        }
        catch (ArgumentException exception) { error.WriteLine(exception.Message); return UsageError; }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException or IOException
            or UnauthorizedAccessException or CryptographicException)
        {
            // Only the message of our own exceptions; never a stack, path content or key material.
            error.WriteLine("WiFi bench action failed: " + exception.Message);
            return Failed;
        }
    }

    // F5: a read-only look at the interface that owns --wifi-bind, with the same judgement the listener applies.
    // It changes nothing: no profile, no firewall, no socket. Exit 0 when ready, 3 when not.
    private static int Check(WifiBenchAdminOptions options, TextWriter output, Func<IPAddress, WifiNetworkFacts> inspect)
    {
        if (!IPAddress.TryParse(options.BindAddress, out var ip) || ip.ToString() != options.BindAddress || !WifiBenchOptions.IsPrivate(ip))
            throw new ArgumentException("--wifi-bench-check needs an RFC1918 IPv4 --wifi-bind.");
        var facts = inspect(ip);
        output.WriteLine("[WiFi bench] Interface check (read-only; nothing was changed).");
        if (facts.Assigned)
            output.WriteLine($"[WiFi bench] type={facts.InterfaceType} status={facts.Status} physical={facts.Physical} profile={facts.Category} prefix=/{facts.PrefixLength}");
        var why = facts.WhyNotReady();
        output.WriteLine(why is null ? "[WiFi bench] READY: the bench would accept this address."
            : "[WiFi bench] NOT READY: " + why + ".");
        return why is null ? Ok : NotReady;
    }

    private static int Init(WifiIdentityStore store, TextWriter output)
    {
        var identity = store.Create();
        using var root = new System.Security.Cryptography.X509Certificates.X509Certificate2(Convert.FromBase64String(identity.RootDer));
        output.WriteLine("[WiFi bench] Identity created. Host id (root SPKI SHA-256): " + Convert.ToHexString(WifiCertificates.HostId(root)));
        return Ok;
    }

    private static bool ValidFingerprint(string value) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static int IssuePeer(WifiBenchAdminOptions options, WifiIdentityStore store, TextWriter output, DateTimeOffset now)
    {
        if (options.PeerSpki.Length == 0 || options.OfferOut.Length == 0 || !ValidFingerprint(options.Fingerprint) ||
            !IPAddress.TryParse(options.BindAddress, out var ip) || ip.ToString() != options.BindAddress || !WifiBenchOptions.IsPrivate(ip) ||
            !WifiBenchOffer.ValidPort(options.SessionPort))
            throw new ArgumentException("Issuing a bench peer needs --wifi-peer-spki, --wifi-peer-fingerprint, --wifi-offer-out, an RFC1918 --wifi-bind and --wifi-session-port.");
        if (File.Exists(options.OfferOut)) throw new ArgumentException("Offer file already exists; refusing to overwrite.");
        var info = new FileInfo(options.PeerSpki);
        if (!info.Exists || info.Length is < 1 or > 256) throw new ArgumentException("Public key file must be a 1..256 byte DER SubjectPublicKeyInfo.");
        var spki = File.ReadAllBytes(options.PeerSpki);
        // The operator reads this fingerprint from the device/test client and types it here: the local approval.
        var fingerprint = Convert.ToHexString(SHA256.HashData(spki));
        if (!string.Equals(fingerprint, options.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Fingerprint does not match the public key file; nothing was issued.");

        using var registry = new WifiPeerRegistry(store);
        var identity = store.Load();
        using var root = WifiIdentityStore.OpenCertificate(identity.RootDer, identity.RootKey);
        using var leaf = WifiCertificates.Issue(root, spki, false, now);
        registry.Issue(leaf, WifiBenchSession.PanelReadCapability);
        registry.Release(registry.Activate(leaf)); // persists Active; the bench itself never issues or activates
        var offer = new WifiBenchOffer { HostId = WifiCertificates.HostId(root), RootDer = root.RawData, LeafDer = leaf.RawData,
            Ip = options.BindAddress, SessionPort = options.SessionPort, ExpiresAt = now.AddSeconds(WifiPairingInvitation.LifetimeSeconds).ToUnixTimeSeconds() };
        using (var file = new FileStream(options.OfferOut, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            file.Write(offer.Encode());
        output.WriteLine("[WiFi bench] Peer issued and activated for panel-read only. Fingerprint: " + fingerprint);
        output.WriteLine("[WiFi bench] Offer written (public data only; valid 10 min). Revoke with --wifi-bench-revoke.");
        return Ok;
    }

    private static int Revoke(WifiBenchAdminOptions options, WifiIdentityStore store, TextWriter output)
    {
        if (!ValidFingerprint(options.Fingerprint)) throw new ArgumentException("--wifi-peer-fingerprint must be 64 hex characters.");
        using var registry = new WifiPeerRegistry(store);
        registry.Revoke(options.Fingerprint.ToUpperInvariant());
        output.WriteLine("[WiFi bench] Peer revoked; its key hash is permanently refused.");
        return Ok;
    }

    internal static string Describe(WifiRecoveryPlan plan) => plan.Scope switch
    {
        WifiRecoveryScope.None => "none",
        WifiRecoveryScope.OnePeer => "one",
        _ => "all",
    };

    // Dry-run by default. Applying needs --wifi-recover-confirm to repeat the scope that was just reported,
    // so the operator cannot revoke every peer by reflex.
    private static int Recover(WifiBenchAdminOptions options, WifiIdentityStore store, TextWriter output)
    {
        WifiRecoveryPlan plan;
        if (!store.Exists) { output.WriteLine("[WiFi bench] Nothing to recover: no WiFi bench identity exists."); return Ok; }
        try { plan = store.PlanRecovery(); }
        catch (FileNotFoundException) { output.WriteLine("[WiFi bench] Nothing to recover: no pending authorization guard."); return Ok; }
        catch (DirectoryNotFoundException) { output.WriteLine("[WiFi bench] Nothing to recover: no WiFi store."); return Ok; }
        var scope = Describe(plan);
        output.WriteLine(plan.Scope switch
        {
            WifiRecoveryScope.None => "[WiFi bench] Recovery would revoke NO peer (the previous owner exited without an interrupted revocation).",
            WifiRecoveryScope.OnePeer => "[WiFi bench] Recovery would revoke ONE peer (interrupted revocation; key " + plan.KeyId![..8] + "...).",
            _ => "[WiFi bench] Recovery would revoke ALL peers (the guard is unreadable, so intent cannot be known).",
        });
        if (!string.Equals(options.RecoverConfirm, scope, StringComparison.Ordinal))
        {
            output.WriteLine($"[WiFi bench] Nothing changed. Re-run with --wifi-recover-confirm {scope} to apply exactly this.");
            return ConfirmationRequired;
        }
        store.RecoverAuthorizationLocally();
        output.WriteLine("[WiFi bench] Recovery applied (" + scope + ").");
        return Ok;
    }
}
