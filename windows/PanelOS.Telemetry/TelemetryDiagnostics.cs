using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PanelOS.Telemetry;

/// <summary>
/// How far the PawnIO kernel driver actually got. "Installed" and "usable" are
/// deliberately different states: a registered driver package that Windows
/// refuses to load still leaves every MSR/SMU sensor empty, and reporting it as
/// "present" is what produced the wrong "unsupported for this CPU" conclusion.
/// </summary>
public enum KernelDriverState
{
    /// <summary>The probe itself failed; nothing may be concluded.</summary>
    Unknown,

    /// <summary>No PawnIO service and no PawnIO installation directory.</summary>
    NotInstalled,

    /// <summary>Registered, but the device object could not be opened.</summary>
    InstalledNotLoaded,

    /// <summary>Windows PnP reports a problem code for the device node.</summary>
    Blocked,

    /// <summary>The device object exists but this process may not open it.</summary>
    AccessDenied,

    /// <summary>The device object was opened: ring0 access is really available.</summary>
    Loaded,
}

/// <summary>
/// Result of probing the PawnIO kernel driver that LibreHardwareMonitor uses for
/// AMD SMU and MSR access.
/// </summary>
public sealed record KernelDriverStatus(
    KernelDriverState State,
    string Summary,
    string? ImagePath = null,
    uint? ProblemCode = null,
    string? DeviceInstanceId = null)
{
    /// <summary>True only when ring0 access was actually demonstrated.</summary>
    public bool IsUsable => State == KernelDriverState.Loaded;

    /// <summary>True when a driver package exists, usable or not.</summary>
    public bool IsPresent =>
        State is KernelDriverState.InstalledNotLoaded
            or KernelDriverState.Blocked
            or KernelDriverState.AccessDenied
            or KernelDriverState.Loaded;
}

/// <summary>
/// Environment probes that explain <em>why</em> a sensor is missing.
/// Kept separate from selection logic so the mapper stays deterministic and
/// unit testable: the mapper receives the probed facts, it never reads them.
/// </summary>
public static class TelemetryDiagnostics
{
    /// <summary>
    /// Device object LibreHardwareMonitor opens to talk to PawnIO. Opening it is
    /// the only check that distinguishes "driver files exist on disk" from
    /// "ring0 access works"; the file-existence check this replaced reported a
    /// blocked driver as present.
    /// </summary>
    private const string PawnIoDevicePath = @"\\?\GLOBALROOT\Device\PawnIO";

    private const string PawnIoServiceKey =
        @"SYSTEM\CurrentControlSet\Services\PawnIO";

    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint OpenExisting = 3;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;

    private const uint CmLocateDevNodePhantom = 0x0000_0001;
    private const int CrSuccess = 0;

    /// <summary>
    /// LibreHardwareMonitor reads AMD Tctl/Tdie and per-core P-state clocks
    /// through the PawnIO kernel driver (MSR + SMU access). Opening that driver
    /// requires Administrator, so a non-elevated host sees the sensors exist but
    /// report 0. Reporting the real elevation state stops the diagnostics from
    /// blaming privileges when the process is already elevated.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // Treat an unreadable token as non-elevated: suggesting elevation is
            // harmless, whereas claiming elevation would suppress a real hint.
            return false;
        }
    }

    /// <summary>
    /// Probes the PawnIO kernel driver that LibreHardwareMonitor 0.9.x uses to
    /// reach AMD SMU and MSR registers (its embedded
    /// <c>Resources.PawnIo.RyzenSMU.bin</c> and <c>AMDFamily17.bin</c> modules).
    /// The probe opens the driver's device object rather than looking for files,
    /// because an installed-but-unloadable driver leaves Tctl/Tdie and per-core
    /// clocks empty exactly like a missing one while looking installed on disk.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static KernelDriverStatus ProbeKernelDriver()
    {
        try
        {
            return ProbeKernelDriverCore();
        }
        catch (Exception exception)
        {
            // An unreadable environment must not be reported as a verdict.
            return new KernelDriverStatus(
                KernelDriverState.Unknown,
                $"PawnIO status could not be determined: {exception.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    private static KernelDriverStatus ProbeKernelDriverCore()
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey(PawnIoServiceKey);
        var imagePath = serviceKey?.GetValue("ImagePath") as string;
        var deviceInstanceId = ReadDeviceInstanceId(serviceKey);

        using var device = OpenPawnIoDevice(out var openError);
        if (!device.IsInvalid)
        {
            return new KernelDriverStatus(
                KernelDriverState.Loaded,
                "PawnIO driver is loaded and its device object was opened.",
                imagePath,
                DeviceInstanceId: deviceInstanceId);
        }

        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PawnIO");
        var installed = serviceKey is not null || Directory.Exists(installDirectory);
        if (!installed)
        {
            return new KernelDriverStatus(
                KernelDriverState.NotInstalled,
                "PawnIO is not installed; LibreHardwareMonitor cannot reach AMD " +
                "SMU/MSR registers without it.");
        }

        var problem = ReadDeviceProblemCode(deviceInstanceId);
        if (problem is { } problemCode && problemCode != 0)
        {
            return new KernelDriverStatus(
                KernelDriverState.Blocked,
                "PawnIO is installed but Windows refuses to start its device " +
                $"'{deviceInstanceId}': {DescribeProblemCode(problemCode)}.",
                imagePath,
                problemCode,
                deviceInstanceId);
        }

        if (openError == ErrorAccessDenied)
        {
            return new KernelDriverStatus(
                KernelDriverState.AccessDenied,
                "PawnIO is loaded but this process may not open its device " +
                "object; elevated access is required.",
                imagePath,
                DeviceInstanceId: deviceInstanceId);
        }

        var reason = openError is ErrorFileNotFound or ErrorPathNotFound
            ? "its device object does not exist, so the driver is not running"
            : $"opening its device object failed with Win32 error {openError}";
        return new KernelDriverStatus(
            KernelDriverState.InstalledNotLoaded,
            $"PawnIO is installed but {reason}.",
            imagePath,
            DeviceInstanceId: deviceInstanceId);
    }

    /// <summary>
    /// Resolved assembly version of LibreHardwareMonitorLib actually loaded at
    /// runtime, rather than the version requested in the csproj.
    /// </summary>
    public static string LibreHardwareMonitorVersion()
    {
        try
        {
            var name = typeof(Computer).Assembly.GetName();
            return name.Version?.ToString() ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Informational version of the loaded LibreHardwareMonitorLib. The NuGet
    /// package stamps the upstream commit here (<c>0.9.6+&lt;sha&gt;</c>), which
    /// is the only field that identifies the build rather than the API contract:
    /// several different builds all report assembly version 0.9.6.0.
    /// </summary>
    public static string LibreHardwareMonitorInformationalVersion() =>
        ReadAssemblyFact(assembly => assembly
            .GetCustomAttributes(
                typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion);

    /// <summary>Path the LibreHardwareMonitorLib assembly was loaded from.</summary>
    public static string LibreHardwareMonitorLocation() =>
        ReadAssemblyFact(assembly =>
            string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location);

    /// <summary>
    /// SHA-256 of the loaded LibreHardwareMonitorLib file. Pins the exact binary
    /// so a report can never be attributed to the wrong build.
    /// </summary>
    public static string LibreHardwareMonitorFileHash()
    {
        try
        {
            var location = typeof(Computer).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location))
            {
                return "unknown";
            }

            using var stream = File.OpenRead(location);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string ReadAssemblyFact(
        Func<System.Reflection.Assembly, string?> read)
    {
        try
        {
            return read(typeof(Computer).Assembly) ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadDeviceInstanceId(RegistryKey? serviceKey)
    {
        using var enumKey = serviceKey?.OpenSubKey("Enum");
        return enumKey?.GetValue("0") as string;
    }

    /// <summary>
    /// PnP problem code of the driver's device node, or null when the node
    /// cannot be located. Code 52 (<c>CM_PROB_UNSIGNED_DRIVER</c>) is what a
    /// code-integrity rejection looks like from user mode.
    /// </summary>
    private static uint? ReadDeviceProblemCode(string? deviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            return null;
        }

        // PHANTOM: the node must be locatable even though it never started.
        if (CM_Locate_DevNodeW(
                out var devInst,
                deviceInstanceId,
                CmLocateDevNodePhantom) != CrSuccess)
        {
            return null;
        }

        return CM_Get_DevNode_Status(out _, out var problem, devInst, 0) == CrSuccess
            ? problem
            : null;
    }

    private static string DescribeProblemCode(uint problemCode) => problemCode switch
    {
        18 => "the device must be reinstalled (code 18)",
        28 => "no driver is installed for it (code 28)",
        31 => "Windows could not load the driver (code 31)",
        39 => "the driver file is missing or corrupt (code 39)",
        52 => "its digital signature could not be verified, so Windows code " +
            "integrity refused to load it (code 52)",
        _ => $"Windows PnP problem code {problemCode}",
    };

    private static SafeFileHandle OpenPawnIoDevice(out int error)
    {
        var handle = CreateFileW(
            PawnIoDevicePath,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        EntryPoint = "CreateFileW",
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int CM_Locate_DevNodeW(
        out uint devInst,
        string deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern int CM_Get_DevNode_Status(
        out uint status,
        out uint problemNumber,
        uint devInst,
        uint flags);
}
