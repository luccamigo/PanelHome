namespace PanelHost.Services;

// Security review 2026-11-16: adding names is conservative; removing one requires a new security review.
internal static class ProcessProtectionPolicy
{
    internal const bool Reviewed = false;
    internal static bool IsProtectedName(string name) => name.StartsWith("PanelOS", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OpenBot", StringComparison.OrdinalIgnoreCase) ||
        Names.Contains(Path.GetFileNameWithoutExtension(name));

    // E2 (security review, 16/11): one tap must not take down the OpenBot app or its helpers (agent runtimes, cua-driver).
    // Helpers keep other names (claude, node), so the install, data and runtime folders are protected by path too.
    internal static bool IsOpenBotImage(string imagePath) => IsOpenBotImage(imagePath, OpenBotRoots());
    internal static bool IsOpenBotImage(string imagePath, IEnumerable<string> roots)
    {
        string image;
        try { image = Path.GetFullPath(imagePath); } catch (ArgumentException) { return true; }
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (image.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private static IEnumerable<string> OpenBotRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programsX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var parent in new[] { local, roaming, programs, programsX86 })
        {
            if (string.IsNullOrWhiteSpace(parent)) continue;
            yield return Path.Combine(parent, "OpenBot");
        }
        if (!string.IsNullOrWhiteSpace(local)) yield return Path.Combine(local, "Programs", "OpenBot");
    }
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "lsaiso", "svchost", "fontdrvhost", "dwm", "LogonUI", "LockApp", "userinit", "consent",
        "CredentialUIBroker", "WUDFHost", "audiodg", "spoolsv", "WmiPrvSE", "dllhost", "conhost", "dasHost", "unsecapp",
        "AggregatorHost", "SgrmBroker", "explorer", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "ShellExperienceHost",
        "StartMenuExperienceHost", "ShellHost", "TextInputHost", "MsMpEng", "NisSrv", "MpDefenderCoreService",
        "SecurityHealthService", "SecurityHealthSystray", "smartscreen", "PanelHost", "PanelDisplayBroker", "adb", "cua-driver",
    };
}

internal sealed record ProcessSafetyFacts(int Pid, long StartTimeUtcTicks, string Name, bool SameUser,
    bool SameSession, bool IntegrityAllowed, bool Critical, bool OutsideWindows, bool ParentOfHost, bool OpenBotOwned = false);
internal interface IProcessTerminationTarget : IDisposable
{
    ProcessSafetyFacts Facts { get; }
    bool Terminate();
}
internal interface IProcessTerminationTargets { IProcessTerminationTarget? Open(int pid); }
