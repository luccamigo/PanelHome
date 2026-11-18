using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;
using PanelHost.Transport;

namespace PanelHost.Tests;

[TestClass]
public sealed class TransportProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void Frame_header_is_fixed_little_endian_and_round_trips()
    {
        var bytes = MultiplexFrame.EncodeHeader(PanelChannelId.Video, 0x1234, 65_536, 0x10203040);

        CollectionAssert.AreEqual(new byte[] { (byte)'P', (byte)'L', (byte)'M', (byte)'X' }, bytes[..4]);
        Assert.AreEqual(MultiplexFrame.HeaderSize, bytes.Length);
        var decoded = MultiplexFrame.DecodeHeader(bytes);
        Assert.AreEqual(PanelChannelId.Video, decoded.Channel);
        Assert.AreEqual((ushort)0x1234, decoded.Flags);
        Assert.AreEqual(65_536, decoded.PayloadLength);
        Assert.AreEqual(0x10203040u, decoded.Sequence);
    }

    [TestMethod]
    public void Corrupted_oversized_and_unknown_headers_are_rejected_before_allocation()
    {
        var corrupted = MultiplexFrame.EncodeHeader(PanelChannelId.Control, 0, 10, 1);
        corrupted[0] ^= 0x7f;
        Assert.ThrowsException<InvalidDataException>(() => MultiplexFrame.DecodeHeader(corrupted));

        var oversized = MultiplexFrame.EncodeHeader(PanelChannelId.Video, 0, 10, 1);
        BitConverter.TryWriteBytes(oversized.AsSpan(8), checked((uint)MultiplexFrame.MaximumVideoPayload + 1));
        Assert.ThrowsException<InvalidDataException>(() => MultiplexFrame.DecodeHeader(oversized));

        var unknown = MultiplexFrame.EncodeHeader(PanelChannelId.Control, 0, 10, 1);
        unknown[5] = 0xff;
        Assert.ThrowsException<InvalidDataException>(() => MultiplexFrame.DecodeHeader(unknown));
    }

    [TestMethod]
    public void Benchmark_header_is_bounded_versioned_and_little_endian()
    {
        var bytes = TransportBenchmarkProtocol.Header(
            TransportBenchmarkProtocol.Command.Echo,
            1024);

        CollectionAssert.AreEqual("PBMK"u8.ToArray(), bytes[..4]);
        var parsed = TransportBenchmarkProtocol.Parse(bytes);
        Assert.AreEqual(TransportBenchmarkProtocol.Command.Echo, parsed.Command);
        Assert.AreEqual(1024, parsed.PayloadLength);

        BitConverter.TryWriteBytes(
            bytes.AsSpan(8),
            checked((uint)TransportBenchmarkProtocol.MaximumPayload + 1));
        Assert.ThrowsException<InvalidDataException>(() =>
            TransportBenchmarkProtocol.Parse(bytes));
    }

    [TestMethod]
    public void Benchmark_video_snapshot_keeps_every_pipeline_stage_distinct()
    {
        // Capture, receive and send are deliberately separate numbers: a run
        // that misses 60 FPS is a capture/pacing result when driverCaptureFps
        // is already short, and an encoder/carrier result when it is not.
        // Collapsing the chain onto SendFps loses exactly that distinction, so
        // the artifact must round-trip all three.
        var snapshot = new VideoBenchmarkSnapshot(
            Codec: "H264",
            Encoder: "H264 Encoder MFT",
            Width: 1280,
            Height: 800,
            ConfiguredFps: 60,
            NegotiatedFps: 60,
            DriverCaptureFps: 59.4,
            HostReceiveFps: 58.1,
            SendFps: 57.2,
            MegabytesPerSecond: 1.25,
            EncodeLatencyMilliseconds: 4.5,
            CaptureToSendMilliseconds: 9.75,
            DropsOrSequenceGaps: 3);

        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<VideoBenchmarkSnapshot>(json);

        Assert.IsNotNull(parsed);
        Assert.AreEqual("H264 Encoder MFT", parsed.Encoder);
        Assert.AreEqual(60, parsed.ConfiguredFps);
        Assert.AreEqual(59.4, parsed.DriverCaptureFps);
        Assert.AreEqual(58.1, parsed.HostReceiveFps);
        Assert.AreEqual(57.2, parsed.SendFps);
        Assert.AreEqual(9.75, parsed.CaptureToSendMilliseconds);
        Assert.AreNotEqual(parsed.DriverCaptureFps, parsed.SendFps);
        StringAssert.Contains(json, "DriverCaptureFps");
        StringAssert.Contains(json, "HostReceiveFps");
        StringAssert.Contains(json, "CaptureToSendMilliseconds");
    }

    [TestMethod]
    public void Benchmark_options_apply_reproducible_defaults_and_validate_counts()
    {
        var commandLine = new PanelHostCommandLine((_, _) => Task.FromResult(0));
        var model = commandLine.Bind(commandLine.Parse(["--transport-benchmark"]));
        var options = model.CreateBenchmarkOptions();
        Assert.IsNotNull(options);
        Assert.AreEqual(2_000, options.Samples);
        Assert.AreEqual(100, options.WarmupSamples);
        Assert.AreEqual(3, options.Repetitions);

        Assert.AreNotEqual(
            0,
            commandLine.Parse(["--benchmark-samples", "0"]).Errors.Count);
    }

    [TestMethod]
    public void Aoa_interface_match_accepts_only_the_exact_MI_00_hardware_id()
    {
        Assert.IsTrue(AoaInterfaceMatcher.IsSupportedHardwareId(
            ["USB\\VID_18D1&PID_2D01&MI_00"]));
        Assert.IsFalse(AoaInterfaceMatcher.IsSupportedHardwareId(
            ["USB\\VID_18D1&PID_2D01&MI_01"]));
        Assert.IsFalse(AoaInterfaceMatcher.IsSupportedHardwareId(
            ["USB\\VID_18D1&PID_2D00&MI_00"]));
        Assert.IsFalse(AoaInterfaceMatcher.IsSupportedHardwareId(
            ["USB\\VID_18D1&PID_2D01"]));
    }

    [TestMethod]
    public void Aoa_descriptor_match_rejects_wrong_pid_interface_number_or_class()
    {
        var expected = InterfaceDescriptor(0, 0xFF, 0xFF, 0);
        Assert.IsTrue(AoaInterfaceMatcher.IsSupportedDescriptor(
            new UsbDeviceDescriptor(0x18D1, 0x2D01), expected));
        Assert.IsFalse(AoaInterfaceMatcher.IsSupportedDescriptor(
            new UsbDeviceDescriptor(0x18D1, 0x2D00), expected));
        Assert.IsFalse(AoaInterfaceMatcher.IsSupportedDescriptor(
            new UsbDeviceDescriptor(0x18D1, 0x2D01), InterfaceDescriptor(1, 0xFF, 0x42, 1)));
    }

    [TestMethod]
    public void Endpoint_selection_discovers_dynamic_bulk_addresses_and_packet_sizes()
    {
        var selected = WinUsbEndpointSelection.Select(
        [
            new WinUsbEndpoint(0x83, UsbdPipeType.Bulk, 512),
            new WinUsbEndpoint(0x04, UsbdPipeType.Bulk, 512),
            new WinUsbEndpoint(0x85, UsbdPipeType.Interrupt, 64),
        ]);

        Assert.AreEqual((byte)0x83, selected.Read.PipeId);
        Assert.AreEqual((byte)0x04, selected.Write.PipeId);
        Assert.AreEqual((ushort)512, selected.Read.MaximumPacketSize);
    }

    [TestMethod]
    public void Endpoint_selection_rejects_a_missing_bulk_direction()
    {
        var error = Assert.ThrowsException<IOException>(() =>
            WinUsbEndpointSelection.Select(
            [
                new WinUsbEndpoint(0x81, UsbdPipeType.Bulk, 64),
                new WinUsbEndpoint(0x02, UsbdPipeType.Interrupt, 64),
            ]));
        StringAssert.Contains(error.Message, "bulk OUT");
    }

    [TestMethod]
    public async Task Fragmented_physical_reads_preserve_interleaved_logical_streams()
    {
        var (leftPhysical, rightPhysical) = FragmentingDuplexStream.CreatePair(maximumRead: 3);
        await using var left = new MultiplexedTransportSession(leftPhysical, PlmxRole.Host);
        await using var right = new MultiplexedTransportSession(rightPhysical, PlmxRole.Accessory);
        await Task.WhenAll(left.StartAsync(CancellationToken.None), right.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);

        var leftControlTask = left.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var rightControlTask = right.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var leftVideoTask = left.OpenChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        var rightVideoTask = right.AcceptChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        await Task.WhenAll(leftControlTask, rightControlTask, leftVideoTask, rightVideoTask).WaitAsync(Timeout);

        var controlPayload = "touch-down"u8.ToArray();
        var videoPayload = Enumerable.Range(0, 8192).Select(value => (byte)value).ToArray();
        await Task.WhenAll(
            leftControlTask.Result.WriteAsync(controlPayload).AsTask(),
            leftVideoTask.Result.WriteAsync(videoPayload).AsTask()).WaitAsync(Timeout);

        var receivedControl = new byte[controlPayload.Length];
        var receivedVideo = new byte[videoPayload.Length];
        await Task.WhenAll(
            ReadExactlyAsync(rightControlTask.Result, receivedControl),
            ReadExactlyAsync(rightVideoTask.Result, receivedVideo)).WaitAsync(Timeout);
        CollectionAssert.AreEqual(controlPayload, receivedControl);
        CollectionAssert.AreEqual(videoPayload, receivedVideo);
        Assert.IsTrue(left.Metrics.ControlBytesTx >= controlPayload.Length);
        Assert.IsTrue(left.Metrics.VideoBytesTx >= videoPayload.Length);
        Assert.IsTrue(left.Metrics.VideoQueueDepth <= 1);
        Assert.IsTrue(left.Metrics.WireBytesTx > left.Metrics.PayloadBytesTx);
    }

    [TestMethod]
    public async Task Logical_stream_writes_larger_than_one_frame_are_fragmented_transparently()
    {
        var (leftPhysical, rightPhysical) = FragmentingDuplexStream.CreatePair(maximumRead: 4096);
        await using var left = new MultiplexedTransportSession(leftPhysical, PlmxRole.Host);
        await using var right = new MultiplexedTransportSession(rightPhysical, PlmxRole.Accessory);
        await Task.WhenAll(left.StartAsync(CancellationToken.None), right.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var senderTask = left.OpenChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        var receiverTask = right.AcceptChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        await Task.WhenAll(senderTask, receiverTask).WaitAsync(Timeout);
        var payload = Enumerable.Range(0, MultiplexFrame.MaximumOtherPayload * 2 + 17)
            .Select(index => (byte)(index * 31 + 17))
            .ToArray();
        var received = new byte[payload.Length];

        await Task.WhenAll(
            senderTask.Result.WriteAsync(payload).AsTask(),
            ReadExactlyAsync(receiverTask.Result, received)).WaitAsync(Timeout);

        CollectionAssert.AreEqual(payload, received);
    }

    [TestMethod]
    public async Task Disconnect_mid_frame_is_exposed_as_transport_disconnect()
    {
        var (leftPhysical, rightPhysical) = FragmentingDuplexStream.CreatePair(maximumRead: 2);
        await using var left = new MultiplexedTransportSession(leftPhysical, PlmxRole.Host);
        await using var right = new MultiplexedTransportSession(rightPhysical, PlmxRole.Accessory);
        await Task.WhenAll(left.StartAsync(CancellationToken.None), right.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var localTask = left.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var remoteTask = right.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        await Task.WhenAll(localTask, remoteTask).WaitAsync(Timeout);

        await leftPhysical.DisposeAsync();
        var error = await Assert.ThrowsExceptionAsync<PanelTransportDisconnectedException>(async () =>
        {
            var one = new byte[1];
            _ = await remoteTask.Result.ReadAsync(one).AsTask().WaitAsync(Timeout);
        });
        StringAssert.Contains(error.Message, "transport", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task Video_flood_keeps_one_pending_write_and_control_overtakes_backlog()
    {
        var (leftPhysical, rightPhysical) = FragmentingDuplexStream.CreatePair(
            maximumRead: 4096,
            writeDelay: TimeSpan.FromMilliseconds(3));
        await using var left = new MultiplexedTransportSession(leftPhysical, PlmxRole.Host);
        await using var right = new MultiplexedTransportSession(rightPhysical, PlmxRole.Accessory);
        await Task.WhenAll(left.StartAsync(CancellationToken.None), right.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var leftControlTask = left.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var rightControlTask = right.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var leftVideoTask = left.OpenChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        var rightVideoTask = right.AcceptChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        await Task.WhenAll(leftControlTask, rightControlTask, leftVideoTask, rightVideoTask).WaitAsync(Timeout);

        var videoPayload = new byte[256 * 1024];
        var flood = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(async () => await leftVideoTask.Result.WriteAsync(videoPayload)))
            .ToArray();
        var deadline = DateTime.UtcNow + Timeout;
        while (left.Metrics.VideoQueueDepth == 0 && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.AreEqual(1, left.Metrics.VideoQueueDepth);

        await leftControlTask.Result.WriteAsync("ping"u8.ToArray()).AsTask().WaitAsync(Timeout);
        Assert.IsFalse(Task.WhenAll(flood).IsCompleted, "Control waited behind the complete video backlog.");
        Assert.IsTrue(left.Metrics.VideoQueueDepth <= 1);
        await Task.WhenAll(flood).WaitAsync(Timeout);
    }

    [TestMethod]
    public async Task Native_transport_reconnects_the_shared_session_after_disconnect()
    {
        var peers = new List<MultiplexedTransportSession>();
        var peerTasks = new List<Task>();
        ValueTask<Stream> OpenPhysicalAsync(CancellationToken cancellationToken)
        {
            var (hostPhysical, peerPhysical) = FragmentingDuplexStream.CreatePair(maximumRead: 5);
            var peer = new MultiplexedTransportSession(peerPhysical, PlmxRole.Accessory);
            peers.Add(peer);
            peerTasks.Add(Task.Run(async () =>
            {
                await peer.StartAsync(cancellationToken);
                _ = await peer.OpenChannelAsync(PanelChannelId.Control, cancellationToken);
            }, cancellationToken));
            return ValueTask.FromResult<Stream>(hostPhysical);
        }

        await using var transport = new NativeUsbTransport(
            "in-memory-test",
            TextWriter.Null,
            OpenPhysicalAsync);
        await using var listener = transport.Listen(PanelChannelId.Control);
        await listener.StartAsync(CancellationToken.None);
        await using var first = await listener.AcceptAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.AreEqual(1, peers.Count);

        await peers[0].DisposeAsync();
        await using var second = await listener.AcceptAsync(CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.AreEqual(2, peers.Count);
        Assert.IsTrue(transport.Metrics.Reconnects >= 1);
        await Task.WhenAll(peerTasks).WaitAsync(Timeout);
        foreach (var peer in peers.Skip(1)) await peer.DisposeAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target[offset..]);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static UsbInterfaceDescriptor InterfaceDescriptor(
        byte number,
        byte interfaceClass,
        byte interfaceSubClass,
        byte interfaceProtocol) =>
        new()
        {
            InterfaceNumber = number,
            InterfaceClass = interfaceClass,
            InterfaceSubClass = interfaceSubClass,
            InterfaceProtocol = interfaceProtocol,
        };

    private sealed class FragmentingDuplexStream : Stream
    {
        private readonly ChannelReader<byte[]> _incoming;
        private readonly ChannelWriter<byte[]> _outgoing;
        private readonly int _maximumRead;
        private readonly TimeSpan _writeDelay;
        private byte[]? _current;
        private int _offset;

        private FragmentingDuplexStream(
            ChannelReader<byte[]> incoming,
            ChannelWriter<byte[]> outgoing,
            int maximumRead,
            TimeSpan writeDelay)
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _maximumRead = maximumRead;
            _writeDelay = writeDelay;
        }

        public static (FragmentingDuplexStream Left, FragmentingDuplexStream Right) CreatePair(
            int maximumRead,
            TimeSpan writeDelay = default)
        {
            var left = Channel.CreateUnbounded<byte[]>();
            var right = Channel.CreateUnbounded<byte[]>();
            return (
                new FragmentingDuplexStream(left.Reader, right.Writer, maximumRead, writeDelay),
                new FragmentingDuplexStream(right.Reader, left.Writer, maximumRead, writeDelay));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _incoming.WaitToReadAsync(cancellationToken)) return 0;
                if (!_incoming.TryRead(out _current)) continue;
                _offset = 0;
            }
            var count = Math.Min(Math.Min(buffer.Length, _maximumRead), _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_writeDelay > TimeSpan.Zero) await Task.Delay(_writeDelay, cancellationToken);
            await _outgoing.WriteAsync(buffer.ToArray(), cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _outgoing.TryComplete();
            base.Dispose(disposing);
        }
    }
}
