using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PanelHost.Tests;

/// <summary>
/// The product refuses to run the Wi-Fi listener in an elevated process, and its management pipe is owned by the
/// user, not by Administrators. Those tests therefore cannot run in an elevated process. GitHub's Windows runners
/// run as Administrator, so there they are reported as inconclusive (skipped, never silently passing) and are
/// still enforced on any developer machine and in a normal, non-elevated session.
/// </summary>
internal static class ElevatedRunnerGuard
{
    public static void RequireNonElevatedProcess()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Assert.Inconclusive("This test needs a non-elevated process (the host refuses Wi-Fi and owns its pipe as the user); the current process is an administrator, as on GitHub's Windows runners.");
    }
}
