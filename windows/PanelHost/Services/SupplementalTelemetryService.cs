using PanelHost.Protocol;

namespace PanelHost.Services;

/// <summary>Supplements normal telemetry through an already available privileged lease.</summary>
internal sealed class SupplementalTelemetryService(
    ITelemetryService inner,
    Func<bool> brokerConnected,
    Func<CancellationToken, Task<double?>> readCpuTemperature) : ITelemetryService, IRuntimeServiceHealth
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _sampling;
    private TemperatureReading? _temperature;
    private sealed record TemperatureReading(double Value, long Tick);

    internal static bool IsValidTemperature(double? value) => value is > 0 and < 125 && double.IsFinite(value.Value);

    public TelemetryMessage CurrentSnapshot
    {
        get
        {
            var snapshot = inner.CurrentSnapshot;
            if (IsValidTemperature(snapshot.Cpu.TemperatureC)) return snapshot;
            var reading = Volatile.Read(ref _temperature);
            var value = brokerConnected() && reading is not null && Environment.TickCount64 - reading.Tick < 3000
                ? (double?)reading.Value : null;
            return snapshot with { Cpu = snapshot.Cpu with { TemperatureC = value } };
        }
    }

    public bool IsAvailable => inner is not IRuntimeServiceHealth health || health.IsAvailable;
    public string? ErrorCode => (inner as IRuntimeServiceHealth)?.ErrorCode;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await inner.InitializeAsync(cancellationToken);
        _sampling = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(_lifetime.Token))
                    await RefreshAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        });
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        // Keep the last good value while a read is in flight; clear only on failure or when the lease drops.
        // CurrentSnapshot's 3 s freshness check bounds staleness.
        if (!brokerConnected() || IsValidTemperature(inner.CurrentSnapshot.Cpu.TemperatureC))
        {
            Volatile.Write(ref _temperature, null);
            return;
        }
        try
        {
            var value = await readCpuTemperature(cancellationToken);
            Volatile.Write(ref _temperature, IsValidTemperature(value)
                ? new TemperatureReading(value!.Value, Environment.TickCount64) : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { Volatile.Write(ref _temperature, null); /* Optional sensor failure leaves normal telemetry intact. */ }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_sampling is not null) await _sampling;
        _lifetime.Dispose();
        await inner.DisposeAsync();
    }
}
