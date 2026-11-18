using System.Net;
using System.Net.Sockets;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Transport;

namespace PanelHost.Tests;

[TestClass]
public sealed class AdbTcpAuthenticationTests
{
    private static readonly byte[] Token = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    [DataTestMethod]
    [DataRow((byte)PanelChannelId.Control)]
    [DataRow((byte)PanelChannelId.Video)]
    [DataRow((byte)PanelChannelId.Assets)]
    public async Task Valid_token_authenticates_each_exposed_tcp_channel(byte channelValue)
    {
        var channel = (PanelChannelId)channelValue;
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(channel, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);

        await using var accepted = await ConnectAuthenticatedAsync(listener, channel, Token);

        Assert.AreEqual(channel, accepted.Channel);
        Assert.AreEqual(0L, metrics.AuthFailures);
    }

    [TestMethod]
    public async Task Different_token_is_closed_and_counted_before_control_is_exposed()
    {
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(PanelChannelId.Control, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();

        await AssertRejectedAsync(listener.BoundPort!.Value, PanelChannelId.Control, new byte[32]);
        await using var accepted = await CompleteAuthenticatedAsync(
            accept, listener.BoundPort.Value, PanelChannelId.Control, Token);

        Assert.AreEqual(1L, metrics.AuthFailures);
    }

    [TestMethod]
    public async Task Missing_auth_message_is_closed_and_counted()
    {
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(PanelChannelId.Control, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();

        using (var missing = new TcpClient())
        {
            await missing.ConnectAsync(IPAddress.Loopback, listener.BoundPort!.Value);
        }
        await using var accepted = await CompleteAuthenticatedAsync(
            accept, listener.BoundPort.Value, PanelChannelId.Control, Token);

        Assert.AreEqual(1L, metrics.AuthFailures);
    }

    [TestMethod]
    public async Task Oversized_auth_message_is_rejected_from_its_header()
    {
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(PanelChannelId.Video, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        using (var oversized = new TcpClient())
        {
            await oversized.ConnectAsync(IPAddress.Loopback, listener.BoundPort!.Value);
            var header = AdbTcpAuthentication.EncodeRequest(PanelChannelId.Video, Token)[..8];
            header[6] = 0;
            header[7] = AdbTcpAuthentication.MaximumCredentialBytes + 1;
            await oversized.GetStream().WriteAsync(header);
            var one = new byte[1];
            await AssertClosedAsync(oversized.GetStream(), one, TimeSpan.FromSeconds(2));
        }
        await using var accepted = await CompleteAuthenticatedAsync(
            accept, listener.BoundPort.Value, PanelChannelId.Video, Token);

        Assert.AreEqual(1L, metrics.AuthFailures);
    }

    [TestMethod]
    [Timeout(10_000)]
    public async Task Silent_peer_times_out_without_reserving_the_listener()
    {
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(PanelChannelId.Control, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        using (var silent = new TcpClient())
        {
            await silent.ConnectAsync(IPAddress.Loopback, listener.BoundPort!.Value);
            var one = new byte[1];
            Assert.AreEqual(0, await silent.GetStream().ReadAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await using var accepted = await CompleteAuthenticatedAsync(
            accept, listener.BoundPort.Value, PanelChannelId.Control, Token);

        Assert.AreEqual(1L, metrics.AuthFailures);
    }

    [TestMethod]
    public async Task Credential_for_another_channel_is_rejected()
    {
        var metrics = new PanelTransportMetrics();
        await using var listener = new TcpPanelConnectionListener(PanelChannelId.Control, 0, metrics, Token);
        await listener.StartAsync(CancellationToken.None);
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();

        await AssertRejectedAsync(listener.BoundPort!.Value, PanelChannelId.Video, Token);
        await using var accepted = await CompleteAuthenticatedAsync(
            accept, listener.BoundPort.Value, PanelChannelId.Control, Token);

        Assert.AreEqual(1L, metrics.AuthFailures);
    }

    private static async Task<IPanelConnection> ConnectAuthenticatedAsync(
        IPanelConnectionListener listener,
        PanelChannelId channel,
        byte[] token)
    {
        var accept = listener.AcceptAsync(CancellationToken.None).AsTask();
        return await CompleteAuthenticatedAsync(accept, listener.BoundPort!.Value, channel, token);
    }

    private static async Task<IPanelConnection> CompleteAuthenticatedAsync(
        Task<IPanelConnection> accept,
        int port,
        PanelChannelId channel,
        byte[] token)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(AdbTcpAuthentication.EncodeRequest(channel, token));
        var ack = new byte[AdbTcpAuthentication.AckBytes];
        await ReadExactlyAsync(stream, ack);
        Assert.IsTrue(AdbTcpAuthentication.IsValidAck(ack, channel));
        var accepted = await accept.WaitAsync(TimeSpan.FromSeconds(2));
        client.Dispose();
        return accepted;
    }

    private static async Task AssertRejectedAsync(int port, PanelChannelId channel, byte[] token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(AdbTcpAuthentication.EncodeRequest(channel, token));
        var one = new byte[1];
        await AssertClosedAsync(client.GetStream(), one, TimeSpan.FromSeconds(2));
    }

    private static async Task AssertClosedAsync(Stream stream, byte[] buffer, TimeSpan timeout)
    {
        try
        {
            Assert.AreEqual(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(timeout));
        }
        catch (IOException)
        {
            // TCP may expose an immediate close as EOF or a connection reset.
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> target)
    {
        var offset = 0;
        while (offset < target.Length)
        {
            var read = await stream.ReadAsync(target[offset..]);
            if (read == 0) Assert.Fail("Authentication acknowledgement ended early.");
            offset += read;
        }
    }
}
