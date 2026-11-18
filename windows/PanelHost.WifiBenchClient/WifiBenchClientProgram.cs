using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using PanelHost.Security.Wifi;

namespace PanelHost.WifiBenchClient;

internal sealed record ClientOptions(string? Ip = null, int? Port = null, string? ExpectedHostId = null);

internal static partial class WifiBenchClientProgram
{
    private const string KeyPrefix = "PanelOS-WifiBench-";
    public const int UsageExitCode = 2;
    public const int FailedExitCode = 5;

    internal static readonly string[] AllNegatives =
        ["no-cert", "wrong-root", "tls12", "wrong-alpn", "desktop", "stream-deck", "slowloris", "flood"];

    internal const string Usage = """
        Usage:
          keygen <name> <out.spki>                      create a non-exportable CNG key and write its public key
          run <offer.json> <name> <case> [options]       one case, or all-negatives
          window <offer1.json> <name1> <offer2.json> <name2> [options]
                                                        the whole window with no typing: positive (offer1/name1), revoked
                                                        (offer2/name2, revoked on the host beforehand), then all-negatives
                                                        (offer1/name1), with the pacing the host's admission limits need
          cleanup <name>                                delete that key
        cases: positive, revoked, no-cert, wrong-root, tls12, wrong-alpn, desktop, stream-deck, slowloris, flood, idle,
               all-negatives (= no-cert, wrong-root, tls12, wrong-alpn, desktop, stream-deck, slowloris, flood; not idle)
        options: --ip a.b.c.d   (only the offer address or another RFC1918 IPv4)
                 --port n       (1024..65535, not 8765-8767)
                 --expect-host-id <64 hex>   refuse an offer whose host id differs from the one printed by --wifi-bench-init
        """;

    [GeneratedRegex("^[A-Za-z0-9-]{1,40}$")] private static partial Regex ValidName();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "keygen") return KeyGen(args[1], args[2]);
            if (args.Length == 2 && args[0] == "cleanup") return Cleanup(args[1]);
            if (args.Length >= 4 && args[0] == "run") return await RunAsync(args).ConfigureAwait(false);
            if (args.Length >= 5 && args[0] == "window") return await WindowAsync(args).ConfigureAwait(false);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine("Usage error: " + error.Message);
            return UsageExitCode;
        }
        catch (Exception error) when (error is IOException or CryptographicException or InvalidDataException)
        {
            Console.Error.WriteLine("Failed: " + error.Message);
            return FailedExitCode;
        }
        Console.Error.WriteLine(Usage);
        return UsageExitCode;
    }

    private static string KeyName(string name) =>
        ValidName().IsMatch(name) ? KeyPrefix + name : throw new ArgumentException("Key name must be 1..40 letters, digits or '-'.");

    // Strictly "--name value" pairs. A trailing option without a value, a repeated option or an unknown one is an
    // error: silently ignoring a mistyped --ip would send the run to the offer's address instead.
    internal static ClientOptions ParseOptions(IReadOnlyList<string> args)
    {
        string? ip = null, expected = null; int? port = null;
        for (var i = 0; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {args[i]} needs a value.");
            var value = args[i + 1];
            switch (args[i])
            {
                case "--ip" when ip is null: ip = value; break;
                case "--port" when port is null:
                    port = int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : throw new ArgumentException("--port must be a number.");
                    break;
                case "--expect-host-id" when expected is null: expected = value; break;
                case "--ip" or "--port" or "--expect-host-id": throw new ArgumentException($"Option {args[i]} was given twice.");
                default: throw new ArgumentException("Unknown option " + args[i]);
            }
        }
        return new ClientOptions(ip, port, expected);
    }

    internal static IPEndPoint ResolveEndpoint(WifiBenchOffer offer, ClientOptions options)
    {
        var ip = offer.Ip;
        if (options.Ip is not null)
        {
            if (!IPAddress.TryParse(options.Ip, out var parsed) || parsed.ToString() != options.Ip ||
                options.Ip != offer.Ip && !WifiBenchOptions.IsPrivate(parsed))
                throw new ArgumentException("--ip must be the offer address or an RFC1918 IPv4 address.");
            ip = options.Ip;
        }
        var port = options.Port ?? offer.SessionPort;
        if (!WifiBenchOffer.ValidPort(port)) throw new ArgumentException("--port must be 1024..65535 and not 8765-8767.");
        return new IPEndPoint(IPAddress.Parse(ip), port);
    }

    internal static string HostIdHex(WifiBenchOffer offer) => Convert.ToHexString(offer.HostId);

    internal static string PeerFingerprint(WifiBenchOffer offer)
    {
        using var leaf = new X509Certificate2(offer.LeafDer);
        return Convert.ToHexString(SHA256.HashData(leaf.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    // What the operator compares: the host id with the one --wifi-bench-init printed, the fingerprint with keygen's.
    internal static string Describe(WifiBenchOffer offer, IPEndPoint endpoint) =>
        $"Offer host id: {HostIdHex(offer)}{Environment.NewLine}Offer peer fingerprint: {PeerFingerprint(offer)}{Environment.NewLine}Target: {endpoint}";

    private static WifiBenchOffer LoadOffer(string path, ClientOptions options)
    {
        var offer = WifiBenchOffer.Decode(File.ReadAllBytes(path));
        if (options.ExpectedHostId is not null &&
            !string.Equals(options.ExpectedHostId, HostIdHex(offer), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The offer host id does not match --expect-host-id; refusing to connect.");
        return offer;
    }

    private static int KeyGen(string name, string output)
    {
        var fullName = KeyName(name);
        if (File.Exists(output)) throw new ArgumentException("Output file already exists.");
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, fullName, new CngKeyCreationParameters
        {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, ExportPolicy = CngExportPolicies.None, KeyUsage = CngKeyUsages.Signing,
        });
        using var signer = new ECDsaCng(key);
        var spki = signer.ExportSubjectPublicKeyInfo();
        File.WriteAllBytes(output, spki);
        Console.WriteLine("Public key written. Fingerprint (type this into --wifi-peer-fingerprint on the host): " + Convert.ToHexString(SHA256.HashData(spki)));
        return 0;
    }

    private static int Cleanup(string name)
    {
        using var key = CngKey.Open(KeyName(name), CngProvider.MicrosoftSoftwareKeyStorageProvider);
        key.Delete();
        Console.WriteLine("Key deleted.");
        return 0;
    }

    // One peer: its offer, its device certificate and a same-key certificate from a root the host does not trust.
    internal sealed class Peer : IDisposable
    {
        public readonly WifiBenchOffer Offer;
        public readonly X509Certificate2 Device, Foreign;
        private readonly CngKey? _key;
        public Peer(WifiBenchOffer offer, string name)
        {
            Offer = offer;
            _key = CngKey.Open(KeyName(name), CngProvider.MicrosoftSoftwareKeyStorageProvider);
            Device = WifiBenchClientRunner.DeviceCertificate(offer, _key);
            Foreign = WifiBenchClientRunner.ForeignRootCertificate(_key);
        }
        // For the suite: certificates owned by the caller.
        internal Peer(WifiBenchOffer offer, X509Certificate2 device, X509Certificate2 foreign)
        { Offer = offer; Device = device; Foreign = foreign; }
        public void Dispose() { if (_key is null) return; Device.Dispose(); Foreign.Dispose(); _key.Dispose(); }
    }

    // Admission allows a burst of four and one new handshake per five seconds (12 a minute): pace the cases so a
    // refusal is attributable to the case under test, not to the host's own rate limit. The flood case needs a fresh
    // rolling minute, so it waits one out.
    internal static TimeSpan PauseBefore(string name) => TimeSpan.FromSeconds(name == "flood" ? 60 : 6);

    internal static Task RealPause(TimeSpan pause, CancellationToken token) => Task.Delay(pause, token);

    internal static async Task RunCaseAsync(string name, Peer peer, IPEndPoint endpoint, List<BenchResult> results,
        Func<TimeSpan, CancellationToken, Task> pause, CancellationToken token)
    {
        if (results.Count > 0) await pause(PauseBefore(name), token).ConfigureAwait(false);
        results.Add(name switch
        {
            "positive" => await WifiBenchClientRunner.PositiveAsync(peer.Offer, endpoint, peer.Device, token),
            "revoked" => await WifiBenchClientRunner.RevokedOrUnknownAsync(peer.Offer, endpoint, peer.Device, token),
            "no-cert" => await WifiBenchClientRunner.NoCertificateAsync(peer.Offer, endpoint, token),
            "wrong-root" => await WifiBenchClientRunner.WrongRootAsync(peer.Offer, endpoint, peer.Foreign, token),
            "tls12" => await WifiBenchClientRunner.Tls12Async(peer.Offer, endpoint, peer.Device, token),
            "wrong-alpn" => await WifiBenchClientRunner.WrongAlpnAsync(peer.Offer, endpoint, peer.Device, token),
            "desktop" => await WifiBenchClientRunner.CommandAsync(peer.Offer, endpoint, peer.Device, "desktop_start", token),
            "stream-deck" => await WifiBenchClientRunner.CommandAsync(peer.Offer, endpoint, peer.Device, "stream_deck_invoke", token),
            "idle" => await WifiBenchClientRunner.IdleAsync(peer.Offer, endpoint, peer.Device, token),
            "slowloris" => await WifiBenchClientRunner.SlowlorisAsync(endpoint, token),
            "flood" => await WifiBenchClientRunner.FloodAsync(endpoint, 8, token),
            _ => throw new ArgumentException("Unknown case " + name),
        });
        Console.WriteLine(results[^1]);
    }

    private static CancellationToken CancelOnCtrlC()
    {
        var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        return cancel.Token;
    }

    private static int Summarize(List<BenchResult> results)
    {
        var failed = results.Count(result => !result.Passed);
        Console.WriteLine($"Summary: {results.Count - failed} PASS, {failed} FAIL. Compare the host's counters line with docs/wifi-bench.md.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var options = ParseOptions(args[4..]);
        var offer = LoadOffer(args[1], options);
        var endpoint = ResolveEndpoint(offer, options);
        Console.WriteLine(Describe(offer, endpoint));
        using var peer = new Peer(offer, args[2]);
        var token = CancelOnCtrlC();
        var results = new List<BenchResult>();
        foreach (var name in args[3] == "all-negatives" ? AllNegatives : [args[3]])
            await RunCaseAsync(name, peer, endpoint, results, RealPause, token).ConfigureAwait(false);
        return Summarize(results);
    }

    // The whole window in one command. Start it right after the host reports that the listener is enabled; with the
    // pacing it takes about two minutes of the 180 s window. No prompt, no input.
    private static async Task<int> WindowAsync(string[] args)
    {
        var options = ParseOptions(args[5..]);
        var offer1 = LoadOffer(args[1], options);
        var offer2 = LoadOffer(args[3], options);
        if (!offer1.HostId.AsSpan().SequenceEqual(offer2.HostId))
            throw new ArgumentException("The two offers belong to different hosts.");
        var endpoint = ResolveEndpoint(offer1, options);
        if (!endpoint.Equals(ResolveEndpoint(offer2, options)))
            throw new ArgumentException("The two offers name different endpoints; pass --ip/--port to force one.");
        Console.WriteLine("Peer 1 (positive and negatives):" + Environment.NewLine + Describe(offer1, endpoint));
        Console.WriteLine("Peer 2 (must already be revoked on the host):" + Environment.NewLine + Describe(offer2, endpoint));
        using var peer1 = new Peer(offer1, args[2]);
        using var peer2 = new Peer(offer2, args[4]);
        var token = CancelOnCtrlC();
        return Summarize(await RunWindowAsync(peer1, peer2, endpoint, RealPause, token).ConfigureAwait(false));
    }

    // positive -> revoked -> all-negatives, in this order, with the host-friendly pauses. The suite runs this same
    // function against a loopback listener with the admission clock advanced instead of sleeping.
    internal static async Task<List<BenchResult>> RunWindowAsync(Peer valid, Peer revoked, IPEndPoint endpoint,
        Func<TimeSpan, CancellationToken, Task> pause, CancellationToken token)
    {
        var results = new List<BenchResult>();
        await RunCaseAsync("positive", valid, endpoint, results, pause, token).ConfigureAwait(false);
        await RunCaseAsync("revoked", revoked, endpoint, results, pause, token).ConfigureAwait(false);
        foreach (var name in AllNegatives) await RunCaseAsync(name, valid, endpoint, results, pause, token).ConfigureAwait(false);
        return results;
    }
}
