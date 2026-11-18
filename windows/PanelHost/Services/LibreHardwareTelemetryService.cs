using System.Globalization;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelOS.Telemetry;
using PanelHost.Protocol;

namespace PanelHost.Services;

public sealed class LibreHardwareTelemetryService : ITelemetryService, IRuntimeServiceHealth
{
    private static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CpuDiagnosticRoundDelay =
        TimeSpan.FromMilliseconds(250);
    private const int RediscoveryIntervalSamples = 30;
    private const int CpuDiagnosticRounds = 3;

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = true,
        IsMotherboardEnabled = true,
    };
    private readonly UpdateVisitor _updateVisitor;
    private readonly ILogger<LibreHardwareTelemetryService> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _computerLock = new();
    private readonly object _snapshotLock = new();
    private readonly bool _startSampling;
    private readonly WindowsCpuClock _windowsCpuClock = new();
    private string? _lastCpuClockSource;

    private IReadOnlyList<HardwareBinding> _bindings = Array.Empty<HardwareBinding>();
    private IReadOnlyList<TelemetryHardwareSample> _lastSamples =
        Array.Empty<TelemetryHardwareSample>();
    private TelemetryEnvironment _environment = new();
    private TelemetryMessage _currentSnapshot = new();
    private Task? _samplingTask;
    private int _rediscoveryRequested;
    private bool _computerOpened;
    private bool _initialized;
    private bool _samplingWarningWritten;
    private bool _disposed;
    private string? _initializationErrorCode;

    public LibreHardwareTelemetryService(
        bool startSampling = true,
        ILogger<LibreHardwareTelemetryService>? logger = null)
    {
        _startSampling = startSampling;
        _logger = logger ?? NullLogger<LibreHardwareTelemetryService>.Instance;
        _updateVisitor = new UpdateVisitor(_logger);
    }

    public TelemetryMessage CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _currentSnapshot;
            }
        }
    }

    bool IRuntimeServiceHealth.IsAvailable => _initializationErrorCode is null;

    string? IRuntimeServiceHealth.ErrorCode => _initializationErrorCode;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        try
        {
            await Task.Run(InitializeComputer, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _initializationErrorCode = exception.GetType().Name;
            // The exception type is part of the diagnosis: an access violation
            // from the kernel driver and a missing WMI class fail identically
            // when only the message survives.
            _logger.LogError(
                exception,
                "Hardware telemetry failed with errorCode {ErrorCode}; unavailable sensor values will be published",
                exception.GetType().Name);
            CloseComputer();
            _lastSamples = Array.Empty<TelemetryHardwareSample>();
            PublishSnapshot(new TelemetryMessage());
        }

        WriteCpuAvailabilityWarnings(_lastSamples, _environment);
        _initialized = true;
        if (_startSampling && _computerOpened)
        {
            _samplingTask = Task.Run(
                () => RunSamplingLoopAsync(_lifetimeCancellation.Token),
                CancellationToken.None);
        }
    }

    public Task DumpSensorsAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Telemetry service must be initialized before dumping sensors.");
        }

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_computerLock)
                {
                    if (!_computerOpened)
                    {
                        writer.WriteLine(
                            "[Telemetry] No hardware sensors are currently available.");
                        return;
                    }

                    UpdateHardware();
                    var samples = ReadSamples();
                    WriteEnvironment(writer, _environment);
                    WriteGroupedSensors(writer, samples, cancellationToken);

                    writer.WriteLine();
                    writer.WriteLine("PanelHost selections");
                    writer.WriteLine(
                        "Metric | Hardware Type | Hardware | Hardware Identifier | " +
                        "Sensor Type | Sensor | Value | Sensor Identifier");
                    foreach (var selection in TelemetrySnapshotMapper.SelectMetrics(samples))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteSelection(writer, selection);
                    }
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Raw CPU diagnostic snapshot for <c>--telemetry-debug-cpu</c>: the exact
    /// library build in use, the real state of the kernel driver, and every CPU
    /// sensor across three consecutive update cycles. Three cycles exist to tell
    /// a sensor that needs a warm-up apart from one that never reports; nothing
    /// in the sampling path waits, and nothing here changes it.
    /// </summary>
    public async Task DumpCpuDiagnosticsAsync(
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Telemetry service must be initialized before dumping CPU " +
                "diagnostics.");
        }

        WriteEnvironment(writer, _environment);
        if (!_computerOpened)
        {
            writer.WriteLine();
            writer.WriteLine(
                "[Telemetry] No hardware sensors are currently available.");
            return;
        }

        for (var round = 1; round <= CpuDiagnosticRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var round1 = round;
            await Task.Run(
                () =>
                {
                    lock (_computerLock)
                    {
                        UpdateHardware();
                        WriteCpuSensorRound(writer, round1, cancellationToken);
                    }
                },
                cancellationToken);

            if (round < CpuDiagnosticRounds)
            {
                await Task.Delay(CpuDiagnosticRoundDelay, cancellationToken);
            }
        }

        await Task.Run(
            () =>
            {
                lock (_computerLock)
                {
                    DiscoverBindings();
                    var samples = ReadSamples();
                    PublishSnapshot(CreateSnapshot(samples));
                    var cpu = TelemetrySnapshotMapper.SelectPrimaryCpu(samples);
                    writer.WriteLine();
                    writer.WriteLine("PanelHost CPU diagnosis");
                    foreach (var diagnosis in new[]
                    {
                        TelemetrySnapshotMapper.DiagnoseCpuTemperature(
                            cpu,
                            _environment),
                        TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, _environment),
                        TelemetrySnapshotMapper.DiagnoseCpuVoltage(cpu, _environment),
                    })
                    {
                        writer.WriteLine(
                            $"  {diagnosis.Metric}: {diagnosis.Health} - " +
                            diagnosis.Detail);
                    }
                    writer.WriteLine($"  Published CPU clock: {CurrentSnapshot.Cpu.ClockGhz?.ToString("0.###", CultureInfo.InvariantCulture) ?? "unavailable"} GHz; source={_lastCpuClockSource}");
                    if (_environment.WindowsCpuClockMhz is null && _lastCpuClockSource == "unavailable")
                        writer.WriteLine($"  Windows fallback: {_windowsCpuClock.UnavailableReason}");
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// CPU sensors after this cycle's <see cref="IHardware.Update"/>. Voltage
    /// is explicitly rejected when kernel access is not proven; the other raw
    /// sensors remain visible separately from the published CPU diagnosis.
    /// </summary>
    private void WriteCpuSensorRound(
        TextWriter writer,
        int round,
        CancellationToken cancellationToken)
    {
        writer.WriteLine();
        writer.WriteLine($"=== CPU sensors after hardware.Update() #{round} ===");
        var cpuHardware = EnumerateHardware(_computer.Hardware)
            .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
            .ToArray();
        if (cpuHardware.Length == 0)
        {
            writer.WriteLine("  (no CPU hardware enumerated)");
            return;
        }

        foreach (var hardware in cpuHardware)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteLine(
                $"  {hardware.Name} | {DisplayIdentifier(hardware.Identifier)} | " +
                $"update={(_updateVisitor.WasSuccessful(hardware) ? "ok" : "failed")}");
            if (hardware.Sensors.Length == 0)
            {
                writer.WriteLine("    (no sensors)");
                continue;
            }

            foreach (var sensor in hardware.Sensors
                .OrderBy(sensor => sensor.SensorType)
                .ThenBy(sensor => sensor.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (sensor.SensorType == SensorType.Voltage && _environment.KernelDriver?.IsUsable != true)
                {
                    writer.WriteLine($"    Voltage      | {sensor.Name,-28} | unavailable (raw reading rejected: PawnIO access not proven)");
                    continue;
                }
                writer.WriteLine(
                    $"    {sensor.SensorType,-12} | {sensor.Name,-28} | " +
                    $"{DisplayIdentifier(sensor.Identifier),-30} | " +
                    $"value={FormatValue(sensor.Value),-12} " +
                    $"min={FormatValue(sensor.Min),-12} " +
                    $"max={FormatValue(sensor.Max),-12}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifetimeCancellation.CancelAsync();
        if (_samplingTask is not null)
        {
            try
            {
                await _samplingTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        CloseComputer();
        _windowsCpuClock.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void InitializeComputer()
    {
        lock (_computerLock)
        {
            _environment = ReadEnvironment();
            _computer.Open();
            _computerOpened = true;
            _computer.HardwareAdded += OnHardwareChanged;
            _computer.HardwareRemoved += OnHardwareChanged;
            UpdateHardware();
            DiscoverBindings();
            var samples = ReadSamples();
            _lastSamples = samples;
            PublishSnapshot(CreateSnapshot(samples));

            foreach (var description in
                TelemetrySnapshotMapper.DescribeSelections(samples, _environment))
            {
                _logger.LogInformation("{TelemetrySelection}", description);
            }
        }
    }

    private async Task RunSamplingLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SamplingInterval);
        var samplesSinceDiscovery = 0;

        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                lock (_computerLock)
                {
                    if (!_computerOpened)
                    {
                        PublishSnapshot(new TelemetryMessage());
                        continue;
                    }

                    UpdateHardware();
                    samplesSinceDiscovery++;
                    if (Interlocked.Exchange(ref _rediscoveryRequested, 0) != 0 ||
                        samplesSinceDiscovery >= RediscoveryIntervalSamples)
                    {
                        DiscoverBindings();
                        samplesSinceDiscovery = 0;
                    }

                    var snapshot = CreateSnapshot(ReadSamples());
                    PublishSnapshot(snapshot);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                PublishSnapshot(new TelemetryMessage());
                if (!_samplingWarningWritten)
                {
                    _samplingWarningWritten = true;
                    _logger.LogWarning(
                        exception,
                        "Telemetry sampling failed with errorCode {ErrorCode}; sampling will retry",
                        exception.GetType().Name);
                }
            }
        }
    }

    private void DiscoverBindings()
    {
        _bindings = EnumerateHardware(_computer.Hardware)
            .Select(hardware => new HardwareBinding(
                hardware,
                MapHardwareKind(hardware.HardwareType)))
            .Where(binding => binding.Kind != TelemetryHardwareKind.Other)
            .ToArray();
    }

    // One pass over the already-updated hardware. Every sensor of a device is
    // captured from the same update cycle, so paired readings such as VRAM used
    // and VRAM total can never be taken from two different frames.
    private IReadOnlyList<TelemetryHardwareSample> ReadSamples() =>
        _bindings
            .Where(binding => _updateVisitor.WasSuccessful(binding.Hardware))
            .Select(binding => new TelemetryHardwareSample(
                binding.Kind,
                binding.Hardware.Name,
                binding.Hardware.Sensors
                    .Select(CreateSensorSample)
                    .ToArray(),
                ReadIdentifier(binding.Hardware.Identifier)))
            .ToArray();

    private static TelemetrySensorSample CreateSensorSample(ISensor sensor) =>
        new(
            sensor.Name,
            MapSensorKind(sensor.SensorType),
            sensor.Index,
            sensor.Value,
            ReadIdentifier(sensor.Identifier),
            sensor.Min,
            sensor.Max);

    private static TelemetryEnvironment ReadEnvironment()
    {
        var kernelDriver = TelemetryDiagnostics.ProbeKernelDriver();
        return new TelemetryEnvironment(
            TelemetryDiagnostics.LibreHardwareMonitorVersion(),
            TelemetryDiagnostics.IsProcessElevated(),
            kernelDriver.IsPresent,
            kernelDriver,
            TelemetryDiagnostics.LibreHardwareMonitorInformationalVersion(),
            TelemetryDiagnostics.LibreHardwareMonitorLocation(),
            TelemetryDiagnostics.LibreHardwareMonitorFileHash());
    }

    private void UpdateHardware()
    {
        _updateVisitor.BeginUpdate();
        _computer.Accept(_updateVisitor);
    }

    private TelemetryMessage CreateSnapshot(IReadOnlyList<TelemetryHardwareSample> samples)
    {
        var cpu = TelemetrySnapshotMapper.SelectPrimaryCpu(samples);
        var needsFallback = cpu is not null && TelemetrySnapshotMapper.SelectCpuClockReading(cpu) is null;
        if (!needsFallback)
            _windowsCpuClock.Reset();
        _environment = _environment with
        {
            WindowsCpuClockMhz = needsFallback ? _windowsCpuClock.ReadMhz() : null,
        };
        var source = TelemetrySnapshotMapper.ResolveCpuClock(cpu, _environment)?.Source ?? "unavailable";
        if (source != _lastCpuClockSource)
        {
            _lastCpuClockSource = source;
            _logger.LogInformation("CPU clock source: {Source}; {Detail}", source,
                source == "unavailable" ? _windowsCpuClock.UnavailableReason : "reporting");
        }
        return TelemetrySnapshotMapper.CreateSnapshot(samples, ReadMemoryFallback(), _environment);
    }

    private void PublishSnapshot(TelemetryMessage snapshot)
    {
        lock (_snapshotLock)
        {
            _currentSnapshot = snapshot;
        }
    }

    private void WriteCpuAvailabilityWarnings(
        IReadOnlyList<TelemetryHardwareSample> samples,
        TelemetryEnvironment environment)
    {
        foreach (var warning in
            TelemetrySnapshotMapper.DescribeCpuAvailabilityWarnings(
                samples,
                environment))
        {
            _logger.LogWarning("{TelemetryWarning}", warning);
        }
    }

    private static void WriteEnvironment(
        TextWriter writer,
        TelemetryEnvironment environment)
    {
        writer.WriteLine($"LibreHardwareMonitorLib version: {environment.LibraryVersion}");
        writer.WriteLine(
            "LibreHardwareMonitorLib build: " +
            environment.LibraryInformationalVersion);
        writer.WriteLine(
            $"LibreHardwareMonitorLib file: {environment.LibraryLocation}");
        writer.WriteLine(
            $"LibreHardwareMonitorLib SHA-256: {environment.LibraryFileHash}");
        writer.WriteLine($"Elevated: {(environment.IsElevated ? "yes" : "no")}");
        writer.WriteLine(
            "Kernel driver (PawnIO): " +
            (environment.KernelDriver?.State.ToString() ??
                (environment.HasKernelDriver ? "present" : "NotInstalled")));
        writer.WriteLine(
            "Kernel driver detail: " +
            TelemetrySnapshotMapper.DescribeKernelDriver(environment));
        if (environment.KernelDriver?.ImagePath is { } imagePath)
        {
            writer.WriteLine($"Kernel driver image: {imagePath}");
        }
    }

    /// <summary>
    /// Prints every sensor grouped by its hardware, annotated with the reason it
    /// is accepted or rejected for a metric. Selection must never depend on a name
    /// match that is not visible here.
    /// </summary>
    private void WriteGroupedSensors(
        TextWriter writer,
        IReadOnlyList<TelemetryHardwareSample> samples,
        CancellationToken cancellationToken)
    {
        var primaryCpuIdentifier =
            TelemetrySnapshotMapper.SelectPrimaryCpu(samples)?.Identifier;
        var primaryGpuIdentifier =
            TelemetrySnapshotMapper.SelectPrimaryGpu(samples)?.Identifier;

        foreach (var hardware in EnumerateHardware(_computer.Hardware))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hardwareIdentifier = DisplayIdentifier(hardware.Identifier);
            var kind = MapHardwareKind(hardware.HardwareType);
            writer.WriteLine();
            writer.WriteLine(
                $"=== {hardware.HardwareType} | {hardware.Name} | " +
                $"{hardwareIdentifier} ===");
            if (hardware.Sensors.Length == 0)
            {
                writer.WriteLine("  (no sensors)");
                continue;
            }

            var updated = _updateVisitor.WasSuccessful(hardware);
            var owner = new TelemetryHardwareSample(
                kind,
                hardware.Name,
                Array.Empty<TelemetrySensorSample>(),
                hardwareIdentifier);
            var isPrimaryCpu = hardwareIdentifier == primaryCpuIdentifier;
            var isPrimaryGpu = hardwareIdentifier == primaryGpuIdentifier;

            foreach (var sensor in hardware.Sensors
                .OrderBy(sensor => sensor.SensorType)
                .ThenBy(sensor => sensor.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = CreateSensorSample(sensor);
                if (kind == TelemetryHardwareKind.Cpu && sensor.SensorType == SensorType.Voltage &&
                    _environment.KernelDriver?.IsUsable != true)
                {
                    writer.WriteLine($"  Voltage      | {sensor.Name,-32} | unavailable (raw reading rejected: PawnIO access not proven)");
                    continue;
                }
                var tags = updated
                    ? TelemetrySnapshotMapper.DescribeSensorTags(
                        owner,
                        sample,
                        isPrimaryCpu,
                        isPrimaryGpu)
                    : Array.Empty<string>();
                var suffix = tags.Count == 0
                    ? string.Empty
                    : "  " + string.Join(" ", tags);
                writer.WriteLine(
                    $"  {sensor.SensorType,-12} | {sensor.Name,-32} | " +
                    $"{DisplayIdentifier(sensor.Identifier),-30} | " +
                    $"value={(updated ? FormatValue(sensor.Value) : "unavailable"),-12} " +
                    $"min={FormatValue(sensor.Min),-12} " +
                    $"max={FormatValue(sensor.Max),-12}{suffix}");
            }
        }
    }

    internal static void WriteSelection(
        TextWriter writer,
        TelemetryMetricSelection selection)
    {
        var hardwareType = selection.Hardware?.Kind.ToString() ?? "unavailable";
        var hardwareName = selection.Hardware?.Name ?? "unavailable";
        var hardwareIdentifier = selection.Hardware?.Identifier ?? "unavailable";
        if (selection.Sensors.Count == 0)
        {
            writer.WriteLine(
                $"{selection.Metric} | {hardwareType} | {hardwareName} | " +
                $"{hardwareIdentifier} | unavailable | unavailable | " +
                "unavailable | unavailable");
            return;
        }

        foreach (var sensor in selection.Sensors)
        {
            writer.WriteLine(
                $"{selection.Metric} | {hardwareType} | {hardwareName} | " +
                $"{hardwareIdentifier} | {sensor.Kind} | {sensor.Name} | " +
                $"{FormatValue(sensor.Value)} | " +
                (sensor.Identifier ?? "unavailable"));
        }
    }

    private static string? ReadIdentifier(object? identifier)
    {
        var value = identifier?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string DisplayIdentifier(object? identifier) =>
        ReadIdentifier(identifier) ?? "unavailable";

    private static string FormatValue(double? value) =>
        value.HasValue && double.IsFinite(value.Value)
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "unavailable";

    private void OnHardwareChanged(IHardware hardware)
    {
        _ = hardware;
        Interlocked.Exchange(ref _rediscoveryRequested, 1);
    }

    private void CloseComputer()
    {
        lock (_computerLock)
        {
            if (!_computerOpened)
            {
                return;
            }

            _computer.HardwareAdded -= OnHardwareChanged;
            _computer.HardwareRemoved -= OnHardwareChanged;
            try
            {
                _computer.Close();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Hardware telemetry shutdown failed");
            }
            finally
            {
                _computerOpened = false;
                _bindings = Array.Empty<HardwareBinding>();
            }
        }
    }

    private static IEnumerable<IHardware> EnumerateHardware(
        IEnumerable<IHardware> hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            yield return hardware;
            foreach (var subHardware in EnumerateHardware(hardware.SubHardware))
            {
                yield return subHardware;
            }
        }
    }

    private static TelemetryHardwareKind MapHardwareKind(HardwareType type) => type switch
    {
        HardwareType.Cpu => TelemetryHardwareKind.Cpu,
        HardwareType.GpuNvidia => TelemetryHardwareKind.GpuNvidia,
        HardwareType.GpuAmd => TelemetryHardwareKind.GpuAmd,
        HardwareType.GpuIntel => TelemetryHardwareKind.GpuIntel,
        HardwareType.Memory => TelemetryHardwareKind.Memory,
        HardwareType.Storage => TelemetryHardwareKind.Storage,
        _ => TelemetryHardwareKind.Other,
    };

    private static TelemetrySensorKind MapSensorKind(SensorType type) => type switch
    {
        SensorType.Temperature => TelemetrySensorKind.Temperature,
        SensorType.Load => TelemetrySensorKind.Load,
        SensorType.Clock => TelemetrySensorKind.Clock,
        SensorType.Power => TelemetrySensorKind.Power,
        SensorType.Data => TelemetrySensorKind.Data,
        SensorType.SmallData => TelemetrySensorKind.SmallData,
        SensorType.Voltage => TelemetrySensorKind.Voltage,
        _ => TelemetrySensorKind.Other,
    };

    private static MemoryCapacitySnapshot? ReadMemoryFallback()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0)
        {
            return null;
        }

        const double bytesPerGigabyte = 1024.0 * 1024.0 * 1024.0;
        var totalGb = status.TotalPhysical / bytesPerGigabyte;
        var usedGb = (status.TotalPhysical - status.AvailablePhysical) /
            bytesPerGigabyte;
        return new MemoryCapacitySnapshot(usedGb, totalGb);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;

        public MemoryStatusEx()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }

    private sealed record HardwareBinding(
        IHardware Hardware,
        TelemetryHardwareKind Kind);

    /// <summary>
    /// Updates every hardware node before its sensors are read, including
    /// sub-hardware, and remembers which nodes threw so their stale sensors are
    /// never published as measurements. Internal so the update contract itself
    /// can be tested rather than assumed.
    /// </summary>
    internal sealed class UpdateVisitor : IVisitor
    {
        private readonly ILogger _logger;
        private readonly HashSet<IHardware> _failedHardware =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _reportedFailures =
            new(StringComparer.OrdinalIgnoreCase);

        internal UpdateVisitor(ILogger? logger = null)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public void BeginUpdate() => _failedHardware.Clear();

        public bool WasSuccessful(IHardware hardware) =>
            !_failedHardware.Contains(hardware);

        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch (Exception exception)
            {
                _failedHardware.Add(hardware);
                var identifier = hardware.Identifier.ToString();
                if (_reportedFailures.Add(identifier))
                {
                    _logger.LogWarning(
                        exception,
                        "Telemetry hardware {HardwareType} {HardwareName} could not be updated",
                        hardware.HardwareType,
                        hardware.Name);
                }
            }

            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }
}
