using System.Buffers.Binary;

namespace PanelHost.Video;

internal static class PanelVideoProtocol
{
    internal const int Port = 8766;
    internal const ushort Version = 1;
    internal const int HelloSize = 24;
    internal const int AckSize = 24;
    internal const int FrameHeaderSize = 44;
    internal const int H264PacketHeaderSize = 40;
    internal const uint MaximumHelloDimension = 16_384;
    internal const uint MaximumHelloFps = 60;
    internal const uint MaximumCompressedBytes = 16 * 1024 * 1024;
    internal const uint MaximumH264PayloadBytes = 2 * 1024 * 1024;
    internal const uint MaximumUncompressedBytes = 128 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "PNLV"u8;

    internal static async Task<VideoHello> ReadHelloAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[HelloSize];
        await stream.ReadExactlyAsync(bytes, cancellationToken)
            .ConfigureAwait(false);
        return ParseHello(bytes);
    }

    internal static VideoHello ParseHello(ReadOnlySpan<byte> bytes)
    {
        ValidatePreamble(bytes, HelloSize, VideoMessageType.Hello);
        var hello = new VideoHello(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            (VideoCodecMask)BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
        if (hello.MaxWidth == 0 ||
            hello.MaxHeight == 0 ||
            hello.MaxWidth > MaximumHelloDimension ||
            hello.MaxHeight > MaximumHelloDimension ||
            hello.MaxFps == 0 || hello.MaxFps > MaximumHelloFps)
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidHello,
                "Video hello contains invalid dimensions or FPS.");
        }

        if ((hello.SupportedCodecs &
                (VideoCodecMask.H264 | VideoCodecMask.ZlibBgra8888)) == 0)
        {
            throw new VideoProtocolException(
                VideoRejectCode.UnsupportedCodec,
                "Video client does not support a PanelHost video codec.");
        }

        return hello;
    }

    internal static byte[] EncodeHello(VideoHello hello)
    {
        var bytes = new byte[HelloSize];
        WritePreamble(bytes, VideoMessageType.Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), hello.MaxWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), hello.MaxHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), hello.MaxFps);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(20),
            (uint)hello.SupportedCodecs);
        return bytes;
    }

    internal static byte[] EncodeAck(VideoAck ack)
    {
        var bytes = new byte[AckSize];
        WritePreamble(bytes, VideoMessageType.Ack);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(8),
            (uint)ack.SelectedCodec);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), ack.MaxWidth);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), ack.MaxHeight);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), ack.Fps);
        return bytes;
    }

    internal static VideoAck ParseAck(ReadOnlySpan<byte> bytes)
    {
        ValidatePreamble(bytes, AckSize, VideoMessageType.Ack);
        return new VideoAck(
            (VideoCodec)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
    }

    internal static byte[] EncodeReject(VideoRejectCode errorCode)
    {
        var bytes = new byte[AckSize];
        WritePreamble(bytes, VideoMessageType.Reject);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)errorCode);
        return bytes;
    }

    internal static VideoRejectCode ParseReject(ReadOnlySpan<byte> bytes)
    {
        ValidatePreamble(bytes, AckSize, VideoMessageType.Reject);
        return (VideoRejectCode)BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
    }

    internal static byte[] EncodeFrameHeader(EncodedVideoFrame frame)
    {
        var bytes = new byte[FrameHeaderSize];
        WritePreamble(bytes, VideoMessageType.Frame);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), frame.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(16),
            frame.CaptureTimestampMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), frame.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), frame.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(32),
            (uint)VideoPixelFormat.Bgra8888);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(36),
            frame.UncompressedBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(40),
            checked((uint)frame.CompressedPixels.Length));
        return bytes;
    }

    internal static VideoFrameHeader ParseFrameHeader(ReadOnlySpan<byte> bytes)
    {
        ValidatePreamble(bytes, FrameHeaderSize, VideoMessageType.Frame);
        var header = new VideoFrameHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]),
            (VideoPixelFormat)BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[36..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]));
        if (header.Sequence == 0 ||
            header.Width == 0 || header.Height == 0 ||
            header.Width > MaximumHelloDimension ||
            header.Height > MaximumHelloDimension ||
            header.PixelFormat != VideoPixelFormat.Bgra8888 ||
            header.UncompressedBytes == 0 ||
            header.UncompressedBytes > MaximumUncompressedBytes ||
            header.CompressedBytes == 0 ||
            header.CompressedBytes > MaximumCompressedBytes)
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidFrame,
                "Video frame header is invalid.");
        }

        var expectedBytes = checked((ulong)header.Width * header.Height * 4);
        if (header.UncompressedBytes != expectedBytes)
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidFrame,
                "Video frame byte count does not match its dimensions.");
        }

        return header;
    }

    internal static byte[] EncodeH264PacketHeader(H264VideoPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ValidateH264Packet(packet);
        var bytes = new byte[H264PacketHeaderSize];
        WritePreamble(bytes, VideoMessageType.H264Packet);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), packet.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(16),
            packet.CaptureTimestampMicros);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)packet.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(28),
            checked((uint)packet.Payload.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(32),
            checked((ulong)Math.Max(0, packet.EncodeDurationMicros)));
        return bytes;
    }

    internal static H264PacketHeader ParseH264PacketHeader(ReadOnlySpan<byte> bytes)
    {
        ValidatePreamble(bytes, H264PacketHeaderSize, VideoMessageType.H264Packet);
        var header = new H264PacketHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            (H264PacketFlags)BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]));
        ValidateH264Header(header);
        return header;
    }

    internal static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePreamble(
        ReadOnlySpan<byte> bytes,
        int expectedSize,
        VideoMessageType expectedType)
    {
        if (bytes.Length != expectedSize ||
            !bytes[..4].SequenceEqual(Magic))
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidHello,
                "Invalid Panel Video Protocol magic or message size.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        if (version != Version)
        {
            throw new VideoProtocolException(
                VideoRejectCode.UnsupportedVersion,
                $"Unsupported Panel Video Protocol version {version}.");
        }

        var type = (VideoMessageType)BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        if (type != expectedType)
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidHello,
                $"Unexpected Panel Video Protocol message type {(ushort)type}.");
        }
    }

    private static void WritePreamble(Span<byte> bytes, VideoMessageType type)
    {
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[6..], (ushort)type);
    }

    private static void ValidateH264Packet(H264VideoPacket packet)
    {
        var header = new H264PacketHeader(
            packet.Sequence,
            packet.CaptureTimestampMicros,
            packet.Flags,
            checked((uint)packet.Payload.Length),
            checked((ulong)Math.Max(0, packet.EncodeDurationMicros)));
        ValidateH264Header(header);
    }

    private static void ValidateH264Header(H264PacketHeader header)
    {
        const H264PacketFlags knownFlags =
            H264PacketFlags.Config |
            H264PacketFlags.KeyFrame |
            H264PacketFlags.EndOfAccessUnit;
        if (header.Sequence == 0 ||
            header.PayloadBytes == 0 ||
            header.PayloadBytes > MaximumH264PayloadBytes ||
            header.Flags == H264PacketFlags.None ||
            (header.Flags & ~knownFlags) != 0 ||
            ((header.Flags & H264PacketFlags.Config) == 0 &&
                (header.Flags & H264PacketFlags.EndOfAccessUnit) == 0))
        {
            throw new VideoProtocolException(
                VideoRejectCode.InvalidFrame,
                "H.264 packet header is invalid.");
        }
    }
}

internal enum VideoMessageType : ushort
{
    Hello = 1,
    Ack = 2,
    Reject = 3,
    Frame = 4,
    H264Packet = 5,
}

internal enum VideoCodec : uint
{
    ZlibBgra8888 = 1,
    H264 = 2,
}

[Flags]
internal enum VideoCodecMask : uint
{
    None = 0,
    ZlibBgra8888 = 1,
    H264 = 2,
}

internal enum VideoPixelFormat : uint
{
    Bgra8888 = 1,
}

[Flags]
internal enum H264PacketFlags : uint
{
    None = 0,
    Config = 1,
    KeyFrame = 2,
    EndOfAccessUnit = 4,
}

internal enum VideoRejectCode : uint
{
    InvalidHello = 1,
    UnsupportedVersion = 2,
    UnsupportedCodec = 3,
    Busy = 4,
    DesktopNotRunning = 5,
    InvalidFrame = 6,
}

internal readonly record struct VideoHello(
    uint MaxWidth,
    uint MaxHeight,
    uint MaxFps,
    VideoCodecMask SupportedCodecs);

internal readonly record struct VideoAck(
    VideoCodec SelectedCodec,
    uint MaxWidth,
    uint MaxHeight,
    uint Fps);

internal readonly record struct VideoFrameHeader(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    uint Width,
    uint Height,
    VideoPixelFormat PixelFormat,
    uint UncompressedBytes,
    uint CompressedBytes);

internal readonly record struct H264PacketHeader(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    H264PacketFlags Flags,
    uint PayloadBytes,
    ulong EncodeDurationMicros);

internal sealed record H264VideoPacket(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    H264PacketFlags Flags,
    byte[] Payload,
    long EncodeDurationMicros);

internal sealed class VideoProtocolException : IOException
{
    internal VideoProtocolException(VideoRejectCode rejectCode, string message)
        : base(message)
    {
        RejectCode = rejectCode;
    }

    internal VideoRejectCode RejectCode { get; }
}
