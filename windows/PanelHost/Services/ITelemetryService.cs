using PanelHost.Protocol;

namespace PanelHost.Services;

public interface ITelemetryService : IAsyncDisposable
{
    TelemetryMessage CurrentSnapshot { get; }

    Task InitializeAsync(CancellationToken cancellationToken);
}

internal interface IRuntimeServiceHealth
{
    bool IsAvailable { get; }

    string? ErrorCode { get; }
}
