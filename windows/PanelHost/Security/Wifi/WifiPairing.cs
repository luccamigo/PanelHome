using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text;
using System.Globalization;

namespace PanelHost.Security.Wifi;

internal sealed class WifiPairingOffer : IDisposable
{
    public required byte[] HostId { get; init; }
    public required byte[] RootDer { get; init; }
    public required byte[] PairId { get; init; }
    public required byte[] Token { get; init; }
    public required string Ip { get; init; }
    public required int PairPort { get; init; }
    public required int SessionPort { get; init; }
    public required long ExpiresAt { get; init; }
    public void Dispose() => CryptographicOperations.ZeroMemory(Token);
    public static WifiPairingOffer Create(X509Certificate2 root, string ip, int pairPort, int sessionPort)
    {
        using var generated = new WifiPairingOffer { HostId = WifiCertificates.HostId(root), RootDer = root.RawData, PairId = RandomNumberGenerator.GetBytes(16),
            Token = RandomNumberGenerator.GetBytes(32), Ip = ip, PairPort = pairPort, SessionPort = sessionPort,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(WifiPairingInvitation.LifetimeSeconds).ToUnixTimeSeconds() };
        var encoded = generated.Encode();
        try { return Decode(encoded); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
    public byte[] Encode() => JsonSerializer.SerializeToUtf8Bytes(new
    { type = "PanelOSPair/1", version = 1, hostId = B64(HostId), rootCa = B64(RootDer), ip = Ip, pairPort = PairPort,
      sessionPort = SessionPort, pairId = B64(PairId), token = B64(Token), expiresAt = ExpiresAt });
    // PIN mode: the token is SHA-256 of the six ASCII digits, so the six digits the operator types stand in for
    // the 32-byte QR token (seguranca-wifi.md 5.2). Leading zeros are kept.
    public static WifiPairingOffer CreateWithPin(X509Certificate2 root, string ip, int pairPort, int sessionPort, out string pin)
    {
        pin = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        return CreateWithToken(root, ip, pairPort, sessionPort, SHA256.HashData(Encoding.ASCII.GetBytes(pin)));
    }
    public static byte[] TokenFromPin(string pin) => pin.Length == 6 && pin.All(char.IsAsciiDigit)
        ? SHA256.HashData(Encoding.ASCII.GetBytes(pin)) : throw new InvalidDataException("Invalid PIN.");
    private static WifiPairingOffer CreateWithToken(X509Certificate2 root, string ip, int pairPort, int sessionPort, byte[] token)
    {
        using var generated = new WifiPairingOffer { HostId = WifiCertificates.HostId(root), RootDer = root.RawData, PairId = RandomNumberGenerator.GetBytes(16),
            Token = token, Ip = ip, PairPort = pairPort, SessionPort = sessionPort,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(WifiPairingInvitation.LifetimeSeconds).ToUnixTimeSeconds() };
        var encoded = generated.Encode();
        try { return Decode(encoded); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
    public static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Binary(string value, int size)
    {
        if (value.Length != (size * 8 + 5) / 6 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Invalid pairing binary field.");
        var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        if (bytes.Length != size || B64(bytes) != value) throw new InvalidDataException("Non-canonical pairing field.");
        return bytes;
    }
    public static WifiPairingOffer Decode(byte[] bytes, DateTime? verificationTime = null)
    {
        try { return DecodeCore(bytes, verificationTime); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or
            ArgumentException or FormatException or CryptographicException or KeyNotFoundException or OverflowException)
        { throw new InvalidDataException("Invalid pairing QR.", error); }
    }

    private static WifiPairingOffer DecodeCore(byte[] bytes, DateTime? verificationTime)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length is < 1 or > 1024) throw new InvalidDataException("Invalid QR length.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        var fields = json.RootElement.EnumerateObject().ToArray();
        string[] names = ["type", "version", "hostId", "rootCa", "ip", "pairPort", "sessionPort", "pairId", "token", "expiresAt"];
        if (fields.Length != names.Length || fields.Select(p => p.Name).Distinct().Count() != names.Length ||
            fields.Any(p => !names.Contains(p.Name))) throw new InvalidDataException("Invalid QR fields.");
        var data = json.RootElement;
        if (data.GetProperty("type").GetString() != "PanelOSPair/1" || data.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("Unsupported QR version.");
        var address = data.GetProperty("ip").GetString()!;
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.ToString() != address)
            throw new InvalidDataException("QR requires a literal private IPv4 address.");
        var octets = ip.GetAddressBytes();
        if (!(octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 || octets[0] == 192 && octets[1] == 168))
            throw new InvalidDataException("QR requires a private IPv4 address.");
        var pairPort = data.GetProperty("pairPort").GetInt32();
        var sessionPort = data.GetProperty("sessionPort").GetInt32();
        if (pairPort is < 1 or > 65535 || sessionPort is < 1 or > 65535 || pairPort == sessionPort ||
            pairPort is 8765 or 8766 or 8767 || sessionPort is 8765 or 8766 or 8767)
            throw new InvalidDataException("Invalid pairing ports.");
        var hostId = Binary(data.GetProperty("hostId").GetString()!, 32);
        var encodedRoot = data.GetProperty("rootCa").GetString()!;
        if (encodedRoot.Length is < 1 or > 683 || encodedRoot.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidDataException("Invalid QR root.");
        var rootDer = Convert.FromBase64String(encodedRoot.Replace('-', '+').Replace('_', '/') + new string('=', (4 - encodedRoot.Length % 4) % 4));
        if (X509Certificate2.GetCertContentType(rootDer) != X509ContentType.Cert)
            throw new InvalidDataException("QR root must be a public X.509 certificate.");
        using var root = new X509Certificate2(rootDer);
        if (B64(rootDer) != encodedRoot || !root.RawData.AsSpan().SequenceEqual(rootDer) || !WifiCertificates.ValidateRoot(root, hostId, verificationTime))
            throw new InvalidDataException("Invalid QR root.");
        return new WifiPairingOffer { HostId = hostId, RootDer = rootDer,
            PairId = Binary(data.GetProperty("pairId").GetString()!, 16), Token = Binary(data.GetProperty("token").GetString()!, 32),
            Ip = address, PairPort = pairPort, SessionPort = sessionPort, ExpiresAt = data.GetProperty("expiresAt").GetInt64() };
    }
}

// This is a local operator state machine, not a network handler. Approval is
// deliberately a separate local call with no equivalent remote message.
internal sealed class WifiPairingInvitation : IDisposable
{
    // The owner decided on 10 minutes for a pairing offer/QR (single use, five invalid attempts, local approval within
    // 60 s). seguranca-wifi.md proposed 120 s; this is the only place that number lives.
    public const int LifetimeSeconds = 600;
    private readonly object _gate = new();
    private readonly byte[] _tokenHash;
    private readonly byte[] _hostId;
    private readonly byte[] _pairId;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private byte[]? _candidate;
    private long _reservedAt;
    private int _invalid;
    private bool _closed;
    private Attempt? _currentAttempt;
    public string? CandidateLabel { get; private set; }
    public bool IsClosed { get { lock (_gate) return _closed || Expired; } }

    public WifiPairingInvitation(WifiPairingOffer offer, TimeProvider? clock = null)
    {
        _hostId = offer.HostId.ToArray(); _pairId = offer.PairId.ToArray();
        _tokenHash = SHA256.HashData(offer.Token); _clock = clock ?? TimeProvider.System; _started = _clock.GetTimestamp();
    }
    private bool Expired => _clock.GetElapsedTime(_started) >= TimeSpan.FromSeconds(LifetimeSeconds);
    public Attempt BeginAttempt()
    {
        lock (_gate)
        {
            if (_closed || Expired || _candidate is not null || _currentAttempt is not null) throw new InvalidOperationException("pairing_failed");
            return _currentAttempt = new Attempt(this, RandomNumberGenerator.GetBytes(32));
        }
    }
    public X509Certificate2 ApproveLocally(X509Certificate2 root, WifiPeerRegistry registry, int capabilities)
    {
        lock (_gate)
        {
            if (_closed || Expired || _candidate is null || _clock.GetElapsedTime(_reservedAt) >= TimeSpan.FromSeconds(60))
                throw new InvalidOperationException("pairing_failed");
            if (!CryptographicOperations.FixedTimeEquals(WifiCertificates.HostId(root), _hostId))
                throw new InvalidOperationException("pairing_failed");
            _closed = true;
            var certificate = WifiCertificates.Issue(root, _candidate, false, _clock.GetUtcNow());
            try { registry.Issue(certificate, capabilities, CandidateLabel); return certificate; }
            catch { certificate.Dispose(); throw; }
        }
    }
    public void Dispose() { lock (_gate) { _closed = true; CryptographicOperations.ZeroMemory(_tokenHash); } }

    internal static string? NormalizeLabel(string? value)
    {
        if (value is null || value.Length is < 1 or > 64) return null;
        try
        {
            value = value.Normalize(NormalizationForm.FormC);
            for (var offset = 0; offset < value.Length;)
            {
                if (!Rune.TryGetRuneAt(value, offset, out var rune) || Rune.GetUnicodeCategory(rune) is
                    UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                    return null;
                offset += rune.Utf16SequenceLength;
            }
            return value.Length <= 64 ? value : null;
        }
        catch (ArgumentException) { return null; }
    }

    internal sealed class Attempt(WifiPairingInvitation invitation, byte[] challenge) : IDisposable
    {
        public byte[] Challenge { get; } = challenge;
        private bool _used;
        public bool Reserve(byte[] pairId, byte[] token, byte[] spki, byte[] signature, string label)
        {
            lock (invitation._gate)
            {
                if (_used || invitation._currentAttempt != this) return false;
                _used = true; invitation._currentAttempt = null;
                var valid = false;
                try
                {
                    var normalizedLabel = NormalizeLabel(label);
                    valid = !invitation._closed && !invitation.Expired && invitation._candidate is null &&
                        normalizedLabel is not null &&
                        CryptographicOperations.FixedTimeEquals(invitation._pairId, pairId) && token.Length == 32 &&
                        CryptographicOperations.FixedTimeEquals(invitation._tokenHash, SHA256.HashData(token)) &&
                        WifiProof.Verify(spki, WifiProof.Message(Challenge, invitation._hostId, spki, pairId, token), signature);
                    if (valid) invitation.CandidateLabel = normalizedLabel;
                }
                catch (Exception error) when (error is CryptographicException or InvalidDataException) { }
                if (!valid) { if (++invitation._invalid >= 5) invitation._closed = true; return false; }
                invitation._candidate = spki.ToArray(); invitation._reservedAt = invitation._clock.GetTimestamp();
                return true;
            }
        }
        public void Dispose()
        {
            lock (invitation._gate)
            {
                if (!_used && invitation._currentAttempt == this && !invitation._closed && ++invitation._invalid >= 5)
                    invitation._closed = true;
                _used = true; if (invitation._currentAttempt == this) invitation._currentAttempt = null;
                CryptographicOperations.ZeroMemory(Challenge);
            }
        }
    }
}
