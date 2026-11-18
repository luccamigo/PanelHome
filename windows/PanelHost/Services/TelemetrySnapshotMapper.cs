using PanelHost.Protocol;

using PanelOS.Telemetry;

namespace PanelHost.Services;

internal enum TelemetryHardwareKind
{
    Cpu,
    GpuNvidia,
    GpuAmd,
    GpuIntel,
    Memory,
    Storage,
    Other,
}

internal enum TelemetrySensorKind
{
    Temperature,
    Load,
    Clock,
    Power,
    Data,
    SmallData,
    Voltage,
    Other,
}

internal sealed record TelemetrySensorSample(
    string Name,
    TelemetrySensorKind Kind,
    int Index,
    double? Value,
    string? Identifier = null,
    double? Minimum = null,
    double? Maximum = null);

internal sealed record TelemetryHardwareSample(
    TelemetryHardwareKind Kind,
    string Name,
    IReadOnlyList<TelemetrySensorSample> Sensors,
    string? Identifier = null);

internal sealed record TelemetryMetricSelection(
    string Metric,
    TelemetryHardwareSample? Hardware,
    IReadOnlyList<TelemetrySensorSample> Sensors);

internal sealed record MemoryCapacitySnapshot(double? UsedGb, double? TotalGb);

/// <summary>Host environment facts that explain missing sensors.</summary>
/// <param name="LibraryVersion">Assembly version of the loaded library.</param>
/// <param name="IsElevated">Whether PanelHost runs with Administrator rights.</param>
/// <param name="HasKernelDriver">
/// Whether a PawnIO driver package exists at all. Presence is not usability:
/// <paramref name="KernelDriver"/> carries the functional verdict.
/// </param>
/// <param name="KernelDriver">Result of actually probing the driver.</param>
/// <param name="LibraryInformationalVersion">
/// Build identity of the library (<c>0.9.6+&lt;commit&gt;</c>). The assembly
/// version alone does not identify which build is running.
/// </param>
/// <param name="LibraryLocation">File the library was loaded from.</param>
/// <param name="LibraryFileHash">SHA-256 of that file.</param>
internal sealed record TelemetryEnvironment(
    string LibraryVersion = "unknown",
    bool IsElevated = false,
    bool HasKernelDriver = false,
    KernelDriverStatus? KernelDriver = null,
    string LibraryInformationalVersion = "unknown",
    string LibraryLocation = "unknown",
    string LibraryFileHash = "unknown",
    double? WindowsCpuClockMhz = null);

/// <summary>Where a VRAM occupancy pair came from.</summary>
internal enum VramSource
{
    /// <summary>"GPU Memory Used" + "GPU Memory Total" (NVML / ADL).</summary>
    GpuMemoryUsedTotal,

    /// <summary>"GPU Memory Total" - "GPU Memory Free", same source.</summary>
    GpuMemoryFreeTotal,

    /// <summary>"D3D Dedicated Memory Used" + a D3D dedicated total.</summary>
    D3DDedicated,
}

/// <summary>
/// A VRAM occupancy reading whose used and total values are guaranteed to come
/// from the same GPU, the same measurement source, and the same sample cycle.
/// Percent is always derived here; it is never read from a sensor.
/// </summary>
internal sealed record VramReading(
    double UsedBytes,
    double TotalBytes,
    double UsagePercent,
    string UsedSensorName,
    string TotalSensorName,
    VramSource Source);

/// <summary>A single representative CPU frequency and how it was derived.</summary>
internal sealed record CpuClockReading(
    double Mhz,
    string Definition,
    IReadOnlyList<TelemetrySensorSample> Sensors,
    string Source = "LibreHardwareMonitor");

internal static partial class TelemetrySnapshotMapper
{
    // LibreHardwareMonitor reports GPU SmallData memory sensors in mebibytes.
    // Verified against this hardware rather than inferred from magnitude:
    // LHM "GPU Memory Total" = 8151 and `nvidia-smi memory.total` = 8151 MiB.
    private const double BytesPerMebibyte = 1024.0 * 1024.0;
    private const double BytesPerGibibyte = 1024.0 * 1024.0 * 1024.0;

    // Physically implausible readings are rejected outright rather than clamped,
    // so sensor garbage can never masquerade as a valid measurement.
    private const double MaxPlausibleCpuTemperatureC = 125.0;
    private const double MaxPlausibleClockMhz = 10_000.0;

    // A used value may exceed total by a hair through independent rounding of the
    // two sensors. Anything past this is treated as incoherent, not clamped.
    private const double VramOvershootTolerance = 0.01;

    public static TelemetryMessage CreateSnapshot(
        IReadOnlyList<TelemetryHardwareSample> hardware,
        MemoryCapacitySnapshot? memoryFallback = null,
        TelemetryEnvironment? environment = null)
    {
        var cpu = SelectPrimaryCpu(hardware);
        var gpu = SelectPrimaryGpu(hardware);
        var memory = SelectMemoryHardware(hardware);

        return new TelemetryMessage
        {
            Cpu = CreateCpuSnapshot(cpu, environment),
            Gpu = CreateGpuSnapshot(gpu),
            Memory = CreateMemorySnapshot(memory, memoryFallback),
            Storage = hardware
                .Where(candidate => candidate.Kind == TelemetryHardwareKind.Storage)
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CreateStorageSnapshot)
                .ToArray(),
        };
    }

    internal static TelemetryHardwareSample? SelectPrimaryCpu(
        IReadOnlyList<TelemetryHardwareSample> hardware) =>
        hardware
            .Where(candidate => candidate.Kind == TelemetryHardwareKind.Cpu)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Identifier, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// Picks the panel's GPU. Discrete parts outrank integrated ones so a Ryzen
    /// APU's Radeon Graphics can never displace the dedicated card, and the
    /// dedicated-VRAM tiebreak ignores shared-memory totals.
    /// </summary>
    internal static TelemetryHardwareSample? SelectPrimaryGpu(
        IReadOnlyList<TelemetryHardwareSample> hardware) =>
        hardware
            .Where(candidate => IsGpu(candidate.Kind))
            .Select(candidate => new
            {
                Hardware = candidate,
                DedicatedMemoryBytes = SelectVramTotalBytes(candidate),
            })
            .OrderByDescending(candidate =>
                GetGpuTypePriority(candidate.Hardware.Kind))
            .ThenByDescending(candidate => candidate.DedicatedMemoryBytes.HasValue)
            .ThenByDescending(candidate => candidate.DedicatedMemoryBytes)
            .ThenBy(candidate => candidate.Hardware.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Hardware)
            .FirstOrDefault();

    internal static IReadOnlyList<string> DescribeSelections(
        IReadOnlyList<TelemetryHardwareSample> hardware,
        TelemetryEnvironment? environment = null)
    {
        environment ??= new TelemetryEnvironment();
        var descriptions = new List<string>
        {
            $"[Telemetry] LibreHardwareMonitorLib version: {environment.LibraryVersion}",
            $"[Telemetry] Elevated: {(environment.IsElevated ? "yes" : "no")}",
        };

        var cpu = SelectPrimaryCpu(hardware);
        if (cpu is null)
        {
            descriptions.Add("[Telemetry] CPU: unavailable");
        }
        else
        {
            descriptions.Add($"[Telemetry] CPU: {cpu.Name}");
            descriptions.Add(DescribeCpuTemperatureSelection(cpu, environment));
            descriptions.Add(DescribeSensor("CPU Load", SelectCpuLoad(cpu)));
            descriptions.Add(DescribeCpuClockSelection(cpu, environment));
            descriptions.Add(DescribeDegraded("CPU Voltage", DiagnoseCpuVoltage(cpu, environment)));
        }

        var gpu = SelectPrimaryGpu(hardware);
        if (gpu is null)
        {
            descriptions.Add("[Telemetry] GPU: unavailable");
        }
        else
        {
            descriptions.Add($"[Telemetry] GPU: {gpu.Name}");
            descriptions.Add(DescribeSensor(
                "GPU Temperature",
                SelectGpuTemperature(gpu)));
            descriptions.Add(DescribeSensor("GPU Load", SelectGpuLoad(gpu)));

            var vram = SelectVram(gpu);
            if (vram is null)
            {
                descriptions.Add("[Telemetry] VRAM Used: unavailable");
                descriptions.Add("[Telemetry] VRAM Total: unavailable");
                descriptions.Add("[Telemetry] VRAM Percent: unavailable");
            }
            else
            {
                descriptions.Add($"[Telemetry] VRAM Used: {vram.UsedSensorName}");
                descriptions.Add($"[Telemetry] VRAM Total: {vram.TotalSensorName}");
                descriptions.Add("[Telemetry] VRAM Percent: derived (used / total)");
            }

            var activity = SelectGpuMemoryActivity(gpu);
            if (activity is not null)
            {
                descriptions.Add(
                    $"[Telemetry] GPU Memory activity sensor '{activity.Name}' is " +
                    "reported as activity only and never as VRAM occupancy.");
            }
        }

        descriptions.Add(hardware.Any(candidate =>
            candidate.Kind == TelemetryHardwareKind.Memory)
            ? "[Telemetry] Memory: detected"
            : "[Telemetry] Memory: library sensors unavailable; Windows fallback enabled");
        return descriptions;
    }

    internal static IReadOnlyList<TelemetryMetricSelection> SelectMetrics(
        IReadOnlyList<TelemetryHardwareSample> hardware)
    {
        var selections = new List<TelemetryMetricSelection>();
        var cpu = SelectPrimaryCpu(hardware);
        selections.Add(new TelemetryMetricSelection(
            "CPU Temperature",
            cpu,
            SelectSingle(cpu is null ? null : SelectCpuTemperature(cpu))));
        selections.Add(new TelemetryMetricSelection(
            "CPU Load",
            cpu,
            SelectSingle(cpu is null ? null : SelectCpuLoad(cpu))));
        selections.Add(new TelemetryMetricSelection(
            "CPU Clock",
            cpu,
            cpu is null
                ? Array.Empty<TelemetrySensorSample>()
                : SelectCpuClockReading(cpu)?.Sensors
                    ?? Array.Empty<TelemetrySensorSample>()));
        selections.Add(new TelemetryMetricSelection(
            "CPU Power",
            cpu,
            SelectSingle(cpu is null ? null : SelectCpuPower(cpu))));

        var gpu = SelectPrimaryGpu(hardware);
        selections.Add(new TelemetryMetricSelection(
            "GPU Temperature",
            gpu,
            SelectSingle(gpu is null ? null : SelectGpuTemperature(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "GPU Load",
            gpu,
            SelectSingle(gpu is null ? null : SelectGpuLoad(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "GPU Clock",
            gpu,
            SelectSingle(gpu is null ? null : SelectGpuClock(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "VRAM Used",
            gpu,
            SelectSingle(gpu is null ? null : SelectVramUsedSensor(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "VRAM Total",
            gpu,
            SelectSingle(gpu is null ? null : SelectVramTotalSensor(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "GPU Memory Activity (not VRAM occupancy)",
            gpu,
            SelectSingle(gpu is null ? null : SelectGpuMemoryActivity(gpu))));
        selections.Add(new TelemetryMetricSelection(
            "GPU Power",
            gpu,
            SelectSingle(gpu is null ? null : SelectGpuPower(gpu))));

        var memory = SelectMemoryHardware(hardware);
        var memoryUsed = SelectByName(
            memory,
            TelemetrySensorKind.Data,
            NameHints.Used);
        var memoryAvailable = SelectByName(
            memory,
            TelemetrySensorKind.Data,
            NameHints.Available);
        selections.Add(new TelemetryMetricSelection(
            "Memory Used",
            memory,
            SelectSingle(memoryUsed)));
        selections.Add(new TelemetryMetricSelection(
            "Memory Available",
            memory,
            SelectSingle(memoryAvailable)));
        selections.Add(new TelemetryMetricSelection(
            "Memory Total",
            memory,
            memoryUsed is not null && memoryAvailable is not null
                ? [memoryUsed, memoryAvailable]
                : Array.Empty<TelemetrySensorSample>()));
        selections.Add(new TelemetryMetricSelection(
            "Memory Load",
            memory,
            SelectSingle(SelectMemoryLoad(memory))));

        foreach (var storage in hardware
            .Where(candidate => candidate.Kind == TelemetryHardwareKind.Storage)
            .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            selections.Add(new TelemetryMetricSelection(
                "Storage Temperature",
                storage,
                SelectSingle(SelectStorageTemperature(storage))));
            selections.Add(new TelemetryMetricSelection(
                "Storage Load",
                storage,
                SelectSingle(SelectStorageLoad(storage))));
        }

        return selections;
    }

    /// <summary>
    /// Tags a sensor for <c>--dump-sensors</c> so every accept/reject decision is
    /// visible in the dump instead of being buried in name matching.
    /// </summary>
    internal static IReadOnlyList<string> DescribeSensorTags(
        TelemetryHardwareSample hardware,
        TelemetrySensorSample sensor,
        bool isPrimaryCpu,
        bool isPrimaryGpu)
    {
        var tags = new List<string>();
        if (isPrimaryCpu && hardware.Kind == TelemetryHardwareKind.Cpu)
        {
            if (sensor.Kind == TelemetrySensorKind.Temperature &&
                RankCpuTemperature(sensor) < CpuTemperatureRejectRank)
            {
                tags.Add(IsPlausibleCpuTemperature(sensor)
                    ? "[Candidate:CPU_TEMP]"
                    : "[Rejected:CPU_TEMP_INVALID_VALUE]");
            }

            if (sensor.Kind == TelemetrySensorKind.Clock)
            {
                tags.Add(Matches(sensor.Name, NameHints.NonCoreClock)
                    ? "[Rejected:CPU_CLOCK_NON_CORE]"
                    : IsPlausibleClock(sensor)
                        ? "[Candidate:CPU_CLOCK]"
                        : "[Rejected:CPU_CLOCK_INVALID_VALUE]");
            }
        }

        if (isPrimaryGpu && IsGpu(hardware.Kind))
        {
            if (IsVramUsedCandidate(sensor))
            {
                tags.Add("[Candidate:VRAM_USED]");
            }

            if (IsVramTotalCandidate(sensor))
            {
                tags.Add("[Candidate:VRAM_TOTAL]");
            }

            if (IsGpuMemoryActivity(sensor))
            {
                tags.Add("[Rejected:GPU_MEMORY_ACTIVITY]");
            }

            if (sensor.Kind == TelemetrySensorKind.SmallData &&
                Matches(sensor.Name, NameHints.Shared))
            {
                tags.Add("[Rejected:GPU_SHARED_MEMORY]");
            }
        }

        return tags;
    }

    private static CpuTelemetry CreateCpuSnapshot(
        TelemetryHardwareSample? cpu, TelemetryEnvironment? environment)
    {
        if (cpu is null)
        {
            return new CpuTelemetry();
        }

        return new CpuTelemetry
        {
            Name = EmptyToNull(cpu.Name),
            TemperatureC = HasKnownCpuAccessFailure(environment)
                ? null : Round(Read(SelectCpuTemperature(cpu)), 1),
            UsagePercent = Round(Read(SelectCpuLoad(cpu)), 1),
            ClockGhz = Round(Divide(ResolveCpuClock(cpu, environment)?.Mhz, 1000.0), 3),
            PowerW = HasKnownCpuAccessFailure(environment)
                ? null : Round(Read(SelectCpuPower(cpu)), 1),
        };
    }

    private static GpuTelemetry CreateGpuSnapshot(TelemetryHardwareSample? gpu)
    {
        if (gpu is null)
        {
            return new GpuTelemetry();
        }

        // Used, total and percent are taken from one coherent reading so the three
        // can never disagree, and percent is never sourced from a load sensor.
        var vram = SelectVram(gpu);
        var totalBytes = vram?.TotalBytes ?? SelectVramTotalBytes(gpu);

        return new GpuTelemetry
        {
            Name = EmptyToNull(gpu.Name),
            TemperatureC = Round(Read(SelectGpuTemperature(gpu)), 1),
            UsagePercent = Round(Read(SelectGpuLoad(gpu)), 1),
            ClockGhz = Round(Divide(Read(SelectGpuClock(gpu)), 1000.0), 3),
            MemoryUsagePercent = Round(vram?.UsagePercent, 1),
            MemoryUsedGb = Round(Divide(vram?.UsedBytes, BytesPerGibibyte), 2),
            MemoryTotalGb = Round(Divide(totalBytes, BytesPerGibibyte), 2),
            PowerW = Round(Read(SelectGpuPower(gpu)), 1),
        };
    }

    private static MemoryTelemetry CreateMemorySnapshot(
        TelemetryHardwareSample? memory,
        MemoryCapacitySnapshot? fallback)
    {
        var used = Read(SelectByName(
            memory,
            TelemetrySensorKind.Data,
            NameHints.Used));
        var available = Read(SelectByName(
            memory,
            TelemetrySensorKind.Data,
            NameHints.Available));
        var total = used.HasValue && available.HasValue
            ? used + available
            : null;

        used ??= Normalized(fallback?.UsedGb);
        total ??= Normalized(fallback?.TotalGb);
        var usage = Read(SelectMemoryLoad(memory));
        if (!usage.HasValue && used.HasValue && total > 0)
        {
            usage = (used.Value / total.Value) * 100.0;
        }

        return new MemoryTelemetry
        {
            UsedGb = Round(used, 2),
            TotalGb = Round(total, 2),
            UsagePercent = Round(usage, 1),
        };
    }

    private static StorageTelemetry CreateStorageSnapshot(
        TelemetryHardwareSample storage) => new()
        {
            Name = storage.Name,
            TemperatureC = Round(Read(SelectStorageTemperature(storage)), 1),
            UsagePercent = Round(Read(SelectStorageLoad(storage)), 1),
        };

    // ---------------------------------------------------------------- VRAM ---

    /// <summary>
    /// Resolves VRAM occupancy for one GPU. Both halves of the pair are read from
    /// the same <see cref="TelemetryHardwareSample"/>, which the sampler fills in
    /// a single update cycle, so used and total are always from one snapshot and
    /// never from two different GPUs.
    /// </summary>
    internal static VramReading? SelectVram(TelemetryHardwareSample? gpu) =>
        gpu is null ? null : SelectDedicatedVram(gpu) ?? SelectD3DVram(gpu);

    /// <summary>
    /// The preferred path: the driver's own memory counters (NVML on NVIDIA, ADL
    /// on AMD), whose used and total are defined against each other.
    /// </summary>
    private static VramReading? SelectDedicatedVram(TelemetryHardwareSample gpu)
    {
        var total = SelectVramTotalSensor(gpu);
        if (total is null || Read(total) is not { } totalMb || totalMb <= 0)
        {
            // Without a trustworthy capacity there is no honest percentage.
            return null;
        }

        var totalBytes = totalMb * BytesPerMebibyte;

        var used = SelectVramUsedSensor(gpu);
        if (used is not null && Read(used) is { } usedMb)
        {
            return BuildVram(
                usedMb * BytesPerMebibyte,
                totalBytes,
                used.Name,
                total.Name,
                VramSource.GpuMemoryUsedTotal);
        }

        // Same source, same units: total - free is a coherent substitute.
        var free = SelectVramFreeSensor(gpu);
        if (free is not null && Read(free) is { } freeMb && freeMb >= 0)
        {
            return BuildVram(
                totalBytes - (freeMb * BytesPerMebibyte),
                totalBytes,
                $"{total.Name} - {free.Name}",
                total.Name,
                VramSource.GpuMemoryFreeTotal);
        }

        return null;
    }

    /// <summary>
    /// Last resort. D3D accounting differs from the driver counters, so it is only
    /// ever used as a complete pair; it is never mixed with "GPU Memory Total" or
    /// with shared system memory.
    /// </summary>
    private static VramReading? SelectD3DVram(TelemetryHardwareSample gpu)
    {
        var d3dUsed = SelectD3DDedicatedUsedSensor(gpu);
        var d3dTotal = SelectD3DDedicatedTotalSensor(gpu);
        if (d3dUsed is null ||
            d3dTotal is null ||
            Read(d3dUsed) is not { } usedMb ||
            Read(d3dTotal) is not { } totalMb ||
            totalMb <= 0)
        {
            return null;
        }

        return BuildVram(
            usedMb * BytesPerMebibyte,
            totalMb * BytesPerMebibyte,
            d3dUsed.Name,
            d3dTotal.Name,
            VramSource.D3DDedicated);
    }

    private static VramReading? BuildVram(
        double usedBytes,
        double totalBytes,
        string usedSensorName,
        string totalSensorName,
        VramSource source)
    {
        if (!double.IsFinite(usedBytes) ||
            !double.IsFinite(totalBytes) ||
            totalBytes <= 0 ||
            usedBytes < 0)
        {
            return null;
        }

        // A large overshoot means the two sensors disagree about what they are
        // measuring; report nothing rather than clamping garbage into range.
        if (usedBytes > totalBytes * (1.0 + VramOvershootTolerance))
        {
            return null;
        }

        var clampedUsed = Math.Min(usedBytes, totalBytes);
        return new VramReading(
            clampedUsed,
            totalBytes,
            (clampedUsed / totalBytes) * 100.0,
            usedSensorName,
            totalSensorName,
            source);
    }

    private static double? SelectVramTotalBytes(TelemetryHardwareSample gpu) =>
        Read(SelectVramTotalSensor(gpu)) is { } totalMb && totalMb > 0
            ? totalMb * BytesPerMebibyte
            : null;

    internal static TelemetrySensorSample? SelectVramUsedSensor(
        TelemetryHardwareSample gpu) =>
        gpu.Sensors
            .Where(IsVramUsedCandidate)
            .Where(sensor => Read(sensor) >= 0)
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    internal static TelemetrySensorSample? SelectVramTotalSensor(
        TelemetryHardwareSample gpu) =>
        gpu.Sensors
            .Where(IsVramTotalCandidate)
            .Where(sensor => Read(sensor) > 0)
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    private static TelemetrySensorSample? SelectVramFreeSensor(
        TelemetryHardwareSample gpu) =>
        gpu.Sensors
            .Where(sensor => IsDedicatedMemorySmallData(sensor) &&
                Matches(sensor.Name, NameHints.MemoryFree))
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    private static TelemetrySensorSample? SelectD3DDedicatedUsedSensor(
        TelemetryHardwareSample gpu) =>
        gpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.SmallData &&
                Matches(sensor.Name, NameHints.D3DDedicated) &&
                Matches(sensor.Name, NameHints.Used) &&
                !Matches(sensor.Name, NameHints.Shared))
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    private static TelemetrySensorSample? SelectD3DDedicatedTotalSensor(
        TelemetryHardwareSample gpu) =>
        gpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.SmallData &&
                Matches(sensor.Name, NameHints.D3DDedicated) &&
                Matches(sensor.Name, NameHints.Total) &&
                !Matches(sensor.Name, NameHints.Shared))
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    /// <summary>
    /// "GPU Memory" / "GPU Memory Controller" of <see cref="TelemetrySensorKind.Load"/>
    /// measure memory subsystem activity, not the fraction of VRAM occupied. They
    /// are surfaced for diagnostics and never used for occupancy.
    /// </summary>
    internal static TelemetrySensorSample? SelectGpuMemoryActivity(
        TelemetryHardwareSample gpu) =>
        ValidSensors(gpu, TelemetrySensorKind.Load)
            .Where(IsGpuMemoryActivity)
            .OrderBy(sensor => sensor.Index)
            .FirstOrDefault();

    private static bool IsGpuMemoryActivity(TelemetrySensorSample sensor) =>
        sensor.Kind == TelemetrySensorKind.Load &&
        Matches(sensor.Name, NameHints.Memory) &&
        !Matches(sensor.Name, NameHints.Shared);

    private static bool IsVramUsedCandidate(TelemetrySensorSample sensor) =>
        IsDedicatedMemorySmallData(sensor) &&
        Matches(sensor.Name, NameHints.MemoryUsed);

    private static bool IsVramTotalCandidate(TelemetrySensorSample sensor) =>
        IsDedicatedMemorySmallData(sensor) &&
        Matches(sensor.Name, NameHints.MemoryTotal);

    /// <summary>
    /// Dedicated on-board memory reported as SmallData. Excludes shared system
    /// memory and the D3D counters, which use different accounting.
    /// </summary>
    private static bool IsDedicatedMemorySmallData(TelemetrySensorSample sensor) =>
        sensor.Kind == TelemetrySensorKind.SmallData &&
        !Matches(sensor.Name, NameHints.Shared) &&
        !Matches(sensor.Name, NameHints.D3D);

    // ----------------------------------------------------------------- CPU ---

    private const int CpuTemperatureRejectRank = 90;

    /// <summary>
    /// Explicit Ryzen-aware ranking. Lower is better. Everything scored at or
    /// above <see cref="CpuTemperatureRejectRank"/> is not a whole-CPU reading and
    /// is never used.
    /// </summary>
    private static int RankCpuTemperature(TelemetrySensorSample sensor)
    {
        var name = sensor.Name;
        var isTctlOrTdie = Contains(name, "Tctl") || Contains(name, "Tdie");

        // "Core (Tctl/Tdie)" - the AMD whole-die control temperature.
        if (isTctlOrTdie && Contains(name, "Core"))
        {
            return 0;
        }

        // "CPU Package" (Intel) and "CPU (Tctl/Tdie)" (some AMD boards).
        if (Contains(name, "Package") || (isTctlOrTdie && Contains(name, "CPU")))
        {
            return 1;
        }

        if (Contains(name, "Tdie"))
        {
            return 2;
        }

        if (Contains(name, "Tctl"))
        {
            return 3;
        }

        // A per-CCD reading is a partial view of the die. Accepted only when it is
        // the sole remaining candidate, and reported by name in the diagnostics.
        if (Contains(name, "CCD"))
        {
            return 5;
        }

        if (Matches(name, NameHints.Core))
        {
            return 4;
        }

        // Anything unrecognised on the CPU package is not assumed to be the CPU
        // temperature; motherboard and chipset probes must never stand in for it.
        return CpuTemperatureRejectRank;
    }

    internal static TelemetrySensorSample? SelectCpuTemperature(
        TelemetryHardwareSample cpu) =>
        cpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.Temperature)
            .Where(IsPlausibleCpuTemperature)
            .Where(sensor => RankCpuTemperature(sensor) < CpuTemperatureRejectRank)
            .OrderBy(RankCpuTemperature)
            .ThenBy(sensor => sensor.Index)
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// A CPU die never idles at 0 °C, so zero means "no reading" rather than a
    /// measurement. Values beyond the silicon's range are rejected, not clamped.
    /// </summary>
    private static bool IsPlausibleCpuTemperature(TelemetrySensorSample sensor) =>
        Read(sensor) is { } value &&
        value > 0 &&
        value <= MaxPlausibleCpuTemperatureC;

    private static TelemetrySensorSample? SelectCpuLoad(TelemetryHardwareSample cpu) =>
        Rank(cpu, TelemetrySensorKind.Load, sensor =>
            Matches(sensor.Name, NameHints.Total) ? 0 :
            sensor.Index == 0 ? 1 :
            Matches(sensor.Name, NameHints.Core) ? 3 : 2);

    /// <summary>
    /// PanelOS defines "CPU Clock" as the current average core frequency:
    /// the library's aggregate core-average sensor when it exists, otherwise the
    /// arithmetic mean of the per-core clocks that are reporting a real value.
    /// Bus, fabric and memory clocks are excluded by name, and cores parked at
    /// 0 MHz are left out of the mean rather than dragging it toward zero. When
    /// no core reports anything the result is null - never a base-clock stand-in.
    /// </summary>
    internal static CpuClockReading? SelectCpuClockReading(
        TelemetryHardwareSample? cpu)
    {
        if (cpu is null)
        {
            return null;
        }

        var candidates = cpu.Sensors
            .Where(sensor => sensor.Kind == TelemetrySensorKind.Clock)
            .Where(sensor => !Matches(sensor.Name, NameHints.NonCoreClock))
            .Where(IsPlausibleClock)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var aggregate = candidates
            .Where(sensor => Matches(sensor.Name, NameHints.Average))
            .OrderBy(sensor => Matches(sensor.Name, NameHints.Effective) ? 1 : 0)
            .ThenBy(sensor => sensor.Index)
            .FirstOrDefault();
        if (aggregate is not null)
        {
            return new CpuClockReading(
                aggregate.Value!.Value,
                $"aggregate sensor '{aggregate.Name}'",
                [aggregate]);
        }

        var cores = candidates
            .Where(sensor => Matches(sensor.Name, NameHints.Core))
            .ToArray();
        var requested = cores
            .Where(sensor => !Matches(sensor.Name, NameHints.Effective))
            .ToArray();
        var selected = requested.Length > 0
            ? requested
            : cores.Length > 0
                ? cores
                : candidates;

        return new CpuClockReading(
            selected.Average(sensor => sensor.Value!.Value),
            $"mean of {selected.Length} active core clock sensor(s)",
            selected);
    }

    private static bool IsPlausibleClock(TelemetrySensorSample sensor) =>
        Read(sensor) is { } value && value > 0 && value <= MaxPlausibleClockMhz;

    private static TelemetrySensorSample? SelectCpuPower(TelemetryHardwareSample cpu) =>
        RankPositive(cpu, TelemetrySensorKind.Power, sensor =>
            Matches(sensor.Name, NameHints.Package) ? 0 :
            Matches(sensor.Name, NameHints.Cpu) ? 1 : 2);

    // The selection lines and the availability warnings share one diagnosis, so
    // the startup log can never describe the same sensor two different ways.
    private static string DescribeCpuTemperatureSelection(
        TelemetryHardwareSample cpu,
        TelemetryEnvironment environment) =>
        !HasKnownCpuAccessFailure(environment) && SelectCpuTemperature(cpu) is { } selected
            ? $"[Telemetry] CPU Temperature: {selected.Name}"
            : DescribeDegraded(
                "CPU Temperature",
                DiagnoseCpuTemperature(cpu, environment));

    internal static CpuClockReading? ResolveCpuClock(
        TelemetryHardwareSample? cpu, TelemetryEnvironment? environment)
    {
        if (SelectCpuClockReading(cpu) is { } reading)
            return reading;
        return cpu is not null && environment?.WindowsCpuClockMhz is > 0 and <= MaxPlausibleClockMhz
            ? new CpuClockReading(environment.WindowsCpuClockMhz.Value,
                "WindowsProcessorPerformance estimate (nominal MHz × % Processor Performance / 100)",
                [], WindowsCpuClock.Source)
            : null;
    }

    private static string DescribeCpuClockSelection(
        TelemetryHardwareSample cpu,
        TelemetryEnvironment environment) =>
        ResolveCpuClock(cpu, environment) is { } reading
            ? $"[Telemetry] CPU Clock: {reading.Definition}"
            : DescribeDegraded("CPU Clock", DiagnoseCpuClock(cpu, environment));

    private static string DescribeDegraded(
        string metric,
        TelemetryMetricDiagnosis diagnosis) =>
        $"[Telemetry] {metric}: {diagnosis.Health} - {diagnosis.Detail}";

    private static string DescribeSensorNames(
        IReadOnlyList<TelemetrySensorSample> sensors) =>
        string.Join(
            ", ",
            sensors.Select(sensor => $"'{sensor.Name}'").Take(3));

    // ----------------------------------------------------------------- GPU ---

    private static TelemetrySensorSample? SelectGpuTemperature(
        TelemetryHardwareSample gpu) =>
        RankPositive(gpu, TelemetrySensorKind.Temperature, sensor =>
            Matches(sensor.Name, NameHints.Core) ? 0 :
            Matches(sensor.Name, NameHints.HotSpot) ? 1 : 2);

    private static TelemetrySensorSample? SelectGpuLoad(TelemetryHardwareSample gpu) =>
        Rank(gpu, TelemetrySensorKind.Load, sensor =>
            Matches(sensor.Name, NameHints.Core) ? 0 :
            sensor.Index == 0 ? 1 :
            Matches(sensor.Name, NameHints.Memory) ? 3 : 2);

    private static TelemetrySensorSample? SelectGpuClock(TelemetryHardwareSample gpu) =>
        RankPositive(gpu, TelemetrySensorKind.Clock, sensor =>
            Matches(sensor.Name, NameHints.Core) ? 0 :
            sensor.Index == 0 ? 1 :
            Matches(sensor.Name, NameHints.Memory) ? 3 : 2);

    private static TelemetrySensorSample? SelectGpuPower(TelemetryHardwareSample gpu) =>
        RankPositive(gpu, TelemetrySensorKind.Power, sensor =>
            Matches(sensor.Name, NameHints.PackageOrTotal) ? 0 :
            Matches(sensor.Name, NameHints.Core) ? 1 : 2);

    // -------------------------------------------------------------- shared ---

    private static TelemetrySensorSample? SelectMemoryLoad(
        TelemetryHardwareSample? memory) =>
        Rank(memory, TelemetrySensorKind.Load, sensor =>
            Matches(sensor.Name, NameHints.Memory) ? 0 :
            sensor.Index == 0 ? 1 : 2);

    private static TelemetrySensorSample? SelectStorageTemperature(
        TelemetryHardwareSample storage) =>
        RankPositive(storage, TelemetrySensorKind.Temperature, sensor =>
            Matches(sensor.Name, NameHints.Composite) ? 0 :
            sensor.Index == 0 ? 1 : 2);

    private static TelemetrySensorSample? SelectStorageLoad(
        TelemetryHardwareSample storage) =>
        Rank(storage, TelemetrySensorKind.Load, sensor =>
            sensor.Index == 30 ? 0 :
            Matches(sensor.Name, NameHints.UsedSpace) ? 1 : 2);

    private static TelemetryHardwareSample? SelectMemoryHardware(
        IReadOnlyList<TelemetryHardwareSample> hardware) =>
        hardware
            .Where(candidate => candidate.Kind == TelemetryHardwareKind.Memory)
            .Select(candidate => new
            {
                Hardware = candidate,
                HasLoad = ValidSensors(candidate, TelemetrySensorKind.Load).Any(),
                HasUsed = SelectByName(
                    candidate,
                    TelemetrySensorKind.Data,
                    NameHints.Used) is not null,
                HasAvailable = SelectByName(
                    candidate,
                    TelemetrySensorKind.Data,
                    NameHints.Available) is not null,
                IsVirtual = Matches(candidate.Name, NameHints.Virtual),
                IsMemoryModule = Matches(candidate.Name, NameHints.MemoryModule),
            })
            .OrderBy(candidate => candidate.IsVirtual)
            .ThenBy(candidate => candidate.IsMemoryModule)
            .ThenByDescending(candidate => candidate.HasLoad &&
                candidate.HasUsed &&
                candidate.HasAvailable)
            .ThenByDescending(candidate => candidate.HasUsed && candidate.HasAvailable)
            .ThenByDescending(candidate => candidate.HasLoad)
            .ThenBy(candidate => candidate.Hardware.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Hardware)
            .FirstOrDefault();

    private static TelemetrySensorSample? SelectByName(
        TelemetryHardwareSample? hardware,
        TelemetrySensorKind kind,
        IReadOnlyList<string> hints) =>
        Rank(hardware, kind, _ => 0, requireNameMatch: hints);

    private static TelemetrySensorSample? Rank(
        TelemetryHardwareSample? hardware,
        TelemetrySensorKind kind,
        Func<TelemetrySensorSample, int> rank,
        IReadOnlyList<string>? requireNameMatch = null) =>
        ValidSensors(hardware, kind)
            .Where(sensor => requireNameMatch is null ||
                Matches(sensor.Name, requireNameMatch))
            .OrderBy(rank)
            .ThenBy(sensor => sensor.Index)
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static TelemetrySensorSample? RankPositive(
        TelemetryHardwareSample? hardware,
        TelemetrySensorKind kind,
        Func<TelemetrySensorSample, int> rank) =>
        PositiveSensors(hardware, kind)
            .OrderBy(rank)
            .ThenBy(sensor => sensor.Index)
            .ThenBy(sensor => sensor.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static IReadOnlyList<TelemetrySensorSample> SelectSingle(
        TelemetrySensorSample? sensor) =>
        sensor is null
            ? Array.Empty<TelemetrySensorSample>()
            : [sensor];

    private static IEnumerable<TelemetrySensorSample> ValidSensors(
        TelemetryHardwareSample? hardware,
        TelemetrySensorKind kind) =>
        hardware?.Sensors.Where(sensor =>
            sensor.Kind == kind && Normalized(sensor.Value).HasValue)
        ?? Enumerable.Empty<TelemetrySensorSample>();

    private static IEnumerable<TelemetrySensorSample> PositiveSensors(
        TelemetryHardwareSample? hardware,
        TelemetrySensorKind kind) =>
        ValidSensors(hardware, kind).Where(sensor => sensor.Value > 0);

    private static double? Read(TelemetrySensorSample? sensor) =>
        Normalized(sensor?.Value);

    private static bool IsGpu(TelemetryHardwareKind kind) =>
        kind is TelemetryHardwareKind.GpuNvidia or
            TelemetryHardwareKind.GpuAmd or
            TelemetryHardwareKind.GpuIntel;

    private static int GetGpuTypePriority(TelemetryHardwareKind kind) => kind switch
    {
        TelemetryHardwareKind.GpuNvidia => 2,
        TelemetryHardwareKind.GpuAmd => 2,
        TelemetryHardwareKind.GpuIntel => 1,
        _ => 0,
    };

    private static bool Matches(string value, IReadOnlyList<string> hints) =>
        hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string value, string hint) =>
        value.Contains(hint, StringComparison.OrdinalIgnoreCase);

    private static double? Divide(double? value, double divisor) =>
        value.HasValue ? value.Value / divisor : null;

    private static double? Normalized(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? value : null;

    private static double? Round(double? value, int digits) =>
        Normalized(value) is { } normalized
            ? Math.Round(normalized, digits)
            : null;

    private static string DescribeSensor(
        string label,
        TelemetrySensorSample? sensor) =>
        sensor is null
            ? $"[Telemetry] {label}: unavailable"
            : $"[Telemetry] {label}: {sensor.Name}";

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static class NameHints
    {
        public static readonly string[] Average = ["Average", "Média"];
        public static readonly string[] Available = ["Available", "Livre", "Free"];
        public static readonly string[] Composite = ["Composite"];
        public static readonly string[] Core = ["Core", "Nucleus", "Núcleo"];
        public static readonly string[] Cpu = ["CPU", "Processor"];
        public static readonly string[] D3D = ["D3D"];
        public static readonly string[] D3DDedicated = ["D3D Dedicated"];
        public static readonly string[] Effective = ["Effective"];
        public static readonly string[] HotSpot = ["Hot Spot", "Hotspot"];
        public static readonly string[] Memory = ["Memory", "VRAM"];
        public static readonly string[] MemoryFree = ["Memory Free", "VRAM Free"];
        public static readonly string[] MemoryModule = ["DIMM", "Module", "Stick", "Bank"];
        public static readonly string[] MemoryTotal = ["Memory Total", "VRAM Total"];
        public static readonly string[] MemoryUsed = ["Memory Used", "VRAM Used"];
        public static readonly string[] NonCoreClock =
            ["Bus", "Memory", "Uncore", "Fabric", "FCLK", "UCLK", "MCLK", "Northbridge"];
        public static readonly string[] Package = ["Package", "PPT"];
        public static readonly string[] PackageOrTotal = ["Package", "Total", "Board", "PPT"];
        public static readonly string[] Shared = ["Shared"];
        public static readonly string[] Total = ["Total"];
        public static readonly string[] Used = ["Used", "Utilizada"];
        public static readonly string[] UsedSpace = ["Used Space", "Espaço usado"];
        public static readonly string[] Virtual = ["Virtual", "Swap", "Page"];
    }
}
