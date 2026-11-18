using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace PanelHost.Transport;

/// <summary>
/// PLMX transport protocol version 2.
///
/// v1 had no session identity, so a byte stream reopened over a device that was
/// still in accessory mode could deliver the previous session's frames to the
/// new one. Those frames decoded correctly - they were valid v1 frames - and the
/// new session then failed on "data arrived for unopened channel". v2 fixes that
/// at the protocol level: every frame carries a tag derived from a 128-bit
/// session nonce, and a session refuses to interpret anything until it has
/// completed an explicit HELLO/HELLO_ACK exchange for that exact nonce.
///
/// This is a separation mechanism, not a security mechanism. A random session id
/// distinguishes sessions and rejects stale or accidentally replayed frames; it
/// does not authenticate the peer.
/// </summary>
internal static class PlmxVersions
{
    /// <summary>
    /// Native USB speaks v2 only. v1 remains implemented and tested for the
    /// shared fixtures, but a session never negotiates down to it: silently
    /// accepting v1 would reintroduce exactly the identity-free framing that
    /// stale bytes exploit.
    /// </summary>
    public const byte Minimum = 2;
    public const byte Maximum = 2;

    public static bool IsSupported(byte version) => version is >= Minimum and <= Maximum;
}

internal enum PlmxFrameType : ushort
{
    Data = 0x0000,
    Hello = 0x0001,
    Open = 0x0002,
    Fin = 0x0004,
    HelloAck = 0x0008,
}

/// <summary>
/// 128 bits of cryptographic randomness identifying one physical session.
/// Deliberately not derived from a clock, a process id, a counter or a device
/// serial: those repeat across reconnects, which is the case that has to be
/// distinguishable. Stored as two 64-bit halves so it costs no allocation.
/// </summary>
internal readonly record struct PlmxSessionId(ulong High, ulong Low)
{
    public const int Size = 16;

    public static PlmxSessionId Empty => default;

    public bool IsEmpty => High == 0 && Low == 0;

    /// <summary>
    /// The first four bytes, carried in every frame header. 32 bits is enough to
    /// separate consecutive sessions - a stale frame survives with probability
    /// 2^-32 - while keeping per-frame overhead at four bytes.
    /// </summary>
    public uint Tag => (uint)(High & 0xFFFFFFFFUL);

    /// <summary>Truncated form for logs. The full id is never logged.</summary>
    public string Prefix => IsEmpty
        ? "<none>"
        : string.Create(9, High, static (span, high) =>
        {
            for (var index = 0; index < 3; index++)
            {
                var value = (byte)(high >> (index * 8));
                value.TryFormat(span[(index * 2)..], out _, "X2", CultureInfo.InvariantCulture);
            }
            span[6] = '.';
            span[7] = '.';
            span[8] = '.';
        });

    public static PlmxSessionId Create()
    {
        Span<byte> bytes = stackalloc byte[Size];
        // A fresh session must never reuse a previous id, including after a
        // crash-restart within the same millisecond.
        RandomNumberGenerator.Fill(bytes);
        return Read(bytes);
    }

    public static PlmxSessionId Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size) throw new ArgumentException("A session id needs 16 bytes.", nameof(source));
        return new PlmxSessionId(
            BinaryPrimitives.ReadUInt64LittleEndian(source),
            BinaryPrimitives.ReadUInt64LittleEndian(source[8..]));
    }

    public void WriteTo(Span<byte> target)
    {
        if (target.Length < Size) throw new ArgumentException("A session id needs 16 bytes.", nameof(target));
        BinaryPrimitives.WriteUInt64LittleEndian(target, High);
        BinaryPrimitives.WriteUInt64LittleEndian(target[8..], Low);
    }
}

internal readonly record struct PlmxFrameHeader(
    PanelChannelId Channel,
    PlmxFrameType Type,
    int PayloadLength,
    uint Sequence,
    uint SessionTag);

internal readonly record struct PlmxHello(
    uint TransportVersion,
    PanelTransportCapabilities Capabilities,
    PlmxSessionId SessionId);

internal static class PlmxFrame
{
    public const uint Magic = 0x584D4C50; // "PLMX" little-endian.
    public const byte Version = 2;

    /// <summary>
    /// 24 bytes: the v1 header plus a session tag and four reserved bytes. The
    /// reserved field must be zero, which both keeps room for a future
    /// authentication tag selector and adds 32 more bits of structure that a
    /// random or stale byte run has to match before it can be mistaken for a
    /// frame boundary.
    /// </summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// 32 bytes: "PNTR", the transport version, the capability flags, the
    /// 128-bit nonce, and four reserved bytes that must be zero. The reserved
    /// tail is where a future authenticated handshake can put a key selector
    /// without changing the frame size.
    /// </summary>
    public const int HelloPayloadSize = 32;
    private const int HelloReservedOffset = 28;
    public static ReadOnlySpan<byte> HelloMagic => "PNTR"u8;

    private const int ReservedOffset = 20;

    public static void EncodeHeader(
        Span<byte> header,
        PanelChannelId channel,
        PlmxFrameType type,
        int payloadLength,
        uint sequence,
        uint sessionTag)
    {
        if (header.Length < HeaderSize) throw new ArgumentException("Header buffer is too small.", nameof(header));
        ValidateChannel(channel);
        ValidateLength(channel, payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = Version;
        header[5] = (byte)channel;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], sessionTag);
        BinaryPrimitives.WriteUInt32LittleEndian(header[ReservedOffset..], 0);
    }

    public static byte[] EncodeHeader(
        PanelChannelId channel,
        PlmxFrameType type,
        int payloadLength,
        uint sequence,
        uint sessionTag)
    {
        var header = new byte[HeaderSize];
        EncodeHeader(header, channel, type, payloadLength, sequence, sessionTag);
        return header;
    }

    /// <summary>
    /// Non-throwing decode used by the resync scanner, which must be able to
    /// reject a candidate cheaply and move on rather than fail a session.
    /// </summary>
    public static bool TryDecodeHeader(ReadOnlySpan<byte> source, out PlmxFrameHeader header)
    {
        header = default;
        if (source.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic) return false;
        if (!PlmxVersions.IsSupported(source[4])) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source[ReservedOffset..]) != 0) return false;
        var channel = (PanelChannelId)source[5];
        if (channel is < PanelChannelId.Transport or > PanelChannelId.Audio) return false;
        var type = (PlmxFrameType)BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if (!IsKnownType(type)) return false;
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if (payloadLength > (uint)MaximumPayloadFor(channel)) return false;
        if (!IsPayloadLengthValidFor(type, (int)payloadLength)) return false;
        header = new PlmxFrameHeader(
            channel,
            type,
            (int)payloadLength,
            BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[16..]));
        return true;
    }

    public static PlmxFrameHeader DecodeHeader(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize) throw new InvalidDataException("PLMX header must contain 24 bytes.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != Magic)
            throw new InvalidDataException("Corrupted PLMX magic.");
        if (!PlmxVersions.IsSupported(source[4]))
            throw new InvalidDataException($"Unsupported transport version {source[4]}.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(source[ReservedOffset..]) != 0)
            throw new InvalidDataException("PLMX reserved header bytes must be zero.");
        if (!TryDecodeHeader(source, out var header))
            throw new InvalidDataException("Malformed PLMX header.");
        return header;
    }

    public static bool IsKnownType(PlmxFrameType type) =>
        type is PlmxFrameType.Data or PlmxFrameType.Hello or PlmxFrameType.Open
            or PlmxFrameType.Fin or PlmxFrameType.HelloAck;

    /// <summary>
    /// Frame types with a fixed payload shape are validated here so the scanner
    /// can reject a false boundary before it consumes a bogus payload length.
    /// </summary>
    public static bool IsPayloadLengthValidFor(PlmxFrameType type, int payloadLength) => type switch
    {
        PlmxFrameType.Hello or PlmxFrameType.HelloAck => payloadLength == HelloPayloadSize,
        PlmxFrameType.Open or PlmxFrameType.Fin => payloadLength == 0,
        _ => payloadLength >= 0,
    };

    public static void EncodeHello(
        Span<byte> payload,
        PanelTransportCapabilities capabilities,
        PlmxSessionId sessionId)
    {
        if (payload.Length < HelloPayloadSize)
            throw new ArgumentException("HELLO payload needs 32 bytes.", nameof(payload));
        HelloMagic.CopyTo(payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[8..], (uint)capabilities);
        sessionId.WriteTo(payload[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[HelloReservedOffset..], 0);
    }

    public static byte[] EncodeHello(PanelTransportCapabilities capabilities, PlmxSessionId sessionId)
    {
        var payload = new byte[HelloPayloadSize];
        EncodeHello(payload, capabilities, sessionId);
        return payload;
    }

    public static bool TryDecodeHello(ReadOnlySpan<byte> payload, out PlmxHello hello)
    {
        hello = default;
        if (payload.Length != HelloPayloadSize) return false;
        if (!payload[..4].SequenceEqual(HelloMagic)) return false;
        var version = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        if (version != Version) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(payload[HelloReservedOffset..]) != 0) return false;
        var sessionId = PlmxSessionId.Read(payload[12..]);
        // An all-zero id means the peer did not generate one, which would defeat
        // the whole mechanism.
        if (sessionId.IsEmpty) return false;
        hello = new PlmxHello(
            version,
            (PanelTransportCapabilities)BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            sessionId);
        return true;
    }

    /// <summary>
    /// A candidate is only a real handshake when the header and the payload
    /// agree. Matching the four magic bytes alone is not enough: a previous
    /// session's payload can contain them.
    /// </summary>
    public static bool IsHandshakeFrame(
        in PlmxFrameHeader header,
        ReadOnlySpan<byte> payload,
        PlmxFrameType expected,
        out PlmxHello hello)
    {
        hello = default;
        return header.Type == expected
            && header.Channel == PanelChannelId.Transport
            && header.PayloadLength == HelloPayloadSize
            && TryDecodeHello(payload, out hello)
            && hello.SessionId.Tag == header.SessionTag;
    }

    public static void ValidateChannel(PanelChannelId channel)
    {
        if (channel is < PanelChannelId.Transport or > PanelChannelId.Audio)
            throw new InvalidDataException($"Unknown logical channel {(byte)channel}.");
    }

    public static void ValidateLength(PanelChannelId channel, int length)
    {
        var maximum = MaximumPayloadFor(channel);
        if (length < 0 || length > maximum)
            throw new InvalidDataException($"Payload length {length} exceeds the {channel} limit of {maximum} bytes.");
    }

    // The Hardening Wave 1 payload ceilings are unchanged. Session identity and
    // resync must never become a way around them.
    public static int MaximumPayloadFor(PanelChannelId channel) =>
        MultiplexFrame.MaximumPayloadFor(channel);
}

/// <summary>Explicit carrier lifecycle, replacing a set of independent flags.</summary>
internal enum PlmxSessionState
{
    Disconnected,
    OpeningPhysicalTransport,
    SearchingForHandshake,
    Handshaking,
    Established,
    Closing,
    Recovering,
}

/// <summary>
/// Bounds on how much rubbish a resync may absorb before the carrier gives up
/// and reopens. Without them a peer that never speaks PLMX would keep a reader
/// spinning against an endless stream.
/// </summary>
internal sealed record PlmxResyncLimits(int MaximumBytes, TimeSpan MaximumDuration)
{
    public static PlmxResyncLimits Default { get; } = new(1024 * 1024, TimeSpan.FromSeconds(10));
}

internal sealed class PlmxResyncException : IOException
{
    public PlmxResyncException(string message, long discardedBytes)
        : base(message) => DiscardedBytes = discardedBytes;

    public long DiscardedBytes { get; }
}
