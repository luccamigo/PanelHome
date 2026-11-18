using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;
using PanelOS.Telemetry;

namespace PanelHost.Tests;

/// <summary>
/// Covers the distinction the old "unavailable" wording erased: a sensor that
/// does not exist, a sensor that exists but reads nothing, and a sensor PanelHost
/// cannot reach because the kernel driver is unusable are three different facts
/// with three different fixes.
/// </summary>
[TestClass]
public sealed class TelemetryCpuHealthTests
{
    [TestMethod]
    public void TelemetryDiagnosticsTypesComeFromSharedAssembly()
    {
        Assert.AreEqual(
            "PanelOS.Telemetry",
            typeof(TelemetryDiagnostics).Assembly.GetName().Name);
        Assert.AreSame(
            typeof(TelemetryDiagnostics).Assembly,
            typeof(KernelDriverStatus).Assembly);
    }

    [TestMethod]
    public void Reporting_sensors_are_available()
    {
        var cpu = Cpu(
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 54.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 3_900.0));

        var temperature = TelemetrySnapshotMapper.DiagnoseCpuTemperature(
            cpu,
            WorkingDriver);
        var clock = TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, WorkingDriver);

        Assert.AreEqual(TelemetryMetricHealth.Available, temperature.Health);
        Assert.AreEqual(TelemetryMetricHealth.Available, clock.Health);
        StringAssert.Contains(temperature.Detail, "Core (Tctl/Tdie)");
        StringAssert.Contains(clock.Detail, "Cores (Average)");
        Assert.AreEqual(
            0,
            TelemetrySnapshotMapper
                .DescribeCpuAvailabilityWarnings([cpu], WorkingDriver)
                .Count,
            "Healthy sensors must not produce warnings.");
    }

    [TestMethod]
    public void Absent_sensors_with_a_working_driver_are_unsupported_hardware()
    {
        var cpu = Cpu(Sensor("CPU Total", TelemetrySensorKind.Load, 0, 12.0));

        Assert.AreEqual(
            TelemetryMetricHealth.UnsupportedHardware,
            TelemetrySnapshotMapper
                .DiagnoseCpuTemperature(cpu, WorkingDriver)
                .Health);
        Assert.AreEqual(
            TelemetryMetricHealth.UnsupportedHardware,
            TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, WorkingDriver).Health);
    }

    [TestMethod]
    public void Absent_sensors_without_a_driver_probe_are_only_reported_absent()
    {
        var cpu = Cpu(Sensor("CPU Total", TelemetrySensorKind.Load, 0, 12.0));
        var unprobed = new TelemetryEnvironment(
            "0.9.6.0",
            IsElevated: true,
            HasKernelDriver: true);

        Assert.AreEqual(
            TelemetryMetricHealth.SensorNotPresent,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, unprobed).Health,
            "Without a probed driver, absence is an observation, not a verdict " +
            "about the hardware.");
    }

    [TestMethod]
    public void Present_sensors_reading_nothing_with_a_working_driver_have_no_value()
    {
        var cpu = Cpu(
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0));

        var temperature = TelemetrySnapshotMapper.DiagnoseCpuTemperature(
            cpu,
            WorkingDriver);
        var clock = TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, WorkingDriver);

        Assert.AreEqual(
            TelemetryMetricHealth.SensorPresentNoValue,
            temperature.Health);
        Assert.AreEqual(TelemetryMetricHealth.SensorPresentNoValue, clock.Health);
        StringAssert.Contains(temperature.Detail, "no valid value");
    }

    /// <summary>
    /// The regression this work started from: an elevated host, a PawnIO package
    /// on disk, and Windows refusing to load it. The old diagnostics concluded
    /// the CPU was unsupported; the blocker is hardware access.
    /// </summary>
    [TestMethod]
    public void Blocked_driver_is_a_hardware_access_failure_not_unsupported_hardware()
    {
        var cpu = Cpu(
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0),
            Sensor("Core #1", TelemetrySensorKind.Clock, 3, null),
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 4.4));
        var blocked = Environment(new KernelDriverStatus(
            KernelDriverState.Blocked,
            "PawnIO is installed but Windows refuses to start its device " +
            "'ROOT\\PAWNIO\\0000': its digital signature could not be verified, " +
            "so Windows code integrity refused to load it (code 52).",
            ProblemCode: 52,
            DeviceInstanceId: "ROOT\\PAWNIO\\0000"));

        var warnings = TelemetrySnapshotMapper.DescribeCpuAvailabilityWarnings(
            [cpu],
            blocked);

        Assert.AreEqual(
            TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, blocked).Health);
        Assert.AreEqual(
            TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, blocked).Health);
        Assert.IsFalse(
            warnings.Any(warning => warning.Contains(
                "unsupported",
                StringComparison.OrdinalIgnoreCase)),
            "A blocked driver must never be reported as unsupported hardware.");
        Assert.IsFalse(
            warnings.Any(warning => warning.Contains("Administrator")),
            "An elevated host must not be advised to elevate.");
        Assert.IsTrue(warnings.Any(warning => warning.Contains("code 52")));
        Assert.IsTrue(warnings.Any(warning => warning.Contains("PanelHost elevated: yes")));
    }

    [TestMethod]
    public void Access_denied_driver_is_reported_as_a_permission_problem()
    {
        var cpu = Cpu(
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0));
        var denied = Environment(
            new KernelDriverStatus(
                KernelDriverState.AccessDenied,
                "PawnIO is loaded but this process may not open its device " +
                "object; elevated access is required."),
            elevated: false);

        var warnings = TelemetrySnapshotMapper.DescribeCpuAvailabilityWarnings(
            [cpu],
            denied);

        Assert.AreEqual(
            TelemetryMetricHealth.PermissionDenied,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, denied).Health);
        Assert.IsTrue(warnings.Any(warning => warning.Contains("Administrator")));
    }

    [TestMethod]
    public void Missing_driver_is_reported_instead_of_blaming_privileges()
    {
        var cpu = Cpu(
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0));
        var missing = Environment(new KernelDriverStatus(
            KernelDriverState.NotInstalled,
            "PawnIO is not installed; LibreHardwareMonitor cannot reach AMD " +
            "SMU/MSR registers without it."));

        var warnings = TelemetrySnapshotMapper.DescribeCpuAvailabilityWarnings(
            [cpu],
            missing);

        Assert.AreEqual(
            TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, missing).Health);
        Assert.IsTrue(warnings.Any(warning => warning.Contains("PawnIO")));
        Assert.IsFalse(
            warnings.Any(warning => warning.Contains("Administrator")),
            "A missing driver must not be reported as a privilege problem.");
    }

    [TestMethod]
    public void No_cpu_hardware_is_a_hardware_access_failure()
    {
        Assert.AreEqual(
            TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper
                .DiagnoseCpuTemperature(null, WorkingDriver)
                .Health);
        Assert.AreEqual(
            TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper.DiagnoseCpuClock(null, WorkingDriver).Health);
    }

    [TestMethod]
    public void Clock_diagnosis_counts_only_core_clock_candidates()
    {
        var cpu = Cpu(
            Sensor("Bus Speed", TelemetrySensorKind.Clock, 0, 100.0),
            Sensor("Fabric Clock", TelemetrySensorKind.Clock, 1, 1_600.0),
            Sensor("Core #1", TelemetrySensorKind.Clock, 2, 0.0),
            Sensor("Core #2", TelemetrySensorKind.Clock, 3, null));

        var clock = TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, WorkingDriver);

        Assert.AreEqual(TelemetryMetricHealth.SensorPresentNoValue, clock.Health);
        StringAssert.Contains(
            clock.Detail,
            "2 core clock sensor(s) present",
            "Bus and fabric clocks are not core clock candidates.");
    }

    [TestMethod]
    public void Some_reporting_core_clocks_keep_the_metric_available()
    {
        var cpu = Cpu(
            Sensor("Core #1", TelemetrySensorKind.Clock, 0, 0.0),
            Sensor("Core #2", TelemetrySensorKind.Clock, 1, null),
            Sensor("Core #3", TelemetrySensorKind.Clock, 2, 4_400.0));

        Assert.AreEqual(
            TelemetryMetricHealth.Available,
            TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, WorkingDriver).Health);
    }

    /// <summary>
    /// A degraded CPU metric must never take the rest of the snapshot with it:
    /// load, GPU, VRAM, memory and storage keep flowing while temperature and
    /// clock are missing.
    /// </summary>
    [TestMethod]
    public void Degraded_cpu_sensors_do_not_break_the_rest_of_the_snapshot()
    {
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(
            [
                Cpu(
                    Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0),
                    Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0),
                    Sensor("CPU Total", TelemetrySensorKind.Load, 0, 4.4)),
                new TelemetryHardwareSample(
                    TelemetryHardwareKind.GpuNvidia,
                    "NVIDIA GeForce RTX 5060",
                    [
                        Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 31.4),
                        Sensor("GPU Core", TelemetrySensorKind.Load, 0, 7.0),
                        Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_228.0),
                        Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_151.0),
                    ]),
                new TelemetryHardwareSample(
                    TelemetryHardwareKind.Storage,
                    "Samsung SSD",
                    [Sensor("Temperature", TelemetrySensorKind.Temperature, 0, 38.0)]),
            ],
            new MemoryCapacitySnapshot(12.0, 32.0));

        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.AreEqual(4.4, snapshot.Cpu.UsagePercent);
        Assert.AreEqual(31.4, snapshot.Gpu.TemperatureC);
        Assert.AreEqual(7.96, snapshot.Gpu.MemoryTotalGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
        Assert.AreEqual(1, snapshot.Storage.Count);
        Assert.AreEqual(38.0, snapshot.Storage[0].TemperatureC);
    }

    [TestMethod]
    public void Kernel_driver_description_prefers_the_probe_over_the_presence_flag()
    {
        Assert.AreEqual(
            "PawnIO driver is loaded and its device object was opened.",
            TelemetrySnapshotMapper.DescribeKernelDriver(WorkingDriver));
        StringAssert.Contains(
            TelemetrySnapshotMapper.DescribeKernelDriver(
                new TelemetryEnvironment(
                    "0.9.6.0",
                    IsElevated: true,
                    HasKernelDriver: true)),
            "functional state not probed");
        StringAssert.Contains(
            TelemetrySnapshotMapper.DescribeKernelDriver(new TelemetryEnvironment()),
            "not installed");
    }

    private static TelemetryEnvironment WorkingDriver => Environment(
        new KernelDriverStatus(
            KernelDriverState.Loaded,
            "PawnIO driver is loaded and its device object was opened."));

    private static TelemetryEnvironment Environment(
        KernelDriverStatus driver,
        bool elevated = true) =>
        new(
            "0.9.6.0",
            elevated,
            driver.IsPresent,
            driver,
            "0.9.6+3d331e3370efb858411f19511373eff65a218701");

    private static TelemetryHardwareSample Cpu(params TelemetrySensorSample[] sensors) =>
        new(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G with Radeon Graphics",
            sensors);

    private static TelemetrySensorSample Sensor(
        string name,
        TelemetrySensorKind kind,
        int index,
        double? value) =>
        new(name, kind, index, value);
}
