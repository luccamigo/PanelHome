using System.IO.Pipes;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Broker;

namespace PanelHost.Tests;

/// <summary>
/// The broker handshake, at the point where PanelHost has a connected pipe and has not
/// read a byte from it. Everything before this test existed was covered as serialisation;
/// the pipe itself was not, which is how an identity check that Windows refuses at this
/// exact moment reached the bench.
/// </summary>
[TestClass]
public sealed class PanelDisplayBrokerHandshakeTests
{
    [TestMethod]
    public async Task Pan4_disconnected_broker_stop_does_not_invoke_executable_locator()
    {
        var launches = 0;
        await using var client = new PanelDisplayBrokerClient(TextWriter.Null, () =>
        {
            launches++;
            throw new InvalidOperationException("No UAC or process launch is allowed");
        });

        await Assert.ThrowsExceptionAsync<PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException>(
            () => client.DisconnectAsync(CancellationToken.None));
        await Assert.ThrowsExceptionAsync<PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException>(
            () => client.GetStatusIfConnectedAsync(CancellationToken.None));
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task Pan4_status_rechecks_connection_after_waiting_for_request_lock()
    {
        var launches = 0;
        await using var client = new PanelDisplayBrokerClient(TextWriter.Null, () =>
        {
            launches++;
            throw new InvalidOperationException("No UAC or process launch is allowed");
        });
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var type = typeof(PanelDisplayBrokerClient);
        var requestLock = (SemaphoreSlim)type.GetField("_requestLock", flags)!.GetValue(client)!;
        type.GetField("_connected", flags)!.SetValue(client, 1);
        await requestLock.WaitAsync();
        var pending = client.GetStatusIfConnectedAsync(CancellationToken.None);
        Assert.IsFalse(pending.IsCompleted);
        type.GetField("_connected", flags)!.SetValue(client, 0);
        requestLock.Release();

        await Assert.ThrowsExceptionAsync<PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException>(
            () => pending);
        Assert.AreEqual(0, launches);
    }

    [TestMethod]
    public async Task Pan4_broken_control_writer_does_not_abort_broker_connection_cleanup()
    {
        await using var pair = await BrokerPipe.ConnectAsync();
        using var log = new StringWriter();
        await using var client = new PanelDisplayBrokerClient(log,
            () => throw new InvalidOperationException("No broker process may start in this test"));
        var writer = new StreamWriter(pair.Server, new UTF8Encoding(false), 4096, leaveOpen: true);
        await writer.WriteAsync("pending synthetic request");
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(PanelDisplayBrokerClient).GetField("_pipe", flags)!.SetValue(client, pair.Server);
        typeof(PanelDisplayBrokerClient).GetField("_writer", flags)!.SetValue(client, writer);
        await pair.Client.DisposeAsync();
        Assert.AreEqual(0, await pair.Server.ReadAsync(new byte[1]).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5)));
        var handle = pair.Server.SafePipeHandle;

        await client.DisposeAsync();

        Assert.IsTrue(handle.IsClosed);
    }
    [TestMethod]
    public async Task Declined_elevation_prompt_is_reported_as_elevation_declined()
    {
        await using var client = new PanelDisplayBrokerClient(TextWriter.Null,
            () => throw new System.ComponentModel.Win32Exception(1223));

        var failure = await Assert.ThrowsExceptionAsync<PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException>(
            () => client.GetStatusAsync(CancellationToken.None));

        Assert.AreEqual(
            PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.ElevationDeclined,
            failure.Reason);
        Assert.IsFalse(client.IsConnected);
    }

    /// <summary>
    /// Bench 01:29: "IOException: Pipe is broken" escaped the start of the broker as an unexpected
    /// failure, so the operator saw driver_io_error. It is a broker that dropped the lease.
    /// The IOException is thrown by the executable locator, which runs inside the same try as the
    /// handshake: this proves the catch clause and the log, not a real handshake (that needs an
    /// elevated broker process). Any IOException while the pipe, mailbox or process is created is
    /// reported the same way.
    /// </summary>
    [TestMethod]
    public async Task Pipe_broken_while_starting_the_broker_is_a_broker_disconnect()
    {
        using var log = new StringWriter();
        await using var client = new PanelDisplayBrokerClient(log,
            () => throw new IOException("Pipe is broken."));

        var failure = await Assert.ThrowsExceptionAsync<PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException>(
            () => client.GetStatusAsync(CancellationToken.None));

        Assert.AreEqual(
            PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.PipeClosed,
            failure.Reason);
        Assert.IsInstanceOfType(failure.InnerException, typeof(IOException));
        StringAssert.Contains(log.ToString(), "closed the connection while starting");
        Assert.IsFalse(client.IsConnected);
    }

    [TestMethod]
    public void Startup_failures_keep_the_broker_exit_code_and_reason()
    {
        var io = new IOException("Pipe is broken.");

        var withExit = PanelDisplayBrokerClient.TranslateStartupFailure(io, 3);
        var withoutExit = PanelDisplayBrokerClient.TranslateStartupFailure(io, null);
        var protocol = PanelDisplayBrokerClient.TranslateStartupFailure(
            new InvalidDataException("bad hello"), null);

        Assert.AreEqual(
            PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.BrokerExited,
            withExit.Reason);
        Assert.AreEqual(
            PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.PipeClosed,
            withoutExit.Reason);
        Assert.AreEqual(
            PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.ProtocolError,
            protocol.Reason);
    }

    [TestMethod]
    public void A_disconnect_that_was_already_classified_is_not_translated_again()
    {
        Assert.IsTrue(PanelDisplayBrokerClient.IsStartupConnectionFailure(
            new IOException("Pipe is broken.")));
        Assert.IsTrue(PanelDisplayBrokerClient.IsStartupConnectionFailure(
            new ObjectDisposedException("pipe")));
        Assert.IsFalse(PanelDisplayBrokerClient.IsStartupConnectionFailure(
            new PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectedException(
                PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerDisconnectReason.AuthenticationFailed,
                "nonce")));
        Assert.IsFalse(PanelDisplayBrokerClient.IsStartupConnectionFailure(
            new UnauthorizedAccessException()));
    }

    [TestMethod]
    public async Task Connected_process_is_accepted_before_anything_is_read()
    {
        await using var connection = await BrokerPipe.ConnectAsync();

        // The regression: this ran as GetImpersonationUserName(), and Windows answers
        // ERROR_CANNOT_IMPERSONATE until the server has read from the pipe - so every
        // desktop.start failed as driver_io_error before the broker was ever asked
        // anything.
        PanelDisplayBrokerClient.ValidateConnectedProcess(
            connection.Server,
            Environment.ProcessId);
    }

    [TestMethod]
    public async Task A_peer_that_is_not_the_started_broker_is_refused()
    {
        await using var connection = await BrokerPipe.ConnectAsync();

        Assert.ThrowsException<UnauthorizedAccessException>(() =>
            PanelDisplayBrokerClient.ValidateConnectedProcess(
                connection.Server,
                Environment.ProcessId + 1));
    }

    /// <summary>
    /// The same pipe the client creates: byte mode, current user only, and a client that
    /// connects without writing first.
    /// </summary>
    private sealed class BrokerPipe : IAsyncDisposable
    {
        private BrokerPipe(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            Server = server;
            Client = client;
        }

        internal NamedPipeServerStream Server { get; }

        internal NamedPipeClientStream Client { get; }

        internal static async Task<BrokerPipe> ConnectAsync()
        {
            var pipeName = $"PanelOS.DisplayBroker.Test.{Guid.NewGuid():N}";
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
            return new BrokerPipe(server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
