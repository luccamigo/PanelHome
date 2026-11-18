using System.Threading.Channels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Transport;

namespace PanelHost.Tests;

/// <summary>
/// Guards the physical-layer behaviour the CPU work depends on: zero-length
/// packets are not end of stream, buffering does not lose or reorder bytes,
/// frames reach the wire in one transfer, pooled buffers have one owner, and
/// cancellation still unblocks a pending transfer.
/// </summary>
[TestClass]
public sealed class TransportPerformanceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Zero_length_packet_is_retried_instead_of_reported_as_end_of_stream()
    {
        // A bulk transfer whose size is an exact multiple of maxPacketSize is
        // terminated by a zero-length packet. Treating that as EOF is what
        // previously dropped a healthy session.
        var reader = new FakeBulkPipe(64, [[], [], "payload"u8.ToArray()]);
        await using var stream = new WinUsbPipeStream(reader, new FakeBulkPipe(64, []));

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(Timeout);

        Assert.AreEqual(7, read);
        CollectionAssert.AreEqual("payload"u8.ToArray(), buffer[..read]);
        Assert.AreEqual(3, reader.TransferCount, "The two empty transfers must each cost one retry.");
    }

    [TestMethod]
    public async Task One_physical_read_serves_several_logical_reads()
    {
        // The reader loop asks for a 16-byte header and then a payload. Both
        // must come out of a single staged transfer, otherwise every frame
        // costs at least two USB round trips.
        var wire = new byte[16 + 8];
        for (var index = 0; index < wire.Length; index++) wire[index] = (byte)index;
        var reader = new FakeBulkPipe(64, [wire]);
        var profile = new TransportIoProfile();
        await using var stream = new WinUsbPipeStream(reader, new FakeBulkPipe(64, []), profile);

        var header = new byte[16];
        var payload = new byte[8];
        await ReadExactlyAsync(stream, header);
        await ReadExactlyAsync(stream, payload);

        Assert.AreEqual(1, reader.TransferCount);
        Assert.AreEqual(1, profile.Snapshot().ReadCalls);
        CollectionAssert.AreEqual(wire[..16], header);
        CollectionAssert.AreEqual(wire[16..], payload);
    }

    [TestMethod]
    public async Task Writes_are_chunked_to_whole_max_packets_so_no_short_packet_splits_a_frame()
    {
        var writer = new FakeBulkPipe(1024, []);
        await using var stream = new WinUsbPipeStream(new FakeBulkPipe(1024, []), writer);
        var payload = new byte[2560];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 7);

        await stream.WriteAsync(payload).AsTask().WaitAsync(Timeout);

        CollectionAssert.AreEqual(new[] { 1024, 1024, 512 }, writer.WrittenLengths.ToArray());
        foreach (var length in writer.WrittenLengths.Take(writer.WrittenLengths.Count - 1))
            Assert.AreEqual(0, length % 512, "A mid-frame chunk must be a whole number of max packets.");
        CollectionAssert.AreEqual(payload, writer.Written.SelectMany(chunk => chunk).ToArray());
    }

    [TestMethod]
    public async Task A_frame_that_fits_the_fragment_reaches_the_wire_as_one_write()
    {
        var (left, right) = CountingDuplexStream.CreatePair();
        await using var session = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 4096);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 4096);
        await Task.WhenAll(session.StartAsync(CancellationToken.None), peer.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var senderTask = session.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var receiverTask = peer.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        await Task.WhenAll(senderTask, receiverTask).WaitAsync(Timeout);
        var before = left.WriteCount;
        var ioBefore = session.Metrics.Io.Snapshot();

        var payload = "touch-down"u8.ToArray();
        await senderTask.Result.WriteAsync(payload).AsTask().WaitAsync(Timeout);

        Assert.AreEqual(1, left.WriteCount - before, "Header and payload must share one physical write.");
        var received = new byte[payload.Length];
        await ReadExactlyAsync(receiverTask.Result, received).WaitAsync(Timeout);
        CollectionAssert.AreEqual(payload, received);
        var io = session.Metrics.Io.Snapshot() - ioBefore;
        Assert.AreEqual(1, io.FrameWritesCoalesced);
        Assert.AreEqual(0, io.FrameWritesSplit);
    }

    [TestMethod]
    public async Task A_payload_larger_than_the_fragment_is_split_into_frames_that_keep_order()
    {
        var (left, right) = CountingDuplexStream.CreatePair();
        await using var session = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 512);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 512);
        await Task.WhenAll(session.StartAsync(CancellationToken.None), peer.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var senderTask = session.OpenChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        var receiverTask = peer.AcceptChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        await Task.WhenAll(senderTask, receiverTask).WaitAsync(Timeout);

        var payload = new byte[512 * 5 + 13];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 31 + 17);
        var received = new byte[payload.Length];
        await Task.WhenAll(
            senderTask.Result.WriteAsync(payload).AsTask(),
            ReadExactlyAsync(receiverTask.Result, received)).WaitAsync(Timeout);

        CollectionAssert.AreEqual(payload, received);
        Assert.AreEqual(6, session.Metrics.Io.FragmentsTx);
    }

    [TestMethod]
    public async Task A_completed_write_releases_the_caller_buffer_so_slicing_cannot_leak_state()
    {
        // Fragments are sliced, not copied. That is only sound because the
        // caller stays parked until the fragment is on the wire, so overwriting
        // the source afterwards must not change what the peer already received.
        var (left, right) = CountingDuplexStream.CreatePair();
        await using var session = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 512);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 512);
        await Task.WhenAll(session.StartAsync(CancellationToken.None), peer.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var senderTask = session.OpenChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        var receiverTask = peer.AcceptChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        await Task.WhenAll(senderTask, receiverTask).WaitAsync(Timeout);

        var payload = new byte[1500];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        var expected = payload.ToArray();
        await senderTask.Result.WriteAsync(payload).AsTask().WaitAsync(Timeout);
        Array.Fill(payload, (byte)0xEE);

        var received = new byte[expected.Length];
        await ReadExactlyAsync(receiverTask.Result, received).WaitAsync(Timeout);
        CollectionAssert.AreEqual(expected, received);
    }

    [TestMethod]
    public async Task Received_payload_buffers_return_to_the_pool_once_the_consumer_drains_them()
    {
        var (left, right) = CountingDuplexStream.CreatePair();
        await using var session = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 512);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 512);
        await Task.WhenAll(session.StartAsync(CancellationToken.None), peer.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var senderTask = session.OpenChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        var receiverTask = peer.AcceptChannelAsync(PanelChannelId.Assets, CancellationToken.None).AsTask();
        await Task.WhenAll(senderTask, receiverTask).WaitAsync(Timeout);
        var before = peer.Metrics.Io.Snapshot();

        var payload = new byte[512 * 4];
        var received = new byte[payload.Length];
        await Task.WhenAll(
            senderTask.Result.WriteAsync(payload).AsTask(),
            ReadExactlyAsync(receiverTask.Result, received)).WaitAsync(Timeout);

        var profile = peer.Metrics.Io.Snapshot() - before;
        Assert.AreEqual(4, profile.PayloadRentals, "One rental per received fragment.");
        Assert.AreEqual(
            profile.PayloadRentals,
            profile.PayloadReturns,
            "Every rented payload must be returned exactly once after it is drained.");
    }

    [TestMethod]
    public async Task A_cancelled_read_unblocks_instead_of_leaving_a_transfer_pending()
    {
        var reader = new FakeBulkPipe(64, [], blockForever: true);
        await using var stream = new WinUsbPipeStream(reader, new FakeBulkPipe(64, []));
        using var cancellation = new CancellationTokenSource();

        var read = stream.ReadAsync(new byte[16], cancellation.Token).AsTask();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));

        // TaskCanceledException derives from OperationCanceledException; either
        // shape is acceptable as long as the caller is released.
        try
        {
            await read.WaitAsync(Timeout);
            Assert.Fail("A cancelled transfer must not complete successfully.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public void Fragment_and_buffer_sizes_stay_whole_max_packets_within_their_bounds()
    {
        Assert.AreEqual(512, MultiplexFrame.SanitizeFragmentBytes(1));
        Assert.AreEqual(512, MultiplexFrame.SanitizeFragmentBytes(1000));
        Assert.AreEqual(65_536, MultiplexFrame.SanitizeFragmentBytes(65_536));
        Assert.AreEqual(
            MultiplexFrame.MaximumVideoPayload,
            MultiplexFrame.SanitizeFragmentBytes(int.MaxValue));

        Assert.AreEqual(
            NativeUsbTransportOptions.DefaultReadBufferBytes,
            NativeUsbTransportOptions.Sanitize(null, NativeUsbTransportOptions.DefaultReadBufferBytes));
        Assert.AreEqual(32_768, NativeUsbTransportOptions.Sanitize("32768", 1024));
        Assert.AreEqual(32_768, NativeUsbTransportOptions.Sanitize("33000", 1024));
        Assert.AreEqual(512, NativeUsbTransportOptions.Sanitize("1", 1024));
    }

    [TestMethod]
    public async Task Control_overtakes_a_saturating_video_stream_within_one_fragment()
    {
        // The point of a small fragment is that control never waits for a whole
        // multi-megabyte video write, only for the fragment in flight.
        var (left, right) = CountingDuplexStream.CreatePair(writeDelay: TimeSpan.FromMilliseconds(2));
        await using var session = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 512);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 512);
        await Task.WhenAll(session.StartAsync(CancellationToken.None), peer.StartAsync(CancellationToken.None))
            .WaitAsync(Timeout);
        var control = session.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var peerControl = peer.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask();
        var video = session.OpenChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        var peerVideo = peer.AcceptChannelAsync(PanelChannelId.Video, CancellationToken.None).AsTask();
        await Task.WhenAll(control, peerControl, video, peerVideo).WaitAsync(Timeout);

        // Sized against the platform timer granularity: the point is that
        // control jumps the queue, not how long the flood itself takes.
        var videoFlood = Task.Run(async () =>
        {
            for (var index = 0; index < 4; index++)
                await video.Result.WriteAsync(new byte[8 * 1024]);
        });
        await Task.Delay(20);
        var started = DateTime.UtcNow;
        await control.Result.WriteAsync("ping"u8.ToArray()).AsTask().WaitAsync(Timeout);
        var controlWait = DateTime.UtcNow - started;

        Assert.IsFalse(videoFlood.IsCompleted, "The video flood must still be in flight.");
        Assert.IsTrue(
            controlWait < TimeSpan.FromMilliseconds(500),
            $"Control waited {controlWait.TotalMilliseconds:F0} ms behind video.");
        await videoFlood.WaitAsync(Timeout);
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

    /// <summary>Scripted bulk pipe standing in for a WinUSB endpoint.</summary>
    private sealed class FakeBulkPipe(int bufferBytes, IReadOnlyList<byte[]> reads, bool blockForever = false)
        : IBulkPipe
    {
        private int _readIndex;

        public byte[] Buffer { get; } = new byte[bufferBytes];
        public bool Drained => true;
        public int TransferCount { get; private set; }
        public List<byte[]> Written { get; } = [];
        public List<int> WrittenLengths => Written.ConvertAll(chunk => chunk.Length);

        public async ValueTask<(int Transferred, bool CompletedInline)> TransferAsync(
            int count,
            CancellationToken cancellationToken)
        {
            TransferCount++;
            if (blockForever)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            if (_readIndex < reads.Count)
            {
                var chunk = reads[_readIndex++];
                chunk.CopyTo(Buffer, 0);
                return (chunk.Length, true);
            }
            Written.Add(Buffer.AsSpan(0, count).ToArray());
            return (count, true);
        }

        public void Dispose() { }
    }

    /// <summary>Duplex pipe that counts physical writes.</summary>
    private sealed class CountingDuplexStream : Stream
    {
        private readonly ChannelReader<byte[]> _incoming;
        private readonly ChannelWriter<byte[]> _outgoing;
        private readonly TimeSpan _writeDelay;
        private byte[]? _current;
        private int _offset;
        private int _writeCount;

        private CountingDuplexStream(
            ChannelReader<byte[]> incoming,
            ChannelWriter<byte[]> outgoing,
            TimeSpan writeDelay)
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _writeDelay = writeDelay;
        }

        public int WriteCount => Volatile.Read(ref _writeCount);

        public static (CountingDuplexStream Left, CountingDuplexStream Right) CreatePair(TimeSpan writeDelay = default)
        {
            var left = Channel.CreateUnbounded<byte[]>();
            var right = Channel.CreateUnbounded<byte[]>();
            return (
                new CountingDuplexStream(left.Reader, right.Writer, writeDelay),
                new CountingDuplexStream(right.Reader, left.Writer, writeDelay));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _incoming.WaitToReadAsync(cancellationToken)) return 0;
                if (!_incoming.TryRead(out _current)) continue;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_writeDelay > TimeSpan.Zero) await Task.Delay(_writeDelay, cancellationToken);
            Interlocked.Increment(ref _writeCount);
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
