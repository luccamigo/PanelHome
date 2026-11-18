using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Broker;
using PanelOS.DisplayBroker.Protocol;

namespace PanelHost.Tests;

/// <summary>
/// PAN-3: a broker that is alive but never answers (a driver call that does not return).
/// Before the fix the host waited forever on the control pipe while holding the request
/// lock, so the Desktop could neither stop nor recover.
/// </summary>
[TestClass]
public sealed class PanelDisplayBrokerUnresponsiveTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    [TestMethod]
    public async Task Pan3_silent_broker_is_declared_unresponsive_and_its_lease_is_closed()
    {
        await using var pair = await BrokerPipePair.ConnectAsync();
        using var log = new StringWriter();
        await using var client = new PanelDisplayBrokerClient(
            log,
            () => throw new InvalidOperationException("No broker process may start in this test"),
            requestTimeout: TimeSpan.FromMilliseconds(300));
        Attach(client, pair.Server);
        var serverHandle = pair.Server.SafePipeHandle;
        var events = new List<PanelDisplayBrokerDisconnectedEventArgs>();
        client.Disconnected += (_, args) => events.Add(args);

        // The fake broker reads the request and then goes silent, as a hung driver call would.
        var brokerReader = new BrokerControlMessageReader(pair.Client);
        var request = brokerReader.ReadAsync(CancellationToken.None);

        var failure = await Assert.ThrowsExceptionAsync<PanelDisplayBrokerDisconnectedException>(
            () => client.GetStatusIfConnectedAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsNotNull(await request);
        Assert.AreEqual(PanelDisplayBrokerDisconnectReason.Unresponsive, failure.Reason);
        Assert.IsFalse(client.IsConnected);
        Assert.AreEqual(1, events.Count);
        Assert.AreEqual(PanelDisplayBrokerDisconnectReason.Unresponsive, events[0].Reason);
        // Closing the pipe is the lease signal: the broker sees end-of-stream and shuts down.
        Assert.AreEqual(
            0,
            await pair.Client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(serverHandle.IsClosed);
    }

    [TestMethod]
    public async Task Pan3_after_an_unresponsive_broker_the_next_request_does_not_wait_on_the_dead_lease()
    {
        await using var pair = await BrokerPipePair.ConnectAsync();
        await using var client = new PanelDisplayBrokerClient(
            TextWriter.Null,
            () => throw new InvalidOperationException("No UAC or process launch is allowed"),
            requestTimeout: TimeSpan.FromMilliseconds(300));
        Attach(client, pair.Server);
        _ = new BrokerControlMessageReader(pair.Client).ReadAsync(CancellationToken.None);

        await Assert.ThrowsExceptionAsync<PanelDisplayBrokerDisconnectedException>(
            () => client.GetStatusIfConnectedAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        // Stop must now answer immediately (no lock held, no relaunch) so the Desktop can be left.
        await Assert.ThrowsExceptionAsync<PanelDisplayBrokerDisconnectedException>(
            () => client.DisconnectAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task Pan3_caller_cancellation_is_not_reported_as_an_unresponsive_broker()
    {
        await using var pair = await BrokerPipePair.ConnectAsync();
        await using var client = new PanelDisplayBrokerClient(
            TextWriter.Null,
            () => throw new InvalidOperationException("No broker process may start in this test"),
            requestTimeout: TimeSpan.FromSeconds(30));
        Attach(client, pair.Server);
        var events = new List<PanelDisplayBrokerDisconnectedEventArgs>();
        client.Disconnected += (_, args) => events.Add(args);
        _ = new BrokerControlMessageReader(pair.Client).ReadAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => client.GetStatusIfConnectedAsync(cancellation.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.AreNotEqual(
            PanelDisplayBrokerDisconnectReason.Unresponsive,
            events.Single().Reason);
    }

    [TestMethod]
    public async Task Pan3_a_broker_that_answers_in_time_is_unaffected()
    {
        await using var pair = await BrokerPipePair.ConnectAsync();
        await using var client = new PanelDisplayBrokerClient(
            TextWriter.Null,
            () => throw new InvalidOperationException("No broker process may start in this test"),
            requestTimeout: TimeSpan.FromSeconds(5));
        Attach(client, pair.Server);
        var brokerReader = new BrokerControlMessageReader(pair.Client);
        var brokerWriter = new StreamWriter(pair.Client, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        var serve = Task.Run(async () =>
        {
            var line = await brokerReader.ReadAsync(CancellationToken.None);
            var request = PanelDisplayBrokerProtocol.Deserialize<PanelDisplayBrokerRequest>(line!)!;
            await brokerWriter.WriteLineAsync(PanelDisplayBrokerProtocol.Serialize(
                new PanelDisplayBrokerResponse(
                    request.RequestId,
                    PanelDisplayBrokerProtocol.Version,
                    false,
                    Error: new PanelDisplayBrokerError("notfound", "synthetic", null))));
        });

        var failure = await Assert.ThrowsExceptionAsync<PanelOS.DriverInterop.PanelDisplayDriverException>(
            () => client.GetStatusIfConnectedAsync(CancellationToken.None));

        await serve;
        Assert.AreEqual(PanelOS.DriverInterop.PanelDisplayDriverFailureKind.NotFound, failure.Kind);
        Assert.IsTrue(client.IsConnected);
    }

    private static void Attach(PanelDisplayBrokerClient client, NamedPipeServerStream server)
    {
        var type = typeof(PanelDisplayBrokerClient);
        var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        type.GetField("_pipe", Flags)!.SetValue(client, server);
        type.GetField("_reader", Flags)!.SetValue(client, new BrokerControlMessageReader(server));
        type.GetField("_writer", Flags)!.SetValue(client, writer);
        type.GetField("_connected", Flags)!.SetValue(client, 1);
    }

    private sealed class BrokerPipePair : IAsyncDisposable
    {
        private BrokerPipePair(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        internal NamedPipeServerStream Server { get; }

        internal NamedPipeClientStream Client { get; }

        internal static async Task<BrokerPipePair> ConnectAsync()
        {
            var pipeName = $"PanelOS.DisplayBroker.Pan3.{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough |
                    PipeOptions.CurrentUserOnly,
                4096,
                64 * 1024);
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var waiting = server.WaitForConnectionAsync();
            await client.ConnectAsync(5000);
            await waiting;
            return new BrokerPipePair(server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
