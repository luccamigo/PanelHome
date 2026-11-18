using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Transport;

namespace PanelHost.Tests;

/// <summary>
/// PLMX v2 exists because v1 could not tell its own bytes from its
/// predecessor's. These tests pin that: a session opened over a pipe that still
/// holds the previous session's frames must resynchronize onto its own
/// handshake, ignore everything before it, and stay alive afterwards.
/// </summary>
[TestClass]
public sealed class PlmxV2HandshakeDeadlineTests
{
    /// <summary>
    /// Physically observed 2026-08-19: the accessory had not opened its endpoint
    /// when the host sent its single HELLO, so the host waited for a HELLO_ACK
    /// that could never arrive. The resync limits bound discarded bytes and
    /// elapsed time, but both are only checked between reads, and the read never
    /// returned - metrics sat at sessions=1 reconnectAttempts=1 errors=0 forever.
    /// A silent peer must therefore fail the handshake, not hang on it.
    /// </summary>
    [TestMethod]
    public async Task Handshake_against_a_silent_peer_fails_within_the_resync_bound()
    {
        using var stream = new SilentDuplexStream();
        await using var session = new MultiplexedTransportSession(
            stream,
            PlmxRole.Host,
            resyncLimits: new PlmxResyncLimits(1024 * 1024, TimeSpan.FromMilliseconds(400)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var failure = await Assert.ThrowsExceptionAsync<PlmxResyncException>(
            () => session.StartAsync(CancellationToken.None));
        started.Stop();

        StringAssert.Contains(failure.Message, "HelloAck");
        Assert.IsTrue(
            started.Elapsed < TimeSpan.FromSeconds(5),
            $"Handshake took {started.Elapsed.TotalSeconds:F1}s; the bound must be enforced, not merely documented.");
    }

    /// <summary>
    /// A caller-requested shutdown must stay a cancellation: turning it into a
    /// resync failure would make the carrier retry a session the caller closed.
    /// </summary>
    [TestMethod]
    public async Task Caller_cancellation_is_not_reported_as_a_resync_failure()
    {
        using var stream = new SilentDuplexStream();
        await using var session = new MultiplexedTransportSession(
            stream,
            PlmxRole.Host,
            resyncLimits: new PlmxResyncLimits(1024 * 1024, TimeSpan.FromSeconds(30)));
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => session.StartAsync(caller.Token));
    }

    /// <summary>Accepts writes, never yields a byte, stays cancellable.</summary>
    private sealed class SilentDuplexStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}

[TestClass]
public sealed class PlmxV2Tests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly PlmxSessionId SessionA = new(0x1122334455667788UL, 0x99AABBCCDDEEFF00UL);
    private static readonly PlmxSessionId SessionB = new(0x0F1E2D3C4B5A6978UL, 0x8796A5B4C3D2E1F0UL);

    [TestMethod]
    public void A_session_id_is_random_128_bits_and_never_repeats()
    {
        var ids = new HashSet<PlmxSessionId>();
        for (var index = 0; index < 512; index++) Assert.IsTrue(ids.Add(PlmxSessionId.Create()));
        Assert.IsFalse(PlmxSessionId.Create().IsEmpty);
        // The tag is the first four bytes, so it must agree with the wire form.
        var id = PlmxSessionId.Create();
        var bytes = new byte[PlmxSessionId.Size];
        id.WriteTo(bytes);
        Assert.AreEqual(BinaryPrimitives.ReadUInt32LittleEndian(bytes), id.Tag);
        Assert.AreEqual(id, PlmxSessionId.Read(bytes));
    }

    [TestMethod]
    public void The_logged_session_prefix_never_exposes_the_whole_nonce()
    {
        var prefix = SessionA.Prefix;
        Assert.AreEqual(9, prefix.Length);
        StringAssert.EndsWith(prefix, "...");
        Assert.AreEqual("<none>", PlmxSessionId.Empty.Prefix);
    }

    [TestMethod]
    public void A_v2_header_round_trips_and_rejects_v1_reserved_and_oversize()
    {
        var header = PlmxFrame.EncodeHeader(PanelChannelId.Video, PlmxFrameType.Data, 4096, 7, SessionA.Tag);
        Assert.AreEqual(PlmxFrame.HeaderSize, header.Length);
        var decoded = PlmxFrame.DecodeHeader(header);
        Assert.AreEqual(PanelChannelId.Video, decoded.Channel);
        Assert.AreEqual(PlmxFrameType.Data, decoded.Type);
        Assert.AreEqual(4096, decoded.PayloadLength);
        Assert.AreEqual(7u, decoded.Sequence);
        Assert.AreEqual(SessionA.Tag, decoded.SessionTag);

        // A v1 header must not be accepted on a v2 carrier: silently
        // downgrading would reintroduce identity-free framing.
        var downgraded = PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 1, 1, SessionA.Tag);
        downgraded[4] = 1;
        Assert.ThrowsException<InvalidDataException>(() => PlmxFrame.DecodeHeader(downgraded));

        var reserved = PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 1, 1, SessionA.Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(reserved.AsSpan(20), 1);
        Assert.ThrowsException<InvalidDataException>(() => PlmxFrame.DecodeHeader(reserved));

        var oversize = PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 1, 1, SessionA.Tag);
        BinaryPrimitives.WriteUInt32LittleEndian(
            oversize.AsSpan(8),
            checked((uint)MultiplexFrame.MaximumControlPayload + 1));
        Assert.IsFalse(PlmxFrame.TryDecodeHeader(oversize, out _), "Hardening Wave 1 ceilings still apply.");
    }

    [TestMethod]
    public void A_hello_only_validates_when_its_header_and_payload_agree()
    {
        var payload = PlmxFrame.EncodeHello(PanelTransportCapabilities.Multiplexing, SessionA);
        Assert.AreEqual(PlmxFrame.HelloPayloadSize, payload.Length);
        Assert.IsTrue(PlmxFrame.TryDecodeHello(payload, out var hello));
        Assert.AreEqual(SessionA, hello.SessionId);

        var header = PlmxFrame.DecodeHeader(PlmxFrame.EncodeHeader(
            PanelChannelId.Transport, PlmxFrameType.Hello, payload.Length, 1, SessionA.Tag));
        Assert.IsTrue(PlmxFrame.IsHandshakeFrame(header, payload, PlmxFrameType.Hello, out _));

        // A header tagged for one session carrying another session's nonce is
        // not a handshake, it is corruption or a splice.
        var mismatched = PlmxFrame.DecodeHeader(PlmxFrame.EncodeHeader(
            PanelChannelId.Transport, PlmxFrameType.Hello, payload.Length, 1, SessionB.Tag));
        Assert.IsFalse(PlmxFrame.IsHandshakeFrame(mismatched, payload, PlmxFrameType.Hello, out _));

        var zeroed = PlmxFrame.EncodeHello(PanelTransportCapabilities.Multiplexing, PlmxSessionId.Empty);
        Assert.IsFalse(PlmxFrame.TryDecodeHello(zeroed, out _), "An all-zero nonce defeats session identity.");
    }

    [TestMethod]
    public async Task Stale_session_bytes_are_skipped_and_the_new_session_establishes()
    {
        // Session A's complete traffic, then a truncated video frame, then
        // session B's greeting: exactly what a reconnect over a pipe that was
        // never drained looks like.
        var stale = new MemoryStream();
        WriteFrame(stale, PanelChannelId.Transport, PlmxFrameType.Hello, HelloPayload(SessionA), 1, SessionA.Tag);
        WriteFrame(stale, PanelChannelId.Control, PlmxFrameType.Open, [], 2, SessionA.Tag);
        WriteFrame(stale, PanelChannelId.Control, PlmxFrameType.Data, "old-telemetry"u8.ToArray(), 3, SessionA.Tag);
        var truncated = PlmxFrame.EncodeHeader(PanelChannelId.Video, PlmxFrameType.Data, 4096, 4, SessionA.Tag);
        stale.Write(truncated.AsSpan(0, 11));

        var established = await EstablishAccessoryAsync(stale.ToArray(), SessionB);

        // The accessory may first adopt session A's own stale greeting. Because
        // the pipe is FIFO, session B's greeting arrives afterwards and
        // supersedes it, so convergence is what matters, not the first match.
        await WaitForSessionAsync(established, SessionB);
        Assert.AreEqual(SessionB, established.SessionId);
        Assert.AreEqual(PlmxSessionState.Established, established.State);
        await established.DisposeAsync();
    }

    [TestMethod]
    public async Task A_magic_hidden_inside_a_stale_payload_does_not_falsely_synchronize()
    {
        // The scanner must validate the whole frame, not just the four magic
        // bytes, or a previous session's payload can capture it.
        var decoy = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(8), PlmxFrame.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(40), PlmxFrame.Magic);
        var stale = new MemoryStream();
        WriteFrame(stale, PanelChannelId.Control, PlmxFrameType.Data, decoy, 5, SessionA.Tag);

        var established = await EstablishAccessoryAsync(stale.ToArray(), SessionB);

        await WaitForSessionAsync(established, SessionB);
        Assert.AreEqual(SessionB, established.SessionId);
        await established.DisposeAsync();
    }

    [TestMethod]
    public async Task Random_garbage_before_the_handshake_is_absorbed_without_crashing()
    {
        var random = new Random(20260812);
        foreach (var length in new[] { 0, 1, 3, 17, 512, 4096, 64 * 1024 })
        {
            var garbage = new byte[length];
            random.NextBytes(garbage);
            var established = await EstablishAccessoryAsync(garbage, SessionB);
            await WaitForSessionAsync(established, SessionB);
            Assert.AreEqual(SessionB, established.SessionId, $"garbage length {length}");
            await established.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task A_truncated_header_or_payload_from_the_previous_session_is_survivable()
    {
        var truncatedHeader = PlmxFrame
            .EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 128, 9, SessionA.Tag)
            .AsSpan(0, 5)
            .ToArray();
        var withHeader = await EstablishAccessoryAsync(truncatedHeader, SessionB);
        await WaitForSessionAsync(withHeader, SessionB);
        Assert.AreEqual(SessionB, withHeader.SessionId);
        await withHeader.DisposeAsync();

        var partialPayload = new MemoryStream();
        partialPayload.Write(PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 4096, 9, SessionA.Tag));
        partialPayload.Write(new byte[1000]);
        var withPayload = await EstablishAccessoryAsync(partialPayload.ToArray(), SessionB);
        await WaitForSessionAsync(withPayload, SessionB);
        Assert.AreEqual(SessionB, withPayload.SessionId);
        await withPayload.DisposeAsync();
    }

    [TestMethod]
    public async Task Resync_gives_up_after_its_bounded_budget_instead_of_spinning()
    {
        var garbage = new byte[64 * 1024];
        new Random(7).NextBytes(garbage);
        var stream = new ReplayDuplexStream(garbage, repeatForever: true);
        await using var session = new MultiplexedTransportSession(
            stream,
            PlmxRole.Accessory,
            resyncLimits: new PlmxResyncLimits(8 * 1024, TimeSpan.FromSeconds(2)));

        var error = await Assert.ThrowsExceptionAsync<PlmxResyncException>(
            () => session.StartAsync(CancellationToken.None).WaitAsync(Timeout));

        Assert.IsTrue(error.DiscardedBytes >= 8 * 1024);
        Assert.AreEqual(1, session.Metrics.HandshakeFailures);
    }

    [TestMethod]
    public async Task A_stale_FIN_does_not_close_a_channel_in_the_current_session()
    {
        var (host, accessory) = await EstablishPairAsync();
        var control = await host.Session.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask().WaitAsync(Timeout);
        var peerControl = await accessory.Session.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask().WaitAsync(Timeout);

        // A FIN from the previous session, injected straight onto the wire.
        var staleFin = PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Fin, 0, 99, unchecked(host.Session.SessionId.Tag + 1));
        await accessory.Physical.InjectAsync(staleFin);

        await control.WriteAsync("still-here"u8.ToArray()).AsTask().WaitAsync(Timeout);
        var received = new byte[10];
        await ReadExactlyAsync(peerControl, received).WaitAsync(Timeout);

        CollectionAssert.AreEqual("still-here"u8.ToArray(), received);
        Assert.AreEqual(PlmxSessionState.Established, accessory.Session.State);
        Assert.IsTrue(accessory.Session.Metrics.StaleFramesDropped >= 1);
        await host.Session.DisposeAsync();
        await accessory.Session.DisposeAsync();
    }

    [TestMethod]
    public async Task A_stale_DATA_frame_is_dropped_rather_than_failing_the_session()
    {
        var (host, accessory) = await EstablishPairAsync();
        _ = await host.Session.OpenChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask().WaitAsync(Timeout);
        var peerControl = await accessory.Session.AcceptChannelAsync(PanelChannelId.Control, CancellationToken.None).AsTask().WaitAsync(Timeout);

        var staleTag = unchecked(host.Session.SessionId.Tag + 1);
        var staleFrame = new MemoryStream();
        staleFrame.Write(PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 5, 42, staleTag));
        staleFrame.Write("stale"u8);
        await accessory.Physical.InjectAsync(staleFrame.ToArray());

        var deadline = DateTime.UtcNow + Timeout;
        while (accessory.Session.Metrics.StaleFramesDropped == 0 && DateTime.UtcNow < deadline) await Task.Delay(5);

        Assert.AreEqual(1, accessory.Session.Metrics.StaleFramesDropped);
        Assert.AreEqual(PlmxSessionState.Established, accessory.Session.State);
        Assert.AreEqual(0, accessory.Session.Metrics.Errors);
        _ = peerControl;
        await host.Session.DisposeAsync();
        await accessory.Session.DisposeAsync();
    }

    [TestMethod]
    public void Control_ordering_is_strict_while_video_tolerates_gaps()
    {
        // Sequence policy is asserted through the shared fixtures rather than a
        // live session so the expectation is unambiguous.
        var control = PlmxFrame.DecodeHeader(
            PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 1, 5, SessionA.Tag));
        var video = PlmxFrame.DecodeHeader(
            PlmxFrame.EncodeHeader(PanelChannelId.Video, PlmxFrameType.Data, 1, 5, SessionA.Tag));
        Assert.AreEqual(PanelChannelId.Control, control.Channel);
        Assert.AreEqual(PanelChannelId.Video, video.Channel);
        Assert.AreEqual(5u, control.Sequence);
        Assert.AreEqual(5u, video.Sequence);
    }

    [TestMethod]
    public async Task Every_reconnect_gets_a_fresh_session_id()
    {
        var observed = new List<PlmxSessionId>();
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var (host, accessory) = await EstablishPairAsync();
            observed.Add(host.Session.SessionId);
            Assert.AreEqual(host.Session.SessionId, accessory.Session.SessionId);
            await host.Session.DisposeAsync();
            await accessory.Session.DisposeAsync();
        }
        CollectionAssert.AllItemsAreUnique(observed);
    }

    [TestMethod]
    public void The_shared_fixtures_decode_exactly_as_the_Kotlin_side_expects()
    {
        var session = LoadFixture("session.json");
        var sessionTag = (uint)session["sessionTag"]!.GetValue<long>();
        Assert.AreEqual(PlmxFrame.HeaderSize, session["headerSize"]!.GetValue<int>());
        Assert.AreEqual(PlmxFrame.Version, session["version"]!.GetValue<int>());

        var hello = FixtureBytes("hello-v2.json");
        Assert.IsTrue(PlmxFrame.TryDecodeHeader(hello, out var helloHeader));
        Assert.AreEqual(PlmxFrameType.Hello, helloHeader.Type);
        Assert.AreEqual(sessionTag, helloHeader.SessionTag);
        Assert.IsTrue(PlmxFrame.IsHandshakeFrame(
            helloHeader, hello.AsSpan(PlmxFrame.HeaderSize), PlmxFrameType.Hello, out var parsed));
        CollectionAssert.AreEqual(
            Convert.FromHexString(session["sessionId"]!.GetValue<string>()),
            SessionIdBytes(parsed.SessionId));

        var ack = FixtureBytes("hello-ack-v2.json");
        Assert.IsTrue(PlmxFrame.TryDecodeHeader(ack, out var ackHeader));
        Assert.AreEqual(PlmxFrameType.HelloAck, ackHeader.Type);

        Assert.AreEqual(PlmxFrameType.Open, PlmxFrame.DecodeHeader(FixtureBytes("open-v2.json")).Type);
        Assert.AreEqual(PlmxFrameType.Fin, PlmxFrame.DecodeHeader(FixtureBytes("fin-v2.json")).Type);
        var data = PlmxFrame.DecodeHeader(FixtureBytes("data-v2.json"));
        Assert.AreEqual(PanelChannelId.Control, data.Channel);
        Assert.AreEqual(11, data.PayloadLength);

        // A frame for another session decodes but must not match this one.
        var staleHeader = PlmxFrame.DecodeHeader(FixtureBytes("stale-session.json"));
        Assert.AreNotEqual(sessionTag, staleHeader.SessionTag);

        foreach (var invalid in new[]
                 {
                     "invalid-session-zero.json", "invalid-version.json", "invalid-reserved.json",
                     "invalid-open-payload.json", "invalid-hello-length.json",
                 })
        {
            var bytes = FixtureBytes(invalid);
            var decoded = PlmxFrame.TryDecodeHeader(bytes, out var header);
            var accepted = decoded && (header.Type is not (PlmxFrameType.Hello or PlmxFrameType.HelloAck) ||
                PlmxFrame.IsHandshakeFrame(header, bytes.AsSpan(PlmxFrame.HeaderSize), header.Type, out _));
            Assert.IsFalse(accepted, invalid);
        }

        var mismatchedTag = FixtureBytes("invalid-hello-tag.json");
        Assert.IsTrue(PlmxFrame.TryDecodeHeader(mismatchedTag, out var mismatchedHeader));
        Assert.IsFalse(
            PlmxFrame.IsHandshakeFrame(
                mismatchedHeader, mismatchedTag.AsSpan(PlmxFrame.HeaderSize), PlmxFrameType.Hello, out _),
            "invalid-hello-tag.json");
    }

    /// <summary>
    /// Waits for the accessory to converge on the expected session. A dirty
    /// stream can make it adopt a predecessor's greeting first; the newest
    /// HELLO then supersedes it.
    /// </summary>
    private static async Task WaitForSessionAsync(MultiplexedTransportSession session, PlmxSessionId expected)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while ((session.SessionId != expected || session.State != PlmxSessionState.Established) &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        Assert.AreEqual(expected, session.SessionId, "The accessory never converged on the current session.");
        Assert.AreEqual(PlmxSessionState.Established, session.State);
    }

    private static byte[] SessionIdBytes(PlmxSessionId id)
    {
        var bytes = new byte[PlmxSessionId.Size];
        id.WriteTo(bytes);
        return bytes;
    }

    private static byte[] HelloPayload(PlmxSessionId id) =>
        PlmxFrame.EncodeHello(PanelTransportCapabilities.Multiplexing, id);

    private static void WriteFrame(
        Stream stream,
        PanelChannelId channel,
        PlmxFrameType type,
        byte[] payload,
        uint sequence,
        uint sessionTag)
    {
        stream.Write(PlmxFrame.EncodeHeader(channel, type, payload.Length, sequence, sessionTag));
        stream.Write(payload);
    }

    /// <summary>
    /// Feeds an accessory session a prefix of stale bytes followed by a genuine
    /// HELLO, and returns once it has established.
    /// </summary>
    private static async Task<MultiplexedTransportSession> EstablishAccessoryAsync(
        byte[] stalePrefix,
        PlmxSessionId sessionId)
    {
        var inbound = new MemoryStream();
        inbound.Write(stalePrefix);
        inbound.Write(PlmxFrame.EncodeHeader(
            PanelChannelId.Transport, PlmxFrameType.Hello, PlmxFrame.HelloPayloadSize, 1, sessionId.Tag));
        inbound.Write(HelloPayload(sessionId));
        var stream = new ReplayDuplexStream(inbound.ToArray(), repeatForever: false);
        var session = new MultiplexedTransportSession(stream, PlmxRole.Accessory);
        await session.StartAsync(CancellationToken.None).WaitAsync(Timeout);
        return session;
    }

    private static async Task<((MultiplexedTransportSession Session, InjectableDuplexStream Physical) Host,
        (MultiplexedTransportSession Session, InjectableDuplexStream Physical) Accessory)> EstablishPairAsync()
    {
        var (left, right) = InjectableDuplexStream.CreatePair();
        var host = new MultiplexedTransportSession(left, PlmxRole.Host, fragmentBytes: 4096);
        var accessory = new MultiplexedTransportSession(right, PlmxRole.Accessory, fragmentBytes: 4096);
        await Task.WhenAll(
            host.StartAsync(CancellationToken.None),
            accessory.StartAsync(CancellationToken.None)).WaitAsync(Timeout);
        return ((host, left), (accessory, right));
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

    private static JsonNode LoadFixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureRoot(), name)))!;

    private static byte[] FixtureBytes(string name) =>
        Convert.FromHexString(LoadFixture(name)["hex"]!.GetValue<string>());

    private static string FixtureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "protocol-fixtures", "plmx");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("protocol-fixtures/plmx was not found.");
    }

    /// <summary>Replays a fixed inbound script; writes are discarded.</summary>
    private sealed class ReplayDuplexStream(byte[] inbound, bool repeatForever) : Stream
    {
        private int _position;

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
            if (_position >= inbound.Length)
            {
                // A live USB pipe with nothing left to deliver blocks; it does
                // not report end of stream. Returning 0 here would fail the
                // session for a reason the real carrier never produces.
                if (!repeatForever) await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
                _position = 0;
            }
            var count = Math.Min(buffer.Length, inbound.Length - _position);
            inbound.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Duplex pipe that also allows raw bytes to be spliced inbound.</summary>
    private sealed class InjectableDuplexStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _incoming;
        private readonly System.Threading.Channels.ChannelWriter<byte[]> _outgoing;
        private byte[]? _current;
        private int _offset;

        private InjectableDuplexStream(
            System.Threading.Channels.Channel<byte[]> incoming,
            System.Threading.Channels.ChannelWriter<byte[]> outgoing)
        {
            _incoming = incoming;
            _outgoing = outgoing;
        }

        public static (InjectableDuplexStream Left, InjectableDuplexStream Right) CreatePair()
        {
            var left = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
            var right = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
            return (
                new InjectableDuplexStream(left, right.Writer),
                new InjectableDuplexStream(right, left.Writer));
        }

        /// <summary>Splices bytes into this stream's inbound direction.</summary>
        public ValueTask InjectAsync(byte[] bytes) => _incoming.Writer.WriteAsync(bytes);

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
                if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_incoming.Reader.TryRead(out _current)) continue;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await _outgoing.WriteAsync(buffer.ToArray(), cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _outgoing.TryComplete();
            base.Dispose(disposing);
        }
    }
}
