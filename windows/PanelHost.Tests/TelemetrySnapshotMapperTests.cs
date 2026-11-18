using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class TelemetrySnapshotMapperTests
{
    [TestMethod]
    public void Snapshot_maps_cpu_gpu_memory_and_storage_sensors()
    {
        var hardware = new TelemetryHardwareSample[]
        {
            Hardware(
                TelemetryHardwareKind.Cpu,
                "AMD Ryzen 7",
                Sensor("Core #1", TelemetrySensorKind.Temperature, 1, 58.0),
                Sensor("CPU Package", TelemetrySensorKind.Temperature, 0, 52.0),
                Sensor("CPU Core #1", TelemetrySensorKind.Load, 1, 34.0),
                Sensor("CPU Total", TelemetrySensorKind.Load, 0, 18.0),
                Sensor("CPU Core #1", TelemetrySensorKind.Clock, 1, 4_200.0),
                Sensor("CPU Core #2", TelemetrySensorKind.Clock, 2, 4_400.0),
                Sensor("Bus Speed", TelemetrySensorKind.Clock, 0, 100.0),
                Sensor("CPU Package", TelemetrySensorKind.Power, 0, 65.2)),
            Hardware(
                TelemetryHardwareKind.GpuNvidia,
                "NVIDIA GeForce RTX",
                Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 47.0),
                Sensor("GPU Hot Spot", TelemetrySensorKind.Temperature, 1, 63.0),
                Sensor("GPU Core", TelemetrySensorKind.Load, 0, 31.0),
                // Deliberately not 50: proves the derived percentage below comes
                // from used/total and not from this activity sensor.
                Sensor("GPU Memory", TelemetrySensorKind.Load, 1, 91.0),
                Sensor("GPU Core", TelemetrySensorKind.Clock, 0, 2_600.0),
                Sensor("GPU Memory", TelemetrySensorKind.Clock, 1, 10_000.0),
                Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 0, 4_096.0),
                Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 1, 8_192.0),
                Sensor("GPU Board", TelemetrySensorKind.Power, 0, 121.4)),
            Hardware(
                TelemetryHardwareKind.Memory,
                "Generic Memory",
                Sensor("Memory Used", TelemetrySensorKind.Data, 0, 11.4),
                Sensor("Memory Available", TelemetrySensorKind.Data, 1, 20.6),
                Sensor("Memory", TelemetrySensorKind.Load, 0, 35.6)),
            Hardware(
                TelemetryHardwareKind.Storage,
                "NVMe SSD",
                Sensor("Temperature", TelemetrySensorKind.Temperature, 1, 51.0),
                Sensor("Composite", TelemetrySensorKind.Temperature, 0, 44.0),
                Sensor("Used Space", TelemetrySensorKind.Load, 30, 62.0)),
        };

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(hardware);

        Assert.AreEqual("AMD Ryzen 7", snapshot.Cpu.Name);
        Assert.AreEqual(52.0, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(18.0, snapshot.Cpu.UsagePercent);
        Assert.AreEqual(4.3, snapshot.Cpu.ClockGhz);
        Assert.AreEqual(65.2, snapshot.Cpu.PowerW);

        Assert.AreEqual("NVIDIA GeForce RTX", snapshot.Gpu.Name);
        Assert.AreEqual(47.0, snapshot.Gpu.TemperatureC);
        Assert.AreEqual(31.0, snapshot.Gpu.UsagePercent);
        Assert.AreEqual(2.6, snapshot.Gpu.ClockGhz);
        Assert.AreEqual(50.0, snapshot.Gpu.MemoryUsagePercent);
        Assert.AreEqual(4.0, snapshot.Gpu.MemoryUsedGb);
        Assert.AreEqual(8.0, snapshot.Gpu.MemoryTotalGb);
        Assert.AreEqual(121.4, snapshot.Gpu.PowerW);

        Assert.AreEqual(11.4, snapshot.Memory.UsedGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
        Assert.AreEqual(35.6, snapshot.Memory.UsagePercent);
        Assert.AreEqual(1, snapshot.Storage.Count);
        Assert.AreEqual("NVMe SSD", snapshot.Storage[0].Name);
        Assert.AreEqual(44.0, snapshot.Storage[0].TemperatureC);
        Assert.AreEqual(62.0, snapshot.Storage[0].UsagePercent);
    }

    [TestMethod]
    public void Temperature_and_clock_remain_null_when_sensors_are_missing()
    {
        var hardware = new TelemetryHardwareSample[]
        {
            Hardware(
                TelemetryHardwareKind.Cpu,
                "CPU",
                Sensor("CPU Total", TelemetrySensorKind.Load, 0, 22.0)),
            Hardware(
                TelemetryHardwareKind.GpuIntel,
                "Integrated GPU",
                Sensor("GPU Core", TelemetrySensorKind.Load, 0, 13.0)),
        };

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(hardware);

        Assert.AreEqual(22.0, snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Cpu.PowerW);
        Assert.AreEqual(13.0, snapshot.Gpu.UsagePercent);
        Assert.IsNull(snapshot.Gpu.TemperatureC);
        Assert.IsNull(snapshot.Gpu.ClockGhz);
    }

    [TestMethod]
    public void Cpu_without_temperature_preserves_load_and_clock()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen CPU",
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 22.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 4_250.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu]);

        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.AreEqual(22.0, snapshot.Cpu.UsagePercent);
        Assert.AreEqual(4.25, snapshot.Cpu.ClockGhz);
    }

    [TestMethod]
    public void Cpu_without_clock_preserves_load_and_temperature()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen CPU",
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 57.0),
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 31.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu]);

        Assert.AreEqual(57.0, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(31.0, snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
    }

    [TestMethod]
    public void Multiple_sensors_use_aggregate_readings_instead_of_individual_cores()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("Core #1", TelemetrySensorKind.Temperature, 1, 71.0),
            Sensor("CPU Package", TelemetrySensorKind.Temperature, 2, 54.0),
            Sensor("CPU Core #1", TelemetrySensorKind.Load, 1, 83.0),
            Sensor("CPU Total", TelemetrySensorKind.Load, 5, 27.0),
            Sensor("CPU Core #1", TelemetrySensorKind.Clock, 1, 4_000.0),
            Sensor("CPU Core #2", TelemetrySensorKind.Clock, 2, 5_000.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu]);

        Assert.AreEqual(54.0, snapshot.Cpu.TemperatureC);
        Assert.AreEqual(27.0, snapshot.Cpu.UsagePercent);
        Assert.AreEqual(4.5, snapshot.Cpu.ClockGhz);
    }

    [TestMethod]
    public void Cpu_average_clock_is_not_mixed_with_individual_core_clocks()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("CPU Core Average", TelemetrySensorKind.Clock, 0, 4_500.0),
            Sensor("CPU Core #1", TelemetrySensorKind.Clock, 1, 4_000.0),
            Sensor("CPU Core #2", TelemetrySensorKind.Clock, 2, 4_200.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu]);

        Assert.AreEqual(4.5, snapshot.Cpu.ClockGhz);
    }

    [TestMethod]
    public void Cpu_metric_selections_include_hardware_and_sensor_identifiers()
    {
        var cpu = new TelemetryHardwareSample(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen CPU",
            [
                Sensor(
                    "Core (Tctl/Tdie)",
                    TelemetrySensorKind.Temperature,
                    0,
                    57.5,
                    "/amdcpu/0/temperature/0"),
                Sensor(
                    "Core #1",
                    TelemetrySensorKind.Clock,
                    1,
                    4_000.0,
                    "/amdcpu/0/clock/1"),
                Sensor(
                    "Core #2",
                    TelemetrySensorKind.Clock,
                    2,
                    4_200.0,
                    "/amdcpu/0/clock/2"),
            ],
            "/amdcpu/0");

        var selections = TelemetrySnapshotMapper.SelectMetrics([cpu]);
        var temperature = selections.Single(selection =>
            selection.Metric == "CPU Temperature");
        var clocks = selections.Single(selection =>
            selection.Metric == "CPU Clock");

        Assert.AreSame(cpu, temperature.Hardware);
        Assert.AreEqual("/amdcpu/0", temperature.Hardware?.Identifier);
        Assert.AreEqual(
            "/amdcpu/0/temperature/0",
            temperature.Sensors.Single().Identifier);
        CollectionAssert.AreEqual(
            new[] { "/amdcpu/0/clock/1", "/amdcpu/0/clock/2" },
            clocks.Sensors.Select(sensor => sensor.Identifier).ToArray());

        using var writer = new StringWriter();
        LibreHardwareTelemetryService.WriteSelection(writer, temperature);

        Assert.AreEqual(
            "CPU Temperature | Cpu | AMD Ryzen CPU | /amdcpu/0 | " +
            "Temperature | Core (Tctl/Tdie) | 57.5 | " +
            "/amdcpu/0/temperature/0" + Environment.NewLine,
            writer.ToString());
    }

    [TestMethod]
    public void Snapshot_and_diagnostics_use_the_same_primary_cpu()
    {
        var zeta = Hardware(
            TelemetryHardwareKind.Cpu,
            "Zeta CPU",
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 91.0));
        var alpha = Hardware(
            TelemetryHardwareKind.Cpu,
            "Alpha CPU",
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 17.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([zeta, alpha]);
        var selected = TelemetrySnapshotMapper.SelectPrimaryCpu([zeta, alpha]);
        var loadSelection = TelemetrySnapshotMapper
            .SelectMetrics([zeta, alpha])
            .Single(selection => selection.Metric == "CPU Load");

        Assert.AreSame(alpha, selected);
        Assert.AreEqual("Alpha CPU", snapshot.Cpu.Name);
        Assert.AreEqual(17.0, snapshot.Cpu.UsagePercent);
        Assert.AreSame(alpha, loadSelection.Hardware);
    }

    [TestMethod]
    public void Primary_gpu_prefers_largest_reported_dedicated_memory()
    {
        var nvidia = GpuWithMemory(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA 4 GB",
            totalMemoryMb: 4_096.0);
        var amd = GpuWithMemory(
            TelemetryHardwareKind.GpuAmd,
            "AMD 8 GB",
            totalMemoryMb: 8_192.0);
        var intel = GpuWithMemory(
            TelemetryHardwareKind.GpuIntel,
            "Intel 16 GB shared",
            totalMemoryMb: null);

        var selected = TelemetrySnapshotMapper.SelectPrimaryGpu([nvidia, amd, intel]);

        Assert.AreSame(amd, selected);
        Assert.AreEqual("AMD 8 GB", TelemetrySnapshotMapper
            .CreateSnapshot([nvidia, amd, intel])
            .Gpu.Name);
    }

    [TestMethod]
    public void Primary_gpu_prefers_discrete_gpu_then_stable_name_without_memory_data()
    {
        var intel = GpuWithMemory(
            TelemetryHardwareKind.GpuIntel,
            "Intel Graphics",
            totalMemoryMb: null);
        var zetaNvidia = GpuWithMemory(
            TelemetryHardwareKind.GpuNvidia,
            "Zeta NVIDIA",
            totalMemoryMb: null);
        var alphaAmd = GpuWithMemory(
            TelemetryHardwareKind.GpuAmd,
            "Alpha AMD",
            totalMemoryMb: null);

        var selected = TelemetrySnapshotMapper.SelectPrimaryGpu(
            [intel, zetaNvidia, alphaAmd]);

        Assert.AreSame(alphaAmd, selected);
    }

    [TestMethod]
    public void Integrated_shared_memory_does_not_outrank_dedicated_gpu_vram()
    {
        var integrated = Hardware(
            TelemetryHardwareKind.GpuIntel,
            "Intel Graphics",
            Sensor(
                "D3D Shared Memory Total",
                TelemetrySensorKind.SmallData,
                0,
                16_384.0));
        var dedicated = GpuWithMemory(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA 4 GB",
            totalMemoryMb: 4_096.0);

        var selected = TelemetrySnapshotMapper.SelectPrimaryGpu(
            [integrated, dedicated]);

        Assert.AreSame(dedicated, selected);
    }

    [TestMethod]
    public void Memory_fallback_supplies_capacity_and_calculated_usage()
    {
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(
            Array.Empty<TelemetryHardwareSample>(),
            new MemoryCapacitySnapshot(12.0, 32.0));

        Assert.AreEqual(12.0, snapshot.Memory.UsedGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
        Assert.AreEqual(37.5, snapshot.Memory.UsagePercent);
    }

    [TestMethod]
    public void Physical_total_memory_sensors_outrank_virtual_and_dimm_readings()
    {
        var virtualMemory = Hardware(
            TelemetryHardwareKind.Memory,
            "A Virtual Memory",
            Sensor("Memory Used", TelemetrySensorKind.Data, 0, 80.0),
            Sensor("Memory Available", TelemetrySensorKind.Data, 1, 120.0),
            Sensor("Memory", TelemetrySensorKind.Load, 0, 40.0));
        var dimm = Hardware(
            TelemetryHardwareKind.Memory,
            "B DIMM #1",
            Sensor("Memory Used", TelemetrySensorKind.Data, 0, 6.0),
            Sensor("Memory Available", TelemetrySensorKind.Data, 1, 10.0),
            Sensor("Memory", TelemetrySensorKind.Load, 0, 37.5));
        var physicalMemory = Hardware(
            TelemetryHardwareKind.Memory,
            "Z Physical Memory",
            Sensor("Memory Used", TelemetrySensorKind.Data, 0, 12.0),
            Sensor("Memory Available", TelemetrySensorKind.Data, 1, 20.0),
            Sensor("Memory", TelemetrySensorKind.Load, 0, 37.5));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(
            [virtualMemory, dimm, physicalMemory]);

        Assert.AreEqual(12.0, snapshot.Memory.UsedGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
        Assert.AreEqual(37.5, snapshot.Memory.UsagePercent);
    }

    [TestMethod]
    public void No_hardware_produces_safe_null_fields_and_empty_storage()
    {
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(
            Array.Empty<TelemetryHardwareSample>());

        Assert.IsNull(snapshot.Cpu.Name);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Cpu.PowerW);
        Assert.IsNull(snapshot.Gpu.Name);
        Assert.IsNull(snapshot.Gpu.TemperatureC);
        Assert.IsNull(snapshot.Gpu.UsagePercent);
        Assert.IsNull(snapshot.Gpu.MemoryTotalGb);
        Assert.IsNull(snapshot.Memory.UsedGb);
        Assert.IsNull(snapshot.Memory.TotalGb);
        Assert.IsNull(snapshot.Memory.UsagePercent);
        Assert.AreEqual(0, snapshot.Storage.Count);
    }

    [TestMethod]
    public void Diagnostics_report_library_version_elevation_and_vram_provenance()
    {
        var hardware = new TelemetryHardwareSample[]
        {
            Hardware(
                TelemetryHardwareKind.Cpu,
                "AMD Ryzen 5 5600G with Radeon Graphics",
                Sensor("CPU Total", TelemetrySensorKind.Load, 0, 4.4),
                Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 0.0),
                Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0)),
            Hardware(
                TelemetryHardwareKind.GpuNvidia,
                "NVIDIA GeForce RTX 5060",
                Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 31.4),
                Sensor("GPU Core", TelemetrySensorKind.Load, 0, 7.0),
                Sensor("GPU Memory", TelemetrySensorKind.Load, 3, 15.07),
                Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_228.0),
                Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_151.0)),
        };

        var descriptions = TelemetrySnapshotMapper.DescribeSelections(
            hardware,
            new TelemetryEnvironment("0.9.6.0", IsElevated: false, HasKernelDriver: false));

        CollectionAssert.Contains(
            descriptions.ToArray(),
            "[Telemetry] LibreHardwareMonitorLib version: 0.9.6.0");
        CollectionAssert.Contains(descriptions.ToArray(), "[Telemetry] Elevated: no");
        CollectionAssert.Contains(
            descriptions.ToArray(),
            "[Telemetry] VRAM Used: GPU Memory Used");
        CollectionAssert.Contains(
            descriptions.ToArray(),
            "[Telemetry] VRAM Total: GPU Memory Total");
        CollectionAssert.Contains(
            descriptions.ToArray(),
            "[Telemetry] VRAM Percent: derived (used / total)");

        // A present-but-dead sensor must not be described the same way as an
        // absent one; the two point at different problems. With no usable kernel
        // driver the honest verdict is that the hardware could not be reached.
        Assert.IsTrue(descriptions.Any(description =>
            description.StartsWith(
                "[Telemetry] CPU Temperature: HardwareAccessFailure",
                StringComparison.Ordinal) &&
            description.Contains("Core (Tctl/Tdie)") &&
            description.Contains("present but reporting no valid value")));
        Assert.IsTrue(descriptions.Any(description =>
            description.StartsWith(
                "[Telemetry] CPU Clock: HardwareAccessFailure",
                StringComparison.Ordinal) &&
            description.Contains("none reporting a valid value")));
        Assert.IsFalse(
            descriptions.Any(description => description.Contains(
                "unsupported",
                StringComparison.OrdinalIgnoreCase)),
            "Nothing may be called unsupported while hardware access is broken.");
    }

    [TestMethod]
    public void Non_finite_values_are_exposed_as_missing_instead_of_invalid_json_numbers()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("CPU Package", TelemetrySensorKind.Temperature, 0, double.NaN),
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, double.PositiveInfinity),
            Sensor("CPU Core #1", TelemetrySensorKind.Clock, 0, double.NegativeInfinity));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu]);

        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
    }

    [TestMethod]
    public void Zero_temperature_clock_and_power_are_missing_but_zero_load_is_real()
    {
        var hardware = new TelemetryHardwareSample[]
        {
            Hardware(
                TelemetryHardwareKind.Cpu,
                "AMD CPU",
                Sensor("CPU Package", TelemetrySensorKind.Temperature, 0, 0.0),
                Sensor("CPU Total", TelemetrySensorKind.Load, 0, 0.0),
                Sensor("CPU Core #1", TelemetrySensorKind.Clock, 0, 0.0),
                Sensor("CPU Package", TelemetrySensorKind.Power, 0, 0.0)),
            Hardware(
                TelemetryHardwareKind.GpuAmd,
                "AMD GPU",
                Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 0.0),
                Sensor("GPU Core", TelemetrySensorKind.Load, 0, 0.0),
                Sensor("GPU Core", TelemetrySensorKind.Clock, 0, 0.0),
                Sensor("GPU Memory", TelemetrySensorKind.Load, 1, 0.0),
                Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 0, 0.0),
                Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 1, 4_096.0),
                Sensor("GPU Board", TelemetrySensorKind.Power, 0, 0.0)),
            Hardware(
                TelemetryHardwareKind.Memory,
                "Physical Memory",
                Sensor("Memory Used", TelemetrySensorKind.Data, 0, 0.0),
                Sensor("Memory Available", TelemetrySensorKind.Data, 1, 32.0),
                Sensor("Memory", TelemetrySensorKind.Load, 0, 0.0)),
            Hardware(
                TelemetryHardwareKind.Storage,
                "SSD",
                Sensor("Composite", TelemetrySensorKind.Temperature, 0, 0.0),
                Sensor("Used Space", TelemetrySensorKind.Load, 30, 0.0)),
        };

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(hardware);

        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.AreEqual(0.0, snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Cpu.PowerW);

        Assert.IsNull(snapshot.Gpu.TemperatureC);
        Assert.AreEqual(0.0, snapshot.Gpu.UsagePercent);
        Assert.IsNull(snapshot.Gpu.ClockGhz);
        Assert.AreEqual(0.0, snapshot.Gpu.MemoryUsagePercent);
        Assert.AreEqual(0.0, snapshot.Gpu.MemoryUsedGb);
        Assert.AreEqual(4.0, snapshot.Gpu.MemoryTotalGb);
        Assert.IsNull(snapshot.Gpu.PowerW);

        Assert.AreEqual(0.0, snapshot.Memory.UsedGb);
        Assert.AreEqual(32.0, snapshot.Memory.TotalGb);
        Assert.AreEqual(0.0, snapshot.Memory.UsagePercent);
        Assert.IsNull(snapshot.Storage.Single().TemperatureC);
        Assert.AreEqual(0.0, snapshot.Storage.Single().UsagePercent);
    }

    [TestMethod]
    public void Zero_vram_total_does_not_influence_primary_gpu_selection()
    {
        var integratedWithInvalidTotal = Hardware(
            TelemetryHardwareKind.GpuIntel,
            "Intel Graphics",
            Sensor(
                "GPU Memory Total",
                TelemetrySensorKind.SmallData,
                0,
                0.0));
        var dedicatedWithoutMemoryReading = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GPU");

        var selected = TelemetrySnapshotMapper.SelectPrimaryGpu(
            [integratedWithInvalidTotal, dedicatedWithoutMemoryReading]);
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot(
            [integratedWithInvalidTotal, dedicatedWithoutMemoryReading]);

        Assert.AreSame(dedicatedWithoutMemoryReading, selected);
        Assert.AreEqual("NVIDIA GPU", snapshot.Gpu.Name);
        Assert.IsNull(snapshot.Gpu.MemoryTotalGb);
    }

    // ---------------------------------------------------------------- VRAM ---

    [TestMethod]
    public void Vram_uses_dedicated_used_and_total_and_ignores_memory_activity_load()
    {
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([Rtx5060()]);

        // 1228 MiB / 8151 MiB, matching `nvidia-smi --query-gpu=memory.used,memory.total`.
        Assert.AreEqual(1.2, snapshot.Gpu.MemoryUsedGb);
        Assert.AreEqual(7.96, snapshot.Gpu.MemoryTotalGb);
        Assert.AreEqual(15.1, snapshot.Gpu.MemoryUsagePercent);
    }

    [TestMethod]
    public void Gpu_memory_load_sensors_are_never_used_as_vram_occupancy()
    {
        // Both the bare "GPU Memory" load and the "GPU Memory Controller" load
        // sit at values that would be visibly wrong if either leaked through.
        var gpu = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GeForce RTX 5060",
            Sensor("GPU Memory Controller", TelemetrySensorKind.Load, 1, 19.0),
            Sensor("GPU Memory", TelemetrySensorKind.Load, 3, 88.0),
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 2_048.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_192.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([gpu]);
        var vram = TelemetrySnapshotMapper.SelectVram(gpu);

        Assert.AreEqual(25.0, snapshot.Gpu.MemoryUsagePercent);
        Assert.AreEqual(VramSource.GpuMemoryUsedTotal, vram?.Source);
        Assert.AreEqual("GPU Memory Used", vram?.UsedSensorName);
        Assert.AreEqual("GPU Memory Total", vram?.TotalSensorName);
    }

    [TestMethod]
    public void D3d_dedicated_used_never_pairs_with_the_nvml_total()
    {
        // The original defect: "D3D Dedicated Memory Used" won the name match and
        // was divided by "GPU Memory Total", mixing two accounting sources.
        var gpu = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GeForce RTX 5060",
            Sensor("D3D Dedicated Memory Used", TelemetrySensorKind.SmallData, 3, 984.703),
            Sensor("D3D Shared Memory Used", TelemetrySensorKind.SmallData, 4, 67.223),
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_228.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_151.0));

        var vram = TelemetrySnapshotMapper.SelectVram(gpu);

        Assert.AreEqual("GPU Memory Used", vram?.UsedSensorName);
        Assert.AreEqual(VramSource.GpuMemoryUsedTotal, vram?.Source);
    }

    [TestMethod]
    public void D3d_dedicated_pair_is_used_only_when_both_halves_are_d3d()
    {
        var withoutD3dTotal = Hardware(
            TelemetryHardwareKind.GpuAmd,
            "AMD GPU",
            Sensor("D3D Dedicated Memory Used", TelemetrySensorKind.SmallData, 0, 512.0));
        var withD3dTotal = Hardware(
            TelemetryHardwareKind.GpuAmd,
            "AMD GPU",
            Sensor("D3D Dedicated Memory Used", TelemetrySensorKind.SmallData, 0, 512.0),
            Sensor("D3D Dedicated Memory Total", TelemetrySensorKind.SmallData, 1, 2_048.0));

        Assert.IsNull(TelemetrySnapshotMapper.SelectVram(withoutD3dTotal));

        var vram = TelemetrySnapshotMapper.SelectVram(withD3dTotal);
        Assert.AreEqual(VramSource.D3DDedicated, vram?.Source);
        Assert.AreEqual(25.0, vram?.UsagePercent);
    }

    [TestMethod]
    public void Vram_falls_back_to_total_minus_free_from_the_same_source()
    {
        var gpu = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GeForce RTX 5060",
            Sensor("GPU Memory Free", TelemetrySensorKind.SmallData, 0, 6_144.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_192.0));

        var vram = TelemetrySnapshotMapper.SelectVram(gpu);

        Assert.AreEqual(VramSource.GpuMemoryFreeTotal, vram?.Source);
        Assert.AreEqual(25.0, vram?.UsagePercent);
    }

    [TestMethod]
    public void Vram_is_unavailable_without_a_trustworthy_capacity()
    {
        var zeroTotal = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GPU",
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_024.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 0.0));
        var noTotal = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GPU",
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_024.0));

        Assert.IsNull(TelemetrySnapshotMapper.SelectVram(zeroTotal));
        Assert.IsNull(TelemetrySnapshotMapper.SelectVram(noTotal));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([noTotal]);
        Assert.IsNull(snapshot.Gpu.MemoryUsedGb);
        Assert.IsNull(snapshot.Gpu.MemoryTotalGb);
        Assert.IsNull(snapshot.Gpu.MemoryUsagePercent);
    }

    [TestMethod]
    public void Vram_rejects_gross_overshoot_but_clamps_rounding_noise()
    {
        var gross = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GPU",
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 16_384.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_192.0));
        var rounding = Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GPU",
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 8_193.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_192.0));

        Assert.IsNull(
            TelemetrySnapshotMapper.SelectVram(gross),
            "A used value far above total means the sensors disagree.");
        Assert.AreEqual(100.0, TelemetrySnapshotMapper.SelectVram(rounding)?.UsagePercent);
    }

    [TestMethod]
    public void Shared_system_memory_never_contributes_to_vram()
    {
        var gpu = Hardware(
            TelemetryHardwareKind.GpuIntel,
            "Intel Graphics",
            Sensor("D3D Shared Memory Used", TelemetrySensorKind.SmallData, 0, 4_096.0),
            Sensor("GPU Shared Memory Total", TelemetrySensorKind.SmallData, 1, 16_384.0));

        Assert.IsNull(TelemetrySnapshotMapper.SelectVram(gpu));
        Assert.IsNull(
            TelemetrySnapshotMapper.CreateSnapshot([gpu]).Gpu.MemoryTotalGb);
    }

    [TestMethod]
    public void Vram_pair_is_never_mixed_across_two_present_gpus()
    {
        var nvidia = Rtx5060();
        var integrated = Hardware(
            TelemetryHardwareKind.GpuAmd,
            "AMD Radeon(TM) Graphics",
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 500.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 2_048.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([integrated, nvidia]);

        Assert.AreEqual("NVIDIA GeForce RTX 5060", snapshot.Gpu.Name);
        Assert.AreEqual(1.2, snapshot.Gpu.MemoryUsedGb);
        Assert.AreEqual(7.96, snapshot.Gpu.MemoryTotalGb);
        Assert.AreEqual(15.1, snapshot.Gpu.MemoryUsagePercent);
    }

    [TestMethod]
    public void Dump_tags_expose_why_each_memory_sensor_was_accepted_or_rejected()
    {
        var gpu = Rtx5060();

        string[] TagsFor(string sensorName, TelemetrySensorKind kind) =>
            TelemetrySnapshotMapper.DescribeSensorTags(
                gpu,
                gpu.Sensors.Single(sensor =>
                    sensor.Name == sensorName && sensor.Kind == kind),
                isPrimaryCpu: false,
                isPrimaryGpu: true).ToArray();

        CollectionAssert.Contains(
            TagsFor("GPU Memory Used", TelemetrySensorKind.SmallData),
            "[Candidate:VRAM_USED]");
        CollectionAssert.Contains(
            TagsFor("GPU Memory Total", TelemetrySensorKind.SmallData),
            "[Candidate:VRAM_TOTAL]");
        CollectionAssert.Contains(
            TagsFor("GPU Memory", TelemetrySensorKind.Load),
            "[Rejected:GPU_MEMORY_ACTIVITY]");
        CollectionAssert.Contains(
            TagsFor("D3D Shared Memory Used", TelemetrySensorKind.SmallData),
            "[Rejected:GPU_SHARED_MEMORY]");
    }

    // ----------------------------------------------------------------- CPU ---

    [TestMethod]
    public void Cpu_temperature_prefers_tctl_tdie_then_package_over_individual_cores()
    {
        var ryzen = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G",
            Sensor("Core #1", TelemetrySensorKind.Temperature, 3, 70.0),
            Sensor("CPU Package", TelemetrySensorKind.Temperature, 1, 55.0),
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 2, 62.5));
        var withoutTctl = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("Core #1", TelemetrySensorKind.Temperature, 3, 70.0),
            Sensor("CPU Package", TelemetrySensorKind.Temperature, 1, 55.0));

        Assert.AreEqual(
            62.5,
            TelemetrySnapshotMapper.CreateSnapshot([ryzen]).Cpu.TemperatureC);
        Assert.AreEqual(
            55.0,
            TelemetrySnapshotMapper.CreateSnapshot([withoutTctl]).Cpu.TemperatureC);
    }

    [TestMethod]
    public void Cpu_temperature_rejects_unrecognised_and_out_of_range_sensors()
    {
        var unrecognised = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("VRM MOS", TelemetrySensorKind.Temperature, 0, 48.0));
        var absurd = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 0, 512.0));

        Assert.IsNull(
            TelemetrySnapshotMapper.CreateSnapshot([unrecognised]).Cpu.TemperatureC,
            "A sensor that is not a known whole-CPU reading must not stand in for one.");
        Assert.IsNull(
            TelemetrySnapshotMapper.CreateSnapshot([absurd]).Cpu.TemperatureC,
            "Out-of-range garbage must be rejected, not clamped into range.");
    }

    [TestMethod]
    public void Cpu_clock_rejects_bus_and_fabric_clocks()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G",
            Sensor("Bus Speed", TelemetrySensorKind.Clock, 0, 100.0),
            Sensor("Fabric Clock", TelemetrySensorKind.Clock, 1, 1_800.0),
            Sensor("Core #1", TelemetrySensorKind.Clock, 2, 4_200.0),
            Sensor("Core #2", TelemetrySensorKind.Clock, 3, 4_400.0));

        var reading = TelemetrySnapshotMapper.SelectCpuClockReading(cpu);

        Assert.AreEqual(4_300.0, reading?.Mhz);
        CollectionAssert.AreEquivalent(
            new[] { "Core #1", "Core #2" },
            reading?.Sensors.Select(sensor => sensor.Name).ToArray());
        Assert.AreEqual(4.3, TelemetrySnapshotMapper.CreateSnapshot([cpu]).Cpu.ClockGhz);
    }

    [TestMethod]
    public void Cpu_clock_prefers_the_aggregate_sensor_over_effective_and_per_core()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G",
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 4_215.0),
            Sensor("Cores (Average Effective)", TelemetrySensorKind.Clock, 2, 1_100.0),
            Sensor("Core #1", TelemetrySensorKind.Clock, 3, 3_900.0));

        var reading = TelemetrySnapshotMapper.SelectCpuClockReading(cpu);

        Assert.AreEqual(4_215.0, reading?.Mhz);
        StringAssert.Contains(reading?.Definition, "Cores (Average)");
        Assert.AreEqual(4.215, TelemetrySnapshotMapper.CreateSnapshot([cpu]).Cpu.ClockGhz);
    }

    [TestMethod]
    public void Sleeping_cores_are_skipped_without_dragging_the_average_down()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("Core #1", TelemetrySensorKind.Clock, 0, 4_000.0),
            Sensor("Core #2", TelemetrySensorKind.Clock, 1, 0.0),
            Sensor("Core #3", TelemetrySensorKind.Clock, 2, null),
            Sensor("Core #4", TelemetrySensorKind.Clock, 3, 4_400.0));

        var reading = TelemetrySnapshotMapper.SelectCpuClockReading(cpu);

        Assert.AreEqual(4_200.0, reading?.Mhz);
        Assert.AreEqual(2, reading?.Sensors.Count);
    }

    [TestMethod]
    public void Cpu_clock_is_unavailable_when_no_core_reports_a_value()
    {
        var allZero = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G",
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0),
            Sensor("Core #1", TelemetrySensorKind.Clock, 3, 0.0),
            Sensor("Core #1 (Effective)", TelemetrySensorKind.Clock, 4, 0.0));
        var onlyBus = Hardware(
            TelemetryHardwareKind.Cpu,
            "CPU",
            Sensor("Bus Speed", TelemetrySensorKind.Clock, 0, 100.0));

        Assert.IsNull(TelemetrySnapshotMapper.SelectCpuClockReading(allZero));
        Assert.IsNull(TelemetrySnapshotMapper.SelectCpuClockReading(onlyBus));
        Assert.IsNull(
            TelemetrySnapshotMapper.CreateSnapshot([allZero]).Cpu.ClockGhz,
            "A base or bus clock must never be presented as the current clock.");
    }

    [TestMethod]
    public void Amd_igpu_sensors_never_supply_cpu_temperature_or_clock()
    {
        var cpu = Hardware(
            TelemetryHardwareKind.Cpu,
            "AMD Ryzen 5 5600G with Radeon Graphics",
            Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 2, 0.0),
            Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0),
            Sensor("CPU Total", TelemetrySensorKind.Load, 0, 4.44));
        var integratedGpu = Hardware(
            TelemetryHardwareKind.GpuAmd,
            "AMD Radeon(TM) Graphics",
            Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 46.0),
            Sensor("GPU Core", TelemetrySensorKind.Clock, 0, 400.0));

        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([cpu, integratedGpu]);

        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.AreEqual(4.4, snapshot.Cpu.UsagePercent);
        Assert.AreEqual(46.0, snapshot.Gpu.TemperatureC);
    }

    [TestMethod]
    public void Observed_ryzen_5600g_and_rtx_5060_sensors_map_end_to_end()
    {
        // Captured verbatim from `PanelHost --dump-sensors` on the target machine.
        var snapshot = TelemetrySnapshotMapper.CreateSnapshot([
            Hardware(
                TelemetryHardwareKind.Cpu,
                "AMD Ryzen 5 5600G with Radeon Graphics",
                Sensor("CPU Total", TelemetrySensorKind.Load, 0, 4.44),
                Sensor("CPU Core Max", TelemetrySensorKind.Load, 1, 20.0),
                Sensor("Package", TelemetrySensorKind.Power, 0, 0.0),
                Sensor("Cores (Average)", TelemetrySensorKind.Clock, 1, 0.0),
                Sensor("Cores (Average Effective)", TelemetrySensorKind.Clock, 2, 0.0),
                Sensor("Core #1", TelemetrySensorKind.Clock, 3, null),
                Sensor("Core #1 (Effective)", TelemetrySensorKind.Clock, 4, 0.0),
                Sensor("Core (Tctl/Tdie)", TelemetrySensorKind.Temperature, 2, 0.0)),
            Rtx5060(),
        ]);

        Assert.AreEqual("AMD Ryzen 5 5600G with Radeon Graphics", snapshot.Cpu.Name);
        Assert.AreEqual(4.4, snapshot.Cpu.UsagePercent);
        Assert.IsNull(snapshot.Cpu.TemperatureC);
        Assert.IsNull(snapshot.Cpu.ClockGhz);
        Assert.IsNull(snapshot.Cpu.PowerW);

        Assert.AreEqual("NVIDIA GeForce RTX 5060", snapshot.Gpu.Name);
        Assert.AreEqual(31.4, snapshot.Gpu.TemperatureC);
        Assert.AreEqual(7.0, snapshot.Gpu.UsagePercent);
        Assert.AreEqual(1.2, snapshot.Gpu.MemoryUsedGb);
        Assert.AreEqual(7.96, snapshot.Gpu.MemoryTotalGb);
        Assert.AreEqual(15.1, snapshot.Gpu.MemoryUsagePercent);
    }

    /// <summary>The observed RTX 5060 sensor set, including the decoys.</summary>
    private static TelemetryHardwareSample Rtx5060() =>
        Hardware(
            TelemetryHardwareKind.GpuNvidia,
            "NVIDIA GeForce RTX 5060",
            Sensor("GPU Core", TelemetrySensorKind.Temperature, 0, 31.363),
            Sensor("GPU Memory Junction", TelemetrySensorKind.Temperature, 3, 42.0),
            Sensor("GPU Core", TelemetrySensorKind.Load, 0, 7.0),
            Sensor("GPU Memory Controller", TelemetrySensorKind.Load, 1, 19.0),
            Sensor("GPU Memory", TelemetrySensorKind.Load, 3, 15.07),
            Sensor("GPU Core", TelemetrySensorKind.Clock, 0, 885.0),
            Sensor("GPU Memory", TelemetrySensorKind.Clock, 4, 810.0),
            Sensor("D3D Dedicated Memory Used", TelemetrySensorKind.SmallData, 3, 984.703),
            Sensor("D3D Shared Memory Used", TelemetrySensorKind.SmallData, 4, 67.223),
            Sensor("GPU Memory Free", TelemetrySensorKind.SmallData, 0, 6_922.0),
            Sensor("GPU Memory Used", TelemetrySensorKind.SmallData, 1, 1_228.0),
            Sensor("GPU Memory Total", TelemetrySensorKind.SmallData, 2, 8_151.0),
            Sensor("GPU Package", TelemetrySensorKind.Power, 0, 12.429));

    private static TelemetryHardwareSample Hardware(
        TelemetryHardwareKind kind,
        string name,
        params TelemetrySensorSample[] sensors) =>
        new(kind, name, sensors);

    private static TelemetrySensorSample Sensor(
        string name,
        TelemetrySensorKind kind,
        int index,
        double? value,
        string? identifier = null) =>
        new(name, kind, index, value, identifier);

    private static TelemetryHardwareSample GpuWithMemory(
        TelemetryHardwareKind kind,
        string name,
        double? totalMemoryMb) =>
        Hardware(
            kind,
            name,
            Sensor(
                "GPU Memory Total",
                TelemetrySensorKind.SmallData,
                0,
                totalMemoryMb));
}
