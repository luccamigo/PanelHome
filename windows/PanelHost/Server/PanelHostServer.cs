using PanelHost.Protocol;
using PanelHost.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanelHost.Server;

internal sealed class PanelHostServer
{
    private readonly IPanelConnectionListener _listener;
    private readonly PanelClientSession _session;
    private readonly ILogger<PanelHostServer> _logger;

    public PanelHostServer(
        PanelClientSession session,
        IPanelConnectionListener? listener = null,
        ILogger<PanelHostServer>? logger = null)
    {
        _session = session;
        _listener = listener ?? new TcpPanelConnectionListener(PanelChannelId.Control, PanelProtocol.Port);
        _logger = logger ?? NullLogger<PanelHostServer>.Instance;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _listener.StartAsync(cancellationToken);
        if (_listener.BoundPort is int port)
        {
            _logger.LogInformation(
                "Control server listening on {Address}:{Port} for transport {Transport}",
                "127.0.0.1",
                port,
                "adb-tcp");
        }
        else
        {
            _logger.LogInformation("Control server waiting for transport {Transport}", "usb");
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IPanelConnection client;
                try
                {
                    client = await _listener.AcceptAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await using (client)
                {
                    try
                    {
                        await _session.HandleAsync(client, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (IOException exception)
                    {
                        _logger.LogWarning(exception, "Control connection closed");
                    }
                }
            }
        }
        finally
        {
            await _listener.DisposeAsync();
            _logger.LogInformation("Control server stopped");
        }
    }
}
