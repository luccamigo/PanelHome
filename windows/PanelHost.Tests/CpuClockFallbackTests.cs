using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;
using PanelOS.Telemetry;

namespace PanelHost.Tests;

[TestClass]
public sealed class CpuClockFallbackTests
{
    [TestMethod]
    public void Missing_clock_uses_windows_estimate_and_reports_its_source()
    {
        var cpu = Cpu(0);
        var environment = Environment(KernelDriverState.Blocked, 4125);
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu], environment: environment);
        Assert.AreEqual(4.125, snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.PowerW);
        Assert.AreEqual(25.0, snapshot.Cpu.UsagePercent);
        var diagnosis = TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, environment);
        Assert.AreEqual(TelemetryMetricHealth.Available, diagnosis.Health);
        Assert.AreEqual("WindowsProcessorPerformance", diagnosis.Source);
        StringAssert.Contains(string.Join("\n", TelemetrySnapshotMapper.DescribeSelections([cpu], environment)), "WindowsProcessorPerformance");
        StringAssert.Contains(string.Join("\n", TelemetrySnapshotMapper.DescribeSelections([cpu], environment)), "CPU Temperature: HardwareAccessFailure");
    }

    [TestMethod]
    public void Valid_library_clock_wins_over_windows()
    {
        var cpu = Cpu(3900);
        var environment = Environment(KernelDriverState.Loaded, 4125);
        Assert.AreEqual(3.9, TelemetrySnapshotMapper.CreateSnapshot([cpu], environment: environment).Cpu.ClockGhz);
        Assert.AreEqual("LibreHardwareMonitor", TelemetrySnapshotMapper.DiagnoseCpuClock(cpu, environment).Source);
    }

    [DataTestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    [DataRow(10001.0)]
    public void Invalid_windows_reading_is_never_published(double mhz)
    {
        var environment = Environment(KernelDriverState.Blocked, mhz);
        Assert.IsNull(TelemetrySnapshotMapper.CreateSnapshot([Cpu(0)], environment: environment).Cpu.ClockGhz);
        Assert.AreEqual(TelemetryMetricHealth.HardwareAccessFailure,
            TelemetrySnapshotMapper.DiagnoseCpuClock(Cpu(0), environment).Health);
    }

    [DataTestMethod]
    [DataRow(KernelDriverState.Blocked)]
    [DataRow(KernelDriverState.AccessDenied)]
    [DataRow(KernelDriverState.NotInstalled)]
    [DataRow(KernelDriverState.InstalledNotLoaded)]
    [DataRow(KernelDriverState.Unknown)]
    public void Voltage_requires_proven_driver_access(KernelDriverState state)
    {
        var cpu = Cpu(0);
        var diagnosis = TelemetrySnapshotMapper.DiagnoseCpuVoltage(cpu, Environment(state));
        Assert.AreNotEqual(TelemetryMetricHealth.Available, diagnosis.Health);
        StringAssert.Contains(diagnosis.Detail, "unavailable");
    }

    [DataTestMethod]
    [DataRow(KernelDriverState.Blocked)]
    [DataRow(KernelDriverState.AccessDenied)]
    [DataRow(KernelDriverState.NotInstalled)]
    [DataRow(KernelDriverState.InstalledNotLoaded)]
    public void Proven_inaccessible_driver_suppresses_temperature_and_power(KernelDriverState state)
    {
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([Cpu(0)], environment: Environment(state));
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.PowerW);
    }

    [TestMethod]
    public void Unknown_driver_trusts_valid_library_temperature_and_power()
    {
        var cpu = Cpu(3900);
        var environment = Environment(KernelDriverState.Unknown);
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu], environment: environment);
        Assert.AreEqual(65.0, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(50.0, snapshot.Cpu.PowerW);
        Assert.AreEqual(TelemetryMetricHealth.Available,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, environment).Health);
        StringAssert.Contains(string.Join("\n", TelemetrySnapshotMapper.DescribeSelections([cpu], environment)),
            "CPU Temperature: Core (Tctl/Tdie)");
        Assert.AreNotEqual(TelemetryMetricHealth.Available,
            TelemetrySnapshotMapper.DiagnoseCpuVoltage(cpu, environment).Health);
    }

    [DataTestMethod]
    [DataRow(0.0)]
    [DataRow(-1.0)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void Unknown_driver_does_not_validate_invalid_library_readings(double value)
    {
        var cpu = Cpu(0) with { Sensors =
        [
            new("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, value),
            new("CPU Package", TelemetrySensorKind.Power, 0, value),
        ] };
        var environment = Environment(KernelDriverState.Unknown);
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu], environment: environment);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.PowerW);
        Assert.AreEqual(TelemetryMetricHealth.SensorPresentNoValue,
            TelemetrySnapshotMapper.DiagnoseCpuTemperature(cpu, environment).Health);
    }

    [TestMethod]
    public void Working_driver_retains_valid_voltage_and_kernel_metrics()
    {
        var cpu = Cpu(3900);
        var environment = Environment(KernelDriverState.Loaded);
        Assert.AreEqual(TelemetryMetricHealth.Available, TelemetrySnapshotMapper.DiagnoseCpuVoltage(cpu, environment).Health);
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu], environment: environment);
        Assert.AreEqual(65.0, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(50.0, snapshot.Cpu.PowerW);
    }

    [TestMethod]
    public void Estimate_preserves_boost_and_never_substitutes_base_frequency()
    {
        Assert.AreEqual(4290.0, WindowsCpuClock.EstimateMhz(3900, 110));
        Assert.AreEqual(1950.0, WindowsCpuClock.EstimateMhz(3900, 50));
        Assert.IsNull(WindowsCpuClock.EstimateMhz(3900, null));
        Assert.IsNull(WindowsCpuClock.EstimateMhz(null, 110));
        Assert.IsNull(WindowsCpuClock.EstimateMhz(3900, 0));
        Assert.IsNull(WindowsCpuClock.EstimateMhz(3900, double.NaN));
        Assert.IsNull(WindowsCpuClock.EstimateMhz(3900, 300));
    }

    [TestMethod]
    public void No_cpu_does_not_publish_a_machine_estimate_as_an_identified_cpu()
    {
        var environment = Environment(KernelDriverState.Blocked, 4125);
        Assert.IsNull(TelemetrySnapshotMapper.CreateSnapshot([], environment: environment).Cpu.ClockGhz);
        Assert.IsNull(TelemetrySnapshotMapper.DiagnoseCpuClock(null, environment).Source);
    }

    [TestMethod]
    public void Fallback_does_not_keep_last_value_when_windows_stops_reporting()
    {
        var environment = Environment(KernelDriverState.Blocked, 4125);
        Assert.AreEqual(4.125, TelemetrySnapshotMapper.CreateSnapshot([Cpu(0)], environment: environment).Cpu.ClockGhz);
        Assert.IsNull(TelemetrySnapshotMapper.CreateSnapshot([Cpu(0)], environment: environment with { WindowsCpuClockMhz = null }).Cpu.ClockGhz);
    }

    [TestMethod]
    public void Pdh_open_failure_returns_null_with_reason_and_retries()
    {
        var attempts = 0;
        using var clock = new WindowsCpuClock(() => { attempts++; return 0xC0000BBD; });
        Assert.IsNull(clock.ReadMhz());
        Assert.AreEqual("Windows PDH status 0xC0000BBD", clock.UnavailableReason);
        Assert.IsNull(clock.ReadMhz());
        Assert.AreEqual("Windows PDH status 0xC0000BBD", clock.UnavailableReason);
        Assert.AreEqual(2, attempts);
    }

    private static TelemetryEnvironment Environment(KernelDriverState state, double? mhz = null) =>
        new(KernelDriver: new KernelDriverStatus(state, "test"), WindowsCpuClockMhz: mhz);

    private static TelemetryHardwareSample Cpu(double clock) => new(TelemetryHardwareKind.Cpu, "Test CPU",
    [
        new("Core #1", TelemetrySensorKind.Clock, 0, clock),
        new("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 65),
        new("CPU Package", TelemetrySensorKind.Power, 0, 50),
        new("CPU Total", TelemetrySensorKind.Load, 0, 25),
        new("Core #1", TelemetrySensorKind.Voltage, 0, 1.55),
    ]);
}
