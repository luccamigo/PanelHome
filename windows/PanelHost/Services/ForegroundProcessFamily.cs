using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PanelHost.Services;

internal sealed record ForegroundProcessNode(int Pid, int ParentPid, string ImageName);

/// <summary>Same-image process family, bounded to four levels and 256 identities. No command-line or privilege access.</summary>
internal static class ForegroundProcessFamily
{
    private const int MaxDepth = 4;
    private const int MaxMembers = 256;

    internal static Dictionary<int, long> Read(int foregroundPid)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) return [];
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var nodes = new List<ForegroundProcessNode>();
        if (!Process32First(snapshot, ref entry)) return [];
        do { nodes.Add(new((int)entry.Pid, (int)entry.ParentPid, entry.ImageName)); }
        while (Process32Next(snapshot, ref entry));
        var byPid = nodes.ToDictionary(n => n.Pid);
        return Select(foregroundPid, nodes, pid =>
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                // A PID may have been reused since the native snapshot.
                if (!string.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(byPid[pid].ImageName), StringComparison.OrdinalIgnoreCase)) return null;
                return process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        });
    }

    internal static Dictionary<int, long> Select(int foregroundPid, IEnumerable<ForegroundProcessNode> nodes, Func<int, long?> started)
    {
        var byPid = nodes.ToDictionary(n => n.Pid);
        if (!byPid.TryGetValue(foregroundPid, out var foreground)) return [];
        var times = new Dictionary<int, long?>();
        long? Time(int id) => times.TryGetValue(id, out var time) ? time : times[id] = started(id);
        if (Time(foregroundPid) is not long foregroundTime) return [];
        bool SameImage(ForegroundProcessNode node) => string.Equals(node.ImageName, foreground.ImageName, StringComparison.OrdinalIgnoreCase);
        var root = foreground;
        var ancestry = new HashSet<int> { foregroundPid };
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (!byPid.TryGetValue(root.ParentPid, out var parent) || !SameImage(parent) ||
                !ancestry.Add(parent.Pid) || Time(parent.Pid) is not long parentTime || Time(root.Pid) < parentTime) break;
            root = parent;
        }
        var family = new Dictionary<int, long> { [root.Pid] = Time(root.Pid)!.Value };
        var level = new[] { root.Pid };
        for (var depth = 0; depth < MaxDepth && level.Length > 0 && family.Count < MaxMembers; depth++)
        {
            var next = new List<int>();
            foreach (var node in byPid.Values)
            {
                if (!level.Contains(node.ParentPid) || family.ContainsKey(node.Pid) || !SameImage(node) ||
                    Time(node.Pid) is not long time || time < family[node.ParentPid]) continue;
                family.Add(node.Pid, time);
                next.Add(node.Pid);
                if (family.Count == MaxMembers) break;
            }
            level = next.ToArray();
        }
        // Foreground itself must be represented even if the member limit truncated siblings.
        if (!family.ContainsKey(foregroundPid)) return new() { [foregroundPid] = foregroundTime };
        return family;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ImageName;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
