using System.Globalization;
using LibreHardwareMonitor.Hardware;
using PanelOS.Telemetry;

namespace PanelHost.SensorProbe;

/// <summary>
/// Minimal LibreHardwareMonitor probe: open the library, update the CPU, print
/// sensors, rejecting voltage without proven kernel access. No PanelOS protocol
/// or transport runs here. Raw Tctl/Tdie/core clocks are kept separate from
/// the shared Windows clock fallback so their origin remains explicit.
/// </summary>
internal static class Program
{
    private const int UpdateRounds = 3;
    private static readonly TimeSpan RoundDelay = TimeSpan.FromMilliseconds(250);

    /// <returns>
    /// 0 when the CPU reports both a non-zero temperature and a non-zero core
    /// clock, 2 when it does not, 1 when the library could not be opened.
    /// </returns>
    private static int Main()
    {
        WriteLibraryIdentity();
        var driver = TelemetryDiagnostics.ProbeKernelDriver();
        WriteKernelDriverStatus(driver);
        using var windowsClock = new WindowsCpuClock();

        var computer = new Computer { IsCpuEnabled = true };
        try
        {
            computer.Open();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Computer.Open() failed: {exception.GetType().Name}: " +
                exception.Message);
            return 1;
        }

        try
        {
            var cpus = computer.Hardware
                .Where(hardware => hardware.HardwareType == HardwareType.Cpu)
                .ToArray();
            if (cpus.Length == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No CPU hardware was enumerated.");
                return 2;
            }

            for (var round = 1; round <= UpdateRounds; round++)
            {
                UpdateAll(cpus);
                WriteRound(round, cpus, driver);
                // Separate from the raw library sensors: the same OS fallback
                // PanelHost uses when no valid core clock is reported.
                var clock = FirstReportingCoreClock(cpus);
                if (clock is null)
                {
                    var mhz = windowsClock.ReadMhz();
                    Console.WriteLine(mhz is { } value
                        ? $"  CPU clock fallback: {value.ToString("0.###", CultureInfo.InvariantCulture)} MHz; source={WindowsCpuClock.Source} (estimate)"
                        : $"  CPU clock fallback: unavailable; {windowsClock.UnavailableReason}");
                }
                if (round < UpdateRounds)
                {
                    Thread.Sleep(RoundDelay);
                }
            }

            return WriteVerdict(cpus) ? 0 : 2;
        }
        finally
        {
            computer.Close();
        }
    }

    /// <summary>
    /// Sub-hardware is updated explicitly: a parent's <c>Update()</c> does not
    /// refresh its children, and reading a child's sensors without it would show
    /// values from an earlier cycle or none at all.
    /// </summary>
    private static void UpdateAll(IEnumerable<IHardware> hardwareItems)
    {
        foreach (var hardware in hardwareItems)
        {
            try
            {
                hardware.Update();
            }
            catch (Exception exception)
            {
                Console.WriteLine(
                    $"  {hardware.Name}: Update() threw " +
                    $"{exception.GetType().Name}: {exception.Message}");
            }

            UpdateAll(hardware.SubHardware);
        }
    }

    private static void WriteRound(int round, IReadOnlyList<IHardware> cpus, KernelDriverStatus driver)
    {
        Console.WriteLine();
        Console.WriteLine($"=== CPU sensors after hardware.Update() #{round} ===");
        foreach (var cpu in cpus)
        {
            Console.WriteLine($"  {cpu.Name} | {cpu.Identifier}");
            foreach (var sensor in cpu.Sensors
                .OrderBy(sensor => sensor.SensorType)
                .ThenBy(sensor => sensor.Index))
            {
                if (sensor.SensorType == SensorType.Voltage && !driver.IsUsable)
                {
                    Console.WriteLine($"    Voltage      | {sensor.Name,-28} | unavailable (raw reading rejected: PawnIO access not proven)");
                    continue;
                }
                Console.WriteLine(
                    $"    {sensor.SensorType,-12} | {sensor.Name,-28} | " +
                    $"{sensor.Identifier,-30} | value={Format(sensor.Value),-12} " +
                    $"min={Format(sensor.Min),-12} max={Format(sensor.Max),-12}");
            }
        }
    }

    /// <summary>
    /// Reports raw availability only. A zero is called out rather than treated
    /// as a reading: on AMD a Tctl/Tdie or P-state clock of exactly 0 is what a
    /// missing MSR/SMU path looks like, not a measurement.
    /// </summary>
    private static bool WriteVerdict(IReadOnlyList<IHardware> cpus)
    {
        var temperature = FirstReporting(cpus, SensorType.Temperature);
        var clock = FirstReportingCoreClock(cpus);
        Console.WriteLine();
        Console.WriteLine("Verdict");
        Console.WriteLine(
            "  CPU temperature: " +
            (temperature is null
                ? "no temperature sensor reported a non-zero value"
                : $"'{temperature.Name}' = {Format(temperature.Value)}"));
        Console.WriteLine(
            "  CPU clock (raw LibreHardwareMonitor): " +
            (clock is null
                ? "no clock sensor reported a non-zero value"
                : $"'{clock.Name}' = {Format(clock.Value)}"));
        Console.WriteLine(
            "  CPU load: " +
            (FirstReporting(cpus, SensorType.Load) is { } load
                ? $"'{load.Name}' = {Format(load.Value)}"
                : "no load sensor reported a non-zero value"));
        Console.WriteLine(
            "  CPU power: " +
            (FirstReporting(cpus, SensorType.Power) is { } power
                ? $"'{power.Name}' = {Format(power.Value)}"
                : "no power sensor reported a non-zero value"));
        return temperature is not null && clock is not null;
    }

    private static ISensor? FirstReporting(
        IReadOnlyList<IHardware> cpus,
        SensorType type) =>
        cpus
            .SelectMany(cpu => cpu.Sensors)
            .Where(sensor => sensor.SensorType == type)
            .FirstOrDefault(sensor =>
                sensor.Value is { } value && double.IsFinite(value) && value > 0);

    private static ISensor? FirstReportingCoreClock(IReadOnlyList<IHardware> cpus) =>
        cpus.SelectMany(cpu => cpu.Sensors).FirstOrDefault(sensor =>
            sensor.SensorType == SensorType.Clock &&
            sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
            sensor.Value is > 0 and <= 10000);

    private static void WriteLibraryIdentity()
    {
        Console.WriteLine("LibreHardwareMonitorLib identity");
        Console.WriteLine(
            $"  assembly version: {TelemetryDiagnostics.LibreHardwareMonitorVersion()}");
        Console.WriteLine(
            "  informational version: " +
            TelemetryDiagnostics.LibreHardwareMonitorInformationalVersion());
        Console.WriteLine(
            $"  file: {TelemetryDiagnostics.LibreHardwareMonitorLocation()}");
        Console.WriteLine(
            $"  SHA-256: {TelemetryDiagnostics.LibreHardwareMonitorFileHash()}");
        Console.WriteLine(
            "  process: " +
            $"{(Environment.Is64BitProcess ? "x64" : "x86")}, elevated=" +
            (TelemetryDiagnostics.IsProcessElevated() ? "yes" : "no"));
    }

    private static void WriteKernelDriverStatus(KernelDriverStatus status)
    {
        Console.WriteLine();
        Console.WriteLine("PawnIO kernel driver");
        Console.WriteLine($"  state: {status.State}");
        Console.WriteLine($"  detail: {status.Summary}");
        if (status.DeviceInstanceId is { } instance)
        {
            Console.WriteLine($"  device: {instance}");
        }

        if (status.ImagePath is { } image)
        {
            Console.WriteLine($"  image: {image}");
        }

        if (status.ProblemCode is { } problem)
        {
            Console.WriteLine($"  problem code: {problem}");
        }
    }

    private static string Format(float? value) =>
        value.HasValue && float.IsFinite(value.Value)
            ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "null";
}
