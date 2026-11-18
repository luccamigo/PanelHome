using System.IO.Pipes;
using System.Text;
using PanelOS.Windows.Protocol;
using PanelOS.Windows.Wifi;

namespace PanelHost.Security.Wifi;

// One request line, one response line, per connection; user-only pipe; at most four handlers at once and a 3 s
// deadline per connection, so a local process cannot keep the operator's console busy. Nothing from a request is
// echoed into the response except stable codes.
internal sealed class WifiAdminPipeServer(WifiAdminBackend backend, string pipeName = WifiAdminProtocol.PipeName)
{
    private readonly SemaphoreSlim _handlers = new(4, 4);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var running = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly, 4096, 64 * 1024);
                try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
                if (!await _handlers.WaitAsync(0, CancellationToken.None).ConfigureAwait(false)) { await pipe.DisposeAsync().ConfigureAwait(false); continue; }
                running.RemoveAll(task => task.IsCompleted);
                running.Add(HandleAsync(pipe, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { await Task.WhenAll(running).ConfigureAwait(false); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await using var _ = pipe;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            WifiAdminResponse response;
            try
            {
                var line = await new BoundedUtf8LineReader(pipe, WifiAdminProtocol.MaxRequestBytes).ReadLineAsync(deadline.Token).ConfigureAwait(false);
                var request = line is null ? null : WifiAdminProtocol.DeserializeRequest(line);
                response = request is null ? new WifiAdminResponse(false, WifiAdminProtocol.ErrorInvalid, null) : backend.Handle(request);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or JsonLineProtocolException)
            { response = new WifiAdminResponse(false, WifiAdminProtocol.ErrorInvalid, null); }
            await writer.WriteLineAsync(WifiAdminProtocol.Serialize(response).AsMemory(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { _handlers.Release(); }
    }
}