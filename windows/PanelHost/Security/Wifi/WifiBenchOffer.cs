using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PanelHost.Security.Wifi;

// Bench artefact written by --wifi-bench-issue-peer and read by the test client. It carries only public
// material (root, issued leaf, endpoint): no private key and no one-time token, because the bench has no
// pairing port that could consume one. It is not the phone QR and must never be accepted as one.
internal sealed class WifiBenchOffer
{
    public const string TypeName = "PanelOSBenchOffer/1";
    public required byte[] HostId { get; init; }
    public required byte[] RootDer { get; init; }
    public required byte[] LeafDer { get; init; }
    public required string Ip { get; init; }
    public required int SessionPort { get; init; }
    public required long ExpiresAt { get; init; }

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535 and not (8765 or 8766 or 8767);

    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = TypeName, version = 1, hostId = WifiPairingOffer.B64(HostId), rootCa = WifiPairingOffer.B64(RootDer),
        leaf = WifiPairingOffer.B64(LeafDer), ip = Ip, sessionPort = SessionPort, expiresAt = ExpiresAt,
    }, new JsonSerializerOptions { WriteIndented = true });

    public static WifiBenchOffer Decode(byte[] bytes, DateTimeOffset? now = null)
    {
        try { return DecodeCore(bytes, now ?? DateTimeOffset.UtcNow); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or FormatException or CryptographicException or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("Invalid bench offer.", error); }
    }

    private static byte[] Field(string value, int maximum)
    {
        if (value.Length is < 1 || value.Length > maximum || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Invalid offer field.");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (WifiPairingOffer.B64(bytes) != value) throw new InvalidDataException("Non-canonical offer field.");
        return bytes;
    }

    private static WifiBenchOffer DecodeCore(byte[] bytes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 or > 4096) throw new InvalidDataException("Invalid offer length.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        var fields = json.RootElement.EnumerateObject().ToArray();
        string[] names = ["type", "version", "hostId", "rootCa", "leaf", "ip", "sessionPort", "expiresAt"];
        if (fields.Length != names.Length || fields.Select(p => p.Name).Distinct().Count() != names.Length ||
            fields.Any(p => !names.Contains(p.Name))) throw new InvalidDataException("Invalid offer fields.");
        var data = json.RootElement;
        if (data.GetProperty("type").GetString() != TypeName || data.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported offer.");
        var address = data.GetProperty("ip").GetString()!;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.ToString() != address || !WifiBenchOptions.IsPrivate(ip))
            throw new InvalidDataException("Offer requires a literal RFC1918 IPv4 address.");
        var port = data.GetProperty("sessionPort").GetInt32();
        if (!ValidPort(port)) throw new InvalidDataException("Invalid session port.");
        var expires = data.GetProperty("expiresAt").GetInt64();
        if (expires <= now.ToUnixTimeSeconds() || expires > now.AddDays(7).ToUnixTimeSeconds())
            throw new InvalidDataException("Offer expired or too far in the future.");
        var hostId = Field(data.GetProperty("hostId").GetString()!, 43);
        var rootDer = Field(data.GetProperty("rootCa").GetString()!, 683);
        var leafDer = Field(data.GetProperty("leaf").GetString()!, 683);
        if (hostId.Length != 32) throw new InvalidDataException("Invalid host id.");
        using var root = new X509Certificate2(rootDer);
        using var leaf = new X509Certificate2(leafDer);
        if (!WifiCertificates.ValidateRoot(root, hostId, now.UtcDateTime) ||
            !WifiCertificates.Validate(leaf, root, hostId, false, now.UtcDateTime))
            throw new InvalidDataException("Offer certificates do not validate.");
        return new WifiBenchOffer { HostId = hostId, RootDer = rootDer, LeafDer = leafDer, Ip = address, SessionPort = port, ExpiresAt = expires };
    }
}
