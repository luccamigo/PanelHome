using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PanelHost.Services;

internal sealed class WindowsProcessTerminationTargets : IProcessTerminationTargets
{
    public IProcessTerminationTarget? Open(int pid) => Open(pid, allowTerminate: true);
    internal static IProcessTerminationTarget? Open(int pid, bool allowTerminate)
    {
        var handle = OpenProcess(0x1000u | (allowTerminate ? 1u : 0u), false, pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error == 87 || !allowTerminate) return null;
            throw new Win32Exception(error);
        }
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception();
            var path = new StringBuilder(32768); var length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length) || !IsProcessCritical(handle, out var critical) ||
                !ProcessIdToSessionId(pid, out var session) || !ProcessIdToSessionId(Environment.ProcessId, out var hostSession) ||
                !GetProcessInformation(handle, 7, out var protection, 4) || protection != 0xfffffffe ||
                !OpenProcessToken(handle, 8, out var token)) throw new Win32Exception();
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
            using (var current = WindowsIdentity.GetCurrent())
            using (var host = Process.GetCurrentProcess())
            {
                var targetIntegrity = Integrity(token);
                var hostIntegrity = Integrity(current.AccessToken);
                var parent = new nint[6];
                if (NtQueryInformationProcess(host.SafeHandle, 0, parent, IntPtr.Size * parent.Length, out _) != 0) throw new Win32Exception();
                var windows = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                var image = Path.GetFullPath(path.ToString());
                var outsideWindows = !image.Equals(windows, StringComparison.OrdinalIgnoreCase) &&
                    !image.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var facts = new ProcessSafetyFacts(pid, DateTime.FromFileTimeUtc(created).Ticks, Path.GetFileNameWithoutExtension(image),
                    current.User is not null && owner.User?.Equals(current.User) == true, session == hostSession,
                    targetIntegrity <= hostIntegrity, critical, outsideWindows, parent[5].ToInt64() == pid,
                    ProcessProtectionPolicy.IsOpenBotImage(image));
                return new Target(handle, facts);
            }
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { handle.Dispose(); if (allowTerminate) throw; return null; }
    }

    private sealed class Target(SafeProcessHandle handle, ProcessSafetyFacts facts) : IProcessTerminationTarget
    {
        public ProcessSafetyFacts Facts => facts;
        public bool Terminate() => TerminateProcess(handle, 1);
        public void Dispose() => handle.Dispose();
    }

    private static int Integrity(SafeAccessTokenHandle token)
    {
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out var length);
        if (length <= 0 || length > 65536) throw new Win32Exception();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, length, out _)) throw new Win32Exception();
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) throw new Win32Exception();
            return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)count - 1));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int information, IntPtr buffer, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int information, [Out] nint[] buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int information, out uint protection, uint size);
}
