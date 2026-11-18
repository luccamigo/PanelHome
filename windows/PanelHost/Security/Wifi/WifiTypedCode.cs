using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace PanelHost.Security.Wifi;

internal sealed record WifiTypedCodeFields(byte[] HostId, byte[] PairId, string Ip, int PairPort, int SessionPort, string Pin);

// Typed-code fallback for when the camera cannot read the QR (PIN mode of seguranca-wifi.md 5.2).
// It carries the complete 256-bit host id (never abbreviated), the pairing id, the endpoint and the six-digit PIN,
// protected only against typing mistakes by a CRC-16. The CRC is NOT a security control: the PIN is limited to five
// wrong attempts per invitation on the host, and the host id is the pin of the PC the app will talk to.
//
//   bytes: version(1)=1 | hostId(32) | pairId(16) | ipv4(4) | pairPort(2) | sessionPort(2) | pin(3, 0..999999) | crc16(2)
//   text : RFC 4648 Base32 without padding (100 characters), shown in groups of five separated by '-'.
internal static class WifiTypedCode
{
    public const int PayloadBytes = 62;
    public const int TextLength = 100;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(WifiPairingOffer offer, string pin)
    {
        if (pin.Length != 6 || !pin.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid PIN.");
        if (!IPAddress.TryParse(offer.Ip, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException("Invalid address.");
        var bytes = new byte[PayloadBytes];
        bytes[0] = 1;
        offer.HostId.CopyTo(bytes, 1);
        offer.PairId.CopyTo(bytes, 33);
        ip.GetAddressBytes().CopyTo(bytes, 49);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(53), checked((ushort)offer.PairPort));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(55), checked((ushort)offer.SessionPort));
        var value = int.Parse(pin, CultureInfo.InvariantCulture);
        bytes[57] = (byte)(value >> 16); bytes[58] = (byte)(value >> 8); bytes[59] = (byte)value;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(60), Crc16(bytes.AsSpan(0, 60)));
        var plain = Base32(bytes);
        return string.Join('-', Enumerable.Range(0, plain.Length / 5).Select(i => plain.Substring(i * 5, 5)));
    }

    public static WifiTypedCodeFields Decode(string text)
    {
        try { return DecodeCore(text); }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or FormatException or OverflowException)
        { throw new InvalidDataException("Invalid pairing code.", error); }
    }

    private static WifiTypedCodeFields DecodeCore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = new string(text.Where(c => c is not ('-' or ' ' or '\t' or '\r' or '\n')).ToArray()).ToUpperInvariant();
        if (clean.Length != TextLength || clean.Any(c => !Alphabet.Contains(c))) throw new InvalidDataException("Bad code text.");
        var bytes = FromBase32(clean);
        if (bytes.Length != PayloadBytes || bytes[0] != 1) throw new InvalidDataException("Bad code version.");
        if (Crc16(bytes.AsSpan(0, 60)) != BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(60))) throw new InvalidDataException("Bad code checksum.");
        var ip = new IPAddress(bytes.AsSpan(49, 4)).ToString();
        var octets = bytes.AsSpan(49, 4);
        if (!(octets[0] == 10 || octets[0] == 172 && octets[1] is >= 16 and <= 31 || octets[0] == 192 && octets[1] == 168))
            throw new InvalidDataException("Code requires a private IPv4 address.");
        var pairPort = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(53));
        var sessionPort = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(55));
        if (pairPort == sessionPort || !WifiBenchOffer.ValidPort(pairPort) || !WifiBenchOffer.ValidPort(sessionPort))
            throw new InvalidDataException("Invalid ports.");
        var pin = (bytes[57] << 16) | (bytes[58] << 8) | bytes[59];
        if (pin > 999_999) throw new InvalidDataException("Invalid PIN.");
        return new WifiTypedCodeFields(bytes[1..33], bytes[33..49], ip, pairPort, sessionPort, pin.ToString("D6", CultureInfo.InvariantCulture));
    }

    // CRC-16/CCITT-FALSE: polynomial 0x1021, initial value 0xFFFF, no reflection, no final xor.
    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    private static string Base32(byte[] data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value; bits += 8;
            while (bits >= 5) { text.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    private static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { bytes.Add((byte)(buffer >> (bits - 8))); bits -= 8; }
        }
        // Canonical form: the unused trailing bits must be zero, so one payload has exactly one text.
        if ((buffer & ((1 << bits) - 1)) != 0) throw new InvalidDataException("Non-canonical code.");
        return [.. bytes];
    }
}
