using PanelOS.Telemetry;

namespace PanelHost.Services;

/// <summary>
/// Why a metric has no value. "Unavailable" collapses six different situations
/// into one word and hides the only distinction that matters when a sensor
/// regresses: whether the hardware cannot report it, or whether PanelHost cannot
/// reach the hardware at all.
/// </summary>
internal enum TelemetryMetricHealth
{
    /// <summary>A sensor was selected and reported a usable value.</summary>
    Available,

    /// <summary>The library exposes no sensor of this kind for the device.</summary>
    SensorNotPresent,

    /// <summary>The sensor exists but reported nothing usable this cycle.</summary>
    SensorPresentNoValue,

    /// <summary>PanelHost cannot reach the hardware (kernel driver unusable).</summary>
    HardwareAccessFailure,

    /// <summary>The hardware interface exists but refuses this process.</summary>
    PermissionDenied,

    /// <summary>
    /// The hardware genuinely does not expose the metric. Only ever concluded
    /// when hardware access is proven working <em>and</em> the library still
    /// exposes no sensor - never inferred from an empty reading.
    /// </summary>
    UnsupportedHardware,
}

/// <summary>A metric's health plus the fact that produced it.</summary>
internal sealed record TelemetryMetricDiagnosis(
    string Metric,
    TelemetryMetricHealth Health,
    string Detail,
    string? Source = null);

internal static partial class TelemetrySnapshotMapper
{
    private static bool HasKnownCpuAccessFailure(TelemetryEnvironment? environment) =>
        environment?.KernelDriver?.State is KernelDriverState.Blocked or
            KernelDriverState.AccessDenied or KernelDriverState.NotInstalled or
            KernelDriverState.InstalledNotLoaded;

    internal static TelemetryMetricDiagnosis DiagnoseCpuVoltage(
        TelemetryHardwareSample? cpu, TelemetryEnvironment environment)
    {
        if (environment.KernelDriver?.IsUsable != true)
            return new("CPU voltage", ClassifyAccess(environment) ?? TelemetryMetricHealth.HardwareAccessFailure,
                "unavailable: PawnIO access is not proven; raw voltage values are rejected.");
        var sensor = cpu?.Sensors.FirstOrDefault(sensor =>
            sensor.Kind == TelemetrySensorKind.Voltage && sensor.Value is > 0 and < 3);
        return sensor is null
            ? new("CPU voltage", TelemetryMetricHealth.SensorPresentNoValue, "unavailable: no valid voltage reading.")
            : new("CPU voltage", TelemetryMetricHealth.Available, $"'{sensor.Name}' is reporting.", "LibreHardwareMonitor");
    }

    /// <summary>
    /// Classifies CPU temperature availability. Selection and diagnosis share
    /// <see cref="SelectCpuTemperature"/>, so the diagnosis can never disagree
    /// with the value published in the snapshot.
    /// </summary>
    internal static TelemetryMetricDiagnosis DiagnoseCpuTemperature(
        TelemetryHardwareSample? cpu,
        TelemetryEnvironment? environment = null)
    {
        environment ??= new TelemetryEnvironment();
        if (cpu is null)
        {
            return new TelemetryMetricDiagnosis(
                "CPU temperature",
                TelemetryMetricHealth.HardwareAccessFailure,
                "no CPU hardware was enumerated.");
        }

        if (HasKnownCpuAccessFailure(environment))
            return new("CPU temperature", ClassifyAccess(environment) ?? TelemetryMetricHealth.HardwareAccessFailure,
                "unavailable: kernel driver access is not usable.");

        if (SelectCpuTemperature(cpu) is { } selected)
        {
            return new TelemetryMetricDiagnosis(
                "CPU temperature",
                TelemetryMetricHealth.Available,
                $"'{selected.Name}' is reporting.");
        }

        var present = cpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.Temperature)
            .ToArray();
        return present.Length == 0
            ? new TelemetryMetricDiagnosis(
                "CPU temperature",
                ClassifyAbsentSensor(environment),
                "the library exposes no CPU temperature sensor.")
            : new TelemetryMetricDiagnosis(
                "CPU temperature",
                ClassifyValuelessSensor(environment),
                $"{DescribeSensorNames(present)} present but reporting no valid " +
                "value.");
    }

    /// <summary>
    /// Classifies CPU clock availability over the same candidate set the clock
    /// selection uses, so "present" means "would have been used had it reported".
    /// </summary>
    internal static TelemetryMetricDiagnosis DiagnoseCpuClock(
        TelemetryHardwareSample? cpu,
        TelemetryEnvironment? environment = null)
    {
        environment ??= new TelemetryEnvironment();
        if (cpu is null)
        {
            return new TelemetryMetricDiagnosis(
                "CPU clock",
                TelemetryMetricHealth.HardwareAccessFailure,
                "no CPU hardware was enumerated.");
        }

        if (ResolveCpuClock(cpu, environment) is { } reading)
        {
            return new TelemetryMetricDiagnosis(
                "CPU clock",
                TelemetryMetricHealth.Available,
                $"{reading.Definition} is reporting.", reading.Source);
        }

        var present = CpuClockCandidateSensors(cpu);
        return present.Count == 0
            ? new TelemetryMetricDiagnosis(
                "CPU clock",
                ClassifyAbsentSensor(environment),
                "the library exposes no CPU core clock sensor.")
            : new TelemetryMetricDiagnosis(
                "CPU clock",
                ClassifyValuelessSensor(environment),
                $"{present.Count} core clock sensor(s) present, none reporting a " +
                "valid value.");
    }

    /// <summary>
    /// Core clock sensors that survive the non-core name filter: the exact set
    /// <see cref="SelectCpuClockReading"/> considers before value plausibility.
    /// </summary>
    internal static IReadOnlyList<TelemetrySensorSample> CpuClockCandidateSensors(
        TelemetryHardwareSample cpu) =>
        cpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.Clock)
            .Where(sensor => !Matches(sensor.Name, NameHints.NonCoreClock))
            .ToArray();

    /// <summary>
    /// Reports what is actually known about missing CPU sensors. It never
    /// concludes "unsupported" from an empty reading: that verdict requires the
    /// kernel driver to be demonstrably usable and the sensor to be absent.
    /// </summary>
    internal static IReadOnlyList<string> DescribeCpuAvailabilityWarnings(
        IReadOnlyList<TelemetryHardwareSample> hardware,
        TelemetryEnvironment? environment = null)
    {
        environment ??= new TelemetryEnvironment();
        var cpu = SelectPrimaryCpu(hardware);
        var diagnoses = new[]
        {
            DiagnoseCpuTemperature(cpu, environment),
            DiagnoseCpuClock(cpu, environment),
        };

        var degraded = diagnoses
            .Where(diagnosis => diagnosis.Health != TelemetryMetricHealth.Available)
            .ToArray();
        if (degraded.Length == 0)
        {
            return Array.Empty<string>();
        }

        var warnings = degraded
            .Select(diagnosis =>
                $"[Telemetry] {diagnosis.Metric}: {diagnosis.Health} - " +
                diagnosis.Detail)
            .ToList();

        warnings.Add(
            "[Telemetry] PanelHost elevated: " +
            (environment.IsElevated ? "yes" : "no") + ".");
        warnings.Add($"[Telemetry] {DescribeKernelDriver(environment)}");

        // Ryzen Tctl/Tdie and per-core P-state clocks come from the SMU and MSRs,
        // which LibreHardwareMonitor reaches through its PawnIO kernel modules.
        // Name the actual blocker instead of guessing at a hardware limitation.
        if (degraded.Any(diagnosis =>
            diagnosis.Health == TelemetryMetricHealth.HardwareAccessFailure))
        {
            warnings.Add(
                "[Telemetry] AMD Tctl/Tdie and per-core P-state clocks are read " +
                "through that kernel driver; the remaining telemetry is " +
                "unaffected.");
        }
        else if (degraded.Any(diagnosis =>
            diagnosis.Health == TelemetryMetricHealth.PermissionDenied))
        {
            warnings.Add(
                "[Telemetry] Run PanelHost as Administrator so it may open the " +
                "kernel driver.");
        }
        else if (degraded.Any(diagnosis =>
            diagnosis.Health == TelemetryMetricHealth.SensorPresentNoValue))
        {
            warnings.Add(
                "[Telemetry] Hardware access is working, so the sensor is " +
                "expected to report on a later sample; run PanelHost " +
                "--telemetry-debug-cpu for a raw sensor dump if it does not.");
        }

        return warnings;
    }

    /// <summary>
    /// One line naming the driver's real state, including the Windows problem
    /// code when Windows is the component refusing to load it.
    /// </summary>
    internal static string DescribeKernelDriver(TelemetryEnvironment environment)
    {
        if (environment.KernelDriver is { } status)
        {
            return status.Summary;
        }

        return environment.HasKernelDriver
            ? "PawnIO kernel driver: installed (functional state not probed)."
            : "PawnIO kernel driver: not installed, so AMD SMU/MSR sensors " +
                "cannot be read regardless of privileges.";
    }

    /// <summary>
    /// A missing sensor is a hardware limitation only when hardware access is
    /// proven to work. Otherwise the sensor may simply never have been created,
    /// because the library skips sensors it cannot back with a driver.
    /// </summary>
    private static TelemetryMetricHealth ClassifyAbsentSensor(
        TelemetryEnvironment environment) =>
        environment.KernelDriver?.IsUsable == true
            ? TelemetryMetricHealth.UnsupportedHardware
            : ClassifyAccess(environment) ?? TelemetryMetricHealth.SensorNotPresent;

    /// <summary>
    /// A sensor that exists but reads nothing points at the access path first;
    /// only a working access path makes "no value yet" the honest answer.
    /// </summary>
    private static TelemetryMetricHealth ClassifyValuelessSensor(
        TelemetryEnvironment environment) =>
        ClassifyAccess(environment) ?? TelemetryMetricHealth.SensorPresentNoValue;

    /// <summary>
    /// Access-path verdict, or null when the path is known to be healthy (or
    /// nothing was probed and no privilege problem is visible).
    /// </summary>
    private static TelemetryMetricHealth? ClassifyAccess(
        TelemetryEnvironment environment)
    {
        if (environment.KernelDriver is { } status)
        {
            return status.State switch
            {
                KernelDriverState.NotInstalled or
                KernelDriverState.InstalledNotLoaded or
                KernelDriverState.Blocked =>
                    TelemetryMetricHealth.HardwareAccessFailure,
                KernelDriverState.AccessDenied =>
                    TelemetryMetricHealth.PermissionDenied,
                _ => null,
            };
        }

        if (!environment.HasKernelDriver)
        {
            return TelemetryMetricHealth.HardwareAccessFailure;
        }

        return environment.IsElevated
            ? null
            : TelemetryMetricHealth.PermissionDenied;
    }
}
