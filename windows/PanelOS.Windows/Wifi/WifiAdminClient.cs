using System.IO.Pipes;
using System.Text;
using PanelOS.Windows.Protocol;

namespace PanelOS.Windows.Wifi;

public interface IWifiAdminClient
{
    /// <summary>Never throws for an absent peer: that is the normal "WiFi is off" state (Error = unavailable).</summary>
    Task<WifiAdminResponse> SendAsync(WifiAdminRequest request, CancellationToken cancellationToken);
}

public sealed class WifiAdminClient(string pipeName = WifiAdminProtocol.PipeName, TimeSpan? timeout = null) : IWifiAdminClient
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);

    public async Task<WifiAdminResponse> SendAsync(WifiAdminRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(WifiAdminProtocol.Serialize(request).AsMemory(), deadline.Token).ConfigureAwait(false);
            var line = await new BoundedUtf8LineReader(pipe).ReadLineAsync(deadline.Token).ConfigureAwait(false);
            return (line is null ? null : WifiAdminProtocol.DeserializeResponse(line))
                ?? new WifiAdminResponse(false, WifiAdminProtocol.ErrorUnavailable, null);
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException
            or System.Text.Json.JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new WifiAdminResponse(false, WifiAdminProtocol.ErrorUnavailable, null);
        }
    }
}