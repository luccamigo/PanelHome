using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PanelHost.Services;

internal sealed class WindowsComputerStatsSource : IComputerStatsSource
{
    public IReadOnlyList<NetworkCounter> ReadNetwork()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up &&
                    x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Take(64).Select(adapter =>
                {
                    try
                    {
                        var stats = adapter.GetIPStatistics();
                        return new NetworkCounter(adapter.Id, CleanName(adapter.Name), stats.BytesReceived, stats.BytesSent);
                    }
                    catch (NetworkInformationException) { return new NetworkCounter(adapter.Id, CleanName(adapter.Name), null, null); }
                }).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    public IReadOnlyList<ProcessCounter> ReadProcesses()
    {
        var counters = new List<ProcessCounter>();
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Win32Exception) { return counters; }
        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var process in processes)
        {
            using (process)
            {
                string name = $"PID {process.Id}";
                try
                {
                    name = CleanName(process.ProcessName);
                    var start = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime.TotalSeconds;
                    var memory = process.WorkingSet64;
                    var owner = ReadOwner(process, identity.User, current.SessionId);
                    var isProtected = owner != "current_user" || IsProtected(process, name);
                    counters.Add(new(name, process.Id, start, cpu, memory, owner, isProtected));
                }
                catch (InvalidOperationException) { /* Process exited between enumeration and reading. */ }
                catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    counters.Add(new(name, process.Id, null, null, null, "unknown", true));
                }
            }
        }
        return counters;
    }

    internal static string ReadOwner(Process process, SecurityIdentifier? currentUser, int currentSession)
    {
        if (!OpenProcessToken(process.SafeHandle, 0x0008, out var token)) return "unknown";
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            var owner = identity.User;
            if (owner is null) return "unknown";
            if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
                owner.IsWellKnown(WellKnownSidType.NetworkServiceSid)) return "system";
            return currentUser is not null && owner.Equals(currentUser) && process.SessionId == currentSession ? "current_user" : "other_user";
        }
    }

    internal static bool IsProtected(Process process, string name)
    {
        if (process.Id == Environment.ProcessId || process.Id <= 4 || ProcessProtectionPolicy.IsProtectedName(name)) return true;
        using var target = WindowsProcessTerminationTargets.Open(process.Id, allowTerminate: false);
        return target is null || !ProcessTerminationService.Allowed(target.Facts);
    }

    private static string CleanName(string text) => new(text.Where(c => !char.IsControl(c) &&
        c is not ('\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069')).Take(80).ToArray());

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

}
