using Microsoft.Extensions.Hosting;
using PanelHost.Services;

namespace PanelHost.Hosting;

internal sealed class LocalIntegrationHostedService(LocalIntegrationService service) : IHostedService
{
    public Task StartAsync(CancellationToken token) { service.Initialize(); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken token) => service.DisposeAsync().AsTask();
}
