using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Transport;

namespace PanelHost.Tests;

[TestClass]
public sealed class WifiCarrierTests
{
    [TestMethod]
    public async Task Cable_writer_failure_reaches_the_inflight_write_and_closes_session()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var failing = new FailingWriter(left);
        await using var host = new MultiplexedTransportSession(failing);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), peer.StartAsync(timeout.Token));
        using var sender = await host.OpenChannelAsync(PanelChannelId.Control, timeout.Token);
        using var receiver = await peer.AcceptChannelAsync(PanelChannelId.Control, timeout.Token);
        failing.FailWrites = true;
        var error = await Assert.ThrowsExceptionAsync<PanelTransportDisconnectedException>(
            () => sender.WriteAsync(new byte[] { 42 }, timeout.Token).AsTask());
        StringAssert.Contains(error.ToString(), "Synthetic cable write failure");
        Assert.AreNotEqual(PlmxSessionState.Established, host.State);
    }

    private sealed class FailingWriter(Stream inner) : Stream
    {
        public bool FailWrites;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            FailWrites ? ValueTask.FromException(new IOException("Synthetic cable write failure")) : inner.WriteAsync(buffer, ct);
        public override int Read(byte[] b, int o, int n) => inner.Read(b,o,n);
        public override void Write(byte[] b, int o, int n) => WriteAsync(b.AsMemory(o,n)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    [TestMethod]
    public void Budget_bounds_bytes_items_both_directions_and_releases_exactly_once()
    {
        var budget = new CarrierBudget(CarrierLimits.WifiV1);
        var leases = new List<IDisposable>();
        for (var i = 0; i < 64; i++) leases.Add(budget.Reserve(false, PanelChannelId.Control, 0)!);
        Assert.ThrowsException<InvalidDataException>(() => budget.Reserve(false, PanelChannelId.Control, 0));
        foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        leases.Clear();
        for (var i = 0; i < 32; i++) leases.Add(budget.Reserve(true, PanelChannelId.Video, CarrierLimits.WifiFrameBytes)!);
        Assert.ThrowsException<InvalidDataException>(() => budget.Reserve(true, PanelChannelId.Video, 1));
        foreach (var lease in leases) lease.Dispose();
        Assert.AreEqual((long)CarrierLimits.WorkspaceBytes, budget.LiveBytes);
        Assert.ThrowsException<InvalidDataException>(() => budget.Reserve(false, PanelChannelId.Video, 65537));
        Assert.ThrowsException<InvalidDataException>(() => budget.Reserve(false, PanelChannelId.Assets, 0));
        var random = new Random(504);
        leases.Clear();
        for (var i = 0; i < 10000; i++)
        {
            if (leases.Count > 0 && random.Next(3) == 0) { var index = random.Next(leases.Count); leases[index].Dispose(); leases.RemoveAt(index); }
            else
            {
                try { leases.Add(budget.Reserve(random.Next(2) == 0,
                    random.Next(2) == 0 ? PanelChannelId.Control : PanelChannelId.Video, random.Next(65537))!); }
                catch (InvalidDataException) { }
            }
            Assert.IsTrue(budget.LiveBytes is >= CarrierLimits.WorkspaceBytes and <= CarrierLimits.SessionBytes);
        }
        foreach (var lease in leases) lease.Dispose();
        Assert.AreEqual((long)CarrierLimits.WorkspaceBytes, budget.LiveBytes);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Wifi_rejects_oversized_header_and_duplicate_open_without_retaining_buffers(bool duplicate)
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var log = new StringWriter();
        await using var host = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1, fragmentBytes: 1024 * 1024, log: log);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, limits: CarrierLimits.WifiV1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), peer.StartAsync(timeout.Token));
        Assert.AreEqual(65536, host.FragmentBytes);
        if (duplicate)
        {
            await right.WriteAsync(PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Open, 0, 1, host.SessionId.Tag), timeout.Token);
            await right.WriteAsync(PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Open, 0, 2, host.SessionId.Tag), timeout.Token);
        }
        else await right.WriteAsync(PlmxFrame.EncodeHeader(PanelChannelId.Video, PlmxFrameType.Data, 65537, 1, host.SessionId.Tag), timeout.Token);
        while (host.State == PlmxSessionState.Established) await Task.Delay(5, timeout.Token);
        StringAssert.Contains(log.ToString(), duplicate ? "Duplicate WiFi OPEN." : "WiFi carrier limit exceeded.");
        while (host.CarrierLiveBytes != CarrierLimits.WorkspaceBytes) await Task.Delay(5, timeout.Token);
        Assert.AreEqual((long)CarrierLimits.WorkspaceBytes, host.CarrierLiveBytes);
    }

    [TestMethod]
    public async Task Wifi_slow_control_consumer_closes_session_and_releases_payloads()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        using var log = new StringWriter();
        await using var host = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1, log: log);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, limits: CarrierLimits.WifiV1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), peer.StartAsync(timeout.Token));
        using var output = await peer.OpenChannelAsync(PanelChannelId.Control, timeout.Token);
        using var unread = await host.AcceptChannelAsync(PanelChannelId.Control, timeout.Token);
        for (var sequence = 2u; sequence <= 66 && host.State == PlmxSessionState.Established; sequence++)
        {
            try { await right.WriteAsync(PlmxFrame.EncodeHeader(PanelChannelId.Control, PlmxFrameType.Data, 1, sequence, host.SessionId.Tag), timeout.Token);
                await right.WriteAsync(new byte[1], timeout.Token); }
            catch (System.Threading.Channels.ChannelClosedException) { break; }
        }
        while (host.State == PlmxSessionState.Established) await Task.Delay(5, timeout.Token);
        StringAssert.Contains(log.ToString(), "WiFi carrier budget exceeded.");
        while (host.CarrierLiveBytes != CarrierLimits.WorkspaceBytes) await Task.Delay(5, timeout.Token);
        Assert.AreEqual((long)CarrierLimits.WorkspaceBytes, host.CarrierLiveBytes);
    }

    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Video_can_reopen_after_fin_and_disposing_old_stream_keeps_new_channel(bool hostOpens, bool wifi)
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        var limits = wifi ? CarrierLimits.WifiV1 : CarrierLimits.Cable;
        await using var host = new MultiplexedTransportSession(left, limits: limits);
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory, limits: limits);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), peer.StartAsync(timeout.Token));
        var sender = hostOpens ? host : peer; var receiver = hostOpens ? peer : host;
        var first = await sender.OpenChannelAsync(PanelChannelId.Video, timeout.Token);
        var oldReceiver = await receiver.AcceptChannelAsync(PanelChannelId.Video, timeout.Token);
        await first.DisposeAsync();
        Assert.AreEqual(0, await oldReceiver.ReadAsync(new byte[1], timeout.Token));
        await using var second = await sender.OpenChannelAsync(PanelChannelId.Video, timeout.Token);
        await using var currentReceiver = await receiver.AcceptChannelAsync(PanelChannelId.Video, timeout.Token);
        await oldReceiver.DisposeAsync();
        await second.WriteAsync(new byte[] { 42 }, timeout.Token);
        var value = new byte[1]; await currentReceiver.ReadExactlyAsync(value, timeout.Token);
        Assert.AreEqual((byte)42, value[0]);
        Assert.AreEqual(PlmxSessionState.Established, host.State);
    }

    [TestMethod]
    public async Task Thirteenth_and_seventeenth_video_open_preserve_control()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        await using var host = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1);
        // Cable peer bypasses the client's local rate limiter, exercising admission at the host.
        await using var peer = new MultiplexedTransportSession(right, PlmxRole.Accessory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), peer.StartAsync(timeout.Token));
        using var control = await peer.OpenChannelAsync(PanelChannelId.Control, timeout.Token);
        using var receivedControl = await host.AcceptChannelAsync(PanelChannelId.Control, timeout.Token);
        for (var i = 1; i <= 17; i++)
        {
            await using var video = await peer.OpenChannelAsync(PanelChannelId.Video, timeout.Token);
            if (i <= 16)
            {
                await using var received = await host.AcceptChannelAsync(PanelChannelId.Video, timeout.Token);
                await received.DisposeAsync();
            }
            Assert.AreEqual(0, await video.ReadAsync(new byte[1], timeout.Token), $"OPEN {i} must receive FIN");
            await control.WriteAsync(new byte[] { (byte)i }, timeout.Token);
            var data = new byte[1]; await receivedControl.ReadExactlyAsync(data, timeout.Token);
            Assert.AreEqual((byte)i, data[0]);
            Assert.AreEqual(PlmxSessionState.Established, host.State);
        }
    }

    // Pairs with Thirteenth_and_seventeenth_video_open_preserve_control: the host tolerates 16 inbound
    // OPENs a minute while a WiFi client stops itself at 12, so a well-behaved client never trips the host.
    [TestMethod]
    public async Task Wifi_client_stops_itself_at_twelve_reopens_below_the_host_sixteen()
    {
        var (left, right) = WifiCoreTests.MemoryDuplex.CreatePair();
        await using var host = new MultiplexedTransportSession(left, limits: CarrierLimits.WifiV1);
        await using var client = new MultiplexedTransportSession(right, PlmxRole.Accessory, limits: CarrierLimits.WifiV1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(host.StartAsync(timeout.Token), client.StartAsync(timeout.Token));
        for (var i = 1; i <= 12; i++)
        {
            var video = await client.OpenChannelAsync(PanelChannelId.Video, timeout.Token);
            var received = await host.AcceptChannelAsync(PanelChannelId.Video, timeout.Token);
            await video.DisposeAsync();
            Assert.AreEqual(0, await received.ReadAsync(new byte[1], timeout.Token));
            await received.DisposeAsync();
        }
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await client.OpenChannelAsync(PanelChannelId.Video, timeout.Token));
        StringAssert.Contains(error.Message, "reopen rate");
        Assert.AreEqual(PlmxSessionState.Established, host.State, "The host session is not harmed by the client's own refusal.");
    }

    [TestMethod]
    public void Reopen_rate_is_bounded_per_channel_by_a_rolling_monotonic_minute()
    {
        var clock = new OpenClock(); var rate = new CarrierOpenRate(clock);
        for (var i = 0; i < 12; i++) rate.Record(PanelChannelId.Video);
        var error = Assert.ThrowsException<InvalidDataException>(() => rate.Record(PanelChannelId.Video));
        StringAssert.Contains(error.Message, "reopen rate");
        rate.Record(PanelChannelId.Control); clock.Seconds = 60;
        rate.Record(PanelChannelId.Video);
    }
    private sealed class OpenClock : TimeProvider
    { public long Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
}
