using System.Buffers.Binary;

namespace PanelHost.Transport;

internal static class MultiplexFrame
{
    public const uint Magic = 0x584D4C50; // "PLMX" when written little-endian.
    public const byte Version = 1;
    public const int HeaderSize = 16;
    public const int MaximumControlPayload = 1024 * 1024;
    public const int MaximumVideoPayload = 16 * 1024 * 1024;
    public const int MaximumOtherPayload = 1024 * 1024;

    /// <summary>
    /// How much of a logical write goes into one frame. This is a scheduling
    /// knob, not a wire limit: the peer validates against the maximum payload
    /// above, so any fragment size below it stays protocol compatible. Smaller
    /// fragments return control to the writer's priority check more often.
    /// </summary>
    public const int DefaultFragmentBytes = 64 * 1024;
    public const int MinimumFragmentBytes = 512;

    public static int ConfiguredFragmentBytes { get; } = SanitizeFragmentBytes(
        int.TryParse(
            Environment.GetEnvironmentVariable("PANELOS_PLMX_FRAGMENT"),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configured)
            ? configured
            : DefaultFragmentBytes);

    /// <summary>
    /// Fragments stay a multiple of the 512-byte bulk max packet so a frame
    /// boundary never lands mid-packet on the wire.
    /// </summary>
    public static int SanitizeFragmentBytes(int value)
    {
        value = Math.Clamp(value, MinimumFragmentBytes, MaximumVideoPayload);
        return value - (value % MinimumFragmentBytes);
    }

    public static void EncodeHeader(
        Span<byte> header,
        PanelChannelId channel,
        ushort flags,
        int payloadLength,
        uint sequence)
    {
        if (header.Length < HeaderSize) throw new ArgumentException("Header buffer is too small.", nameof(header));
        ValidateChannel(channel);
        ValidateLength(channel, payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        header[4] = Version;
        header[5] = (byte)channel;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], checked((uint)payloadLength));
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], sequence);
    }

    public static byte[] EncodeHeader(PanelChannelId channel, ushort flags, int payloadLength, uint sequence)
    {
        var header = new byte[HeaderSize];
        EncodeHeader(header, channel, flags, payloadLength, sequence);
        return header;
    }

    public static MultiplexFrameHeader DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderSize) throw new InvalidDataException("Multiplex header must contain 16 bytes.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic) throw new InvalidDataException("Corrupted multiplex magic.");
        if (header[4] != Version) throw new InvalidDataException($"Unsupported transport version {header[4]}.");
        var channel = (PanelChannelId)header[5];
        ValidateChannel(channel);
        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        ValidateLength(channel, payloadLength);
        return new MultiplexFrameHeader(
            channel,
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..]),
            payloadLength,
            BinaryPrimitives.ReadUInt32LittleEndian(header[12..]));
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

    public static int MaximumPayloadFor(PanelChannelId channel) =>
        channel switch
        {
            PanelChannelId.Control => MaximumControlPayload,
            PanelChannelId.Video => MaximumVideoPayload,
            _ => MaximumOtherPayload,
        };
}

internal readonly record struct MultiplexFrameHeader(
    PanelChannelId Channel,
    ushort Flags,
    int PayloadLength,
    uint Sequence);
