using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

internal sealed class FakeTelemetryService : ITelemetryService
{
    public FakeTelemetryService(TelemetryMessage? snapshot = null)
    {
        CurrentSnapshot = snapshot ?? new TelemetryMessage();
    }

    public TelemetryMessage CurrentSnapshot { get; private set; }

    public int InitializeCallCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCallCount++;
        return Task.CompletedTask;
    }

    public void SetSnapshot(TelemetryMessage snapshot)
    {
        CurrentSnapshot = snapshot;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
