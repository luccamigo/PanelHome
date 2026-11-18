using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelVideoProtocolTests
{
    [TestMethod]
    public void Hello_is_exact_little_endian_wire_layout()
    {
        var bytes = PanelVideoProtocol.EncodeHello(new VideoHello(
            640,
            400,
            10,
            VideoCodecMask.ZlibBgra8888 | VideoCodecMask.H264));

        Assert.AreEqual(24, bytes.Length);
        CollectionAssert.AreEqual("PNLV"u8.ToArray(), bytes[..4]);
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.AreEqual(640u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.AreEqual(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)));

        Assert.AreEqual(
            new VideoHello(640, 400, 10, (VideoCodecMask)3),
            PanelVideoProtocol.ParseHello(bytes));
    }

    [TestMethod]
    public void Ack_is_exact_wire_layout_and_round_trips()
    {
        var expected = new VideoAck(VideoCodec.ZlibBgra8888, 640, 400, 10);
        var bytes = PanelVideoProtocol.EncodeAck(expected);

        Assert.AreEqual(24, bytes.Length);
        Assert.AreEqual(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.AreEqual(expected, PanelVideoProtocol.ParseAck(bytes));
    }

    [TestMethod]
    public void Frame_header_is_exact_wire_layout_and_round_trips()
    {
        var frame = new EncodedVideoFrame(
            99,
            123_456,
            2,
            1,
            8,
            [1, 2, 3]);
        var bytes = PanelVideoProtocol.EncodeFrameHeader(frame);

        Assert.AreEqual(44, bytes.Length);
        Assert.AreEqual(4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.AreEqual(
            new VideoFrameHeader(99, 123_456, 2, 1,
                VideoPixelFormat.Bgra8888, 8, 3),
            PanelVideoProtocol.ParseFrameHeader(bytes));
    }

    [TestMethod]
    public void Truncated_hello_is_rejected()
    {
        Assert.ThrowsException<VideoProtocolException>(() =>
            PanelVideoProtocol.ParseHello(new byte[23]));
    }

    [TestMethod]
    public void Absurd_dimensions_are_rejected()
    {
        var bytes = PanelVideoProtocol.EncodeHello(new VideoHello(
            20_000,
            400,
            10,
            VideoCodecMask.ZlibBgra8888));

        var exception = Assert.ThrowsException<VideoProtocolException>(() =>
            PanelVideoProtocol.ParseHello(bytes));
        Assert.AreEqual(VideoRejectCode.InvalidHello, exception.RejectCode);
    }

    [TestMethod]
    public void Reject_has_ack_size_and_error_at_offset_eight()
    {
        var bytes = PanelVideoProtocol.EncodeReject(VideoRejectCode.Busy);

        Assert.AreEqual(24, bytes.Length);
        Assert.AreEqual(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        Assert.AreEqual(VideoRejectCode.Busy, PanelVideoProtocol.ParseReject(bytes));
    }
}
