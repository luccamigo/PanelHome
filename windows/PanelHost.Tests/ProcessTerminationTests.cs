using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class ProcessTerminationTests
{
    private static ProcessSafetyFacts Safe => new(101, 1000, "editor", true, true, true, false, true, false);
    private static string Id => Guid.NewGuid().ToString("N");

    [TestMethod]
    public void Gate_is_disabled_unreviewed_and_does_not_even_open_a_process()
    {
        var targets = new Targets(Safe);
        var service = new ProcessTerminationService(targets);
        Assert.IsFalse(service.Reviewed);
        Assert.IsFalse(service.SetEnabled(true));
        Assert.AreEqual("disabled", service.Terminate(Id, 101, 1000, true).Result);
        Assert.AreEqual(0, targets.Opens);
    }

    [TestMethod]
    public void Each_security_check_refuses_independently_and_uses_only_the_revalidated_handle()
    {
        var variants = new[] { Safe with { SameUser = false }, Safe with { SameSession = false }, Safe with { IntegrityAllowed = false },
            Safe with { Critical = true }, Safe with { OutsideWindows = false }, Safe with { ParentOfHost = true },
            Safe with { Name = "svchost" }, Safe with { Name = "PanelOS.anything" }, Safe with { Name = "MsMpEng" },
            Safe with { OpenBotOwned = true }, Safe with { Name = "OpenBot" }, Safe with { Name = "cua-driver" } };
        foreach (var facts in variants)
        {
            var targets = new Targets(facts);
            var service = new ProcessTerminationService(targets, reviewed: true);
            Assert.IsTrue(service.SetEnabled(true));
            Assert.AreEqual("protected", service.Terminate(Id, 101, 1000, true).Result, facts.ToString());
            Assert.AreEqual(0, targets.Kills);
            Assert.IsTrue(targets.Disposed);
        }
        var allowed = new Targets(Safe);
        var enabled = new ProcessTerminationService(allowed, reviewed: true); enabled.SetEnabled(true);
        Assert.AreEqual("terminated", enabled.Terminate(Id, 101, 1000, true).Result);
        Assert.AreEqual(1, allowed.Opens); Assert.AreEqual(1, allowed.Kills); Assert.IsTrue(allowed.Disposed);
    }

    [TestMethod]
    public void Pid_reuse_exited_unlisted_and_permission_failures_never_terminate()
    {
        var reused = new Targets(Safe with { StartTimeUtcTicks = 1001 });
        var service = new ProcessTerminationService(reused, reviewed: true); service.SetEnabled(true);
        Assert.AreEqual("gone", service.Terminate(Id, 101, 1000, true).Result);
        Assert.AreEqual("gone", service.Terminate(Id, 101, 1000, false).Result);
        Assert.AreEqual(1, reused.Opens); Assert.AreEqual(0, reused.Kills);
        var gone = new ProcessTerminationService(new Targets(null), reviewed: true); gone.SetEnabled(true);
        Assert.AreEqual("gone", gone.Terminate(Id, 101, 1000, true).Result);
        var denied = new Targets(Safe) { Success = false };
        var refuse = new ProcessTerminationService(denied, reviewed: true); refuse.SetEnabled(true);
        Assert.AreEqual("denied", refuse.Terminate(Id, 101, 1000, true).Result);
    }

    [TestMethod]
    public void A_request_for_a_process_listed_as_protected_is_protected_and_never_opens_it()
    {
        var targets = new Targets(Safe);
        var service = new ProcessTerminationService(targets, reviewed: true); service.SetEnabled(true);
        Assert.AreEqual("protected", service.Terminate(Id, 101, 1000, offered: false, listedProtected: true).Result);
        Assert.AreEqual("gone", service.Terminate(Id, 101, 1000, offered: false, listedProtected: false).Result);
        Assert.AreEqual(0, targets.Opens);
        Assert.AreEqual(0, targets.Kills);
        // The gate and the request checks still come first.
        var closed = new ProcessTerminationService(targets, reviewed: true);
        Assert.AreEqual("disabled", closed.Terminate(Id, 101, 1000, false, true).Result);
        Assert.AreEqual("invalid_request", service.Terminate("short", 101, 1000, false, true).Result);
    }

    [TestMethod]
    public void Conservative_list_covers_windows_security_cable_and_panelos_in_any_case()
    {
        foreach (var name in new[] { "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe",
            "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "fontdrvhost.exe", "dwm.exe", "LogonUI.exe", "LockApp.exe",
            "userinit.exe", "consent.exe", "CredentialUIBroker.exe", "WUDFHost.exe", "audiodg.exe", "spoolsv.exe", "WmiPrvSE.exe", "dllhost.exe",
            "conhost.exe", "dasHost.exe", "unsecapp.exe", "AggregatorHost.exe", "SgrmBroker.exe", "explorer.exe", "sihost.exe", "ctfmon.exe",
            "taskhostw.exe", "RuntimeBroker.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "ShellHost.exe", "TextInputHost.exe",
            "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe", "smartscreen.exe",
            "PanelHost.exe", "PanelDisplayBroker.exe", "PanelOS.ControlCenter.exe", "PanelOS.Future.exe", "adb.exe" })
            Assert.IsTrue(ProcessProtectionPolicy.IsProtectedName(name.ToUpperInvariant()), name);
        Assert.IsFalse(ProcessProtectionPolicy.IsProtectedName("editor"));
    }

    [TestMethod]
    public void OpenBot_app_and_its_helpers_are_protected_by_name_and_by_folder()
    {
        foreach (var name in new[] { "OpenBot.exe", "openbot", "OPENBOT.EXE", "OpenBot Helper.exe", "cua-driver.exe" })
            Assert.IsTrue(ProcessProtectionPolicy.IsProtectedName(name), name);
        Assert.IsFalse(ProcessProtectionPolicy.IsProtectedName("Bot.exe"));

        // Helpers keep generic names, so the folders decide: the install, the data and the agent runtimes.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(local, "Programs", "OpenBot", "OpenBot.exe")));
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(local, "Programs", "OpenBot", "resources", "cua-driver", "cua-driver.exe")));
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(roaming, "OpenBot", "provider-runtimes", "claude", "bin", "claude.exe")));
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(roaming, "openbot", "provider-runtimes", "node.exe")));
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(roaming, "OpenBot", "x", "..", "y", "node.exe")));
        Assert.IsFalse(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(local, "Programs", "OpenBotTools", "tool.exe")));
        Assert.IsFalse(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(local, "Programs", "Other", "OpenBot", "tool.exe")));
        Assert.IsFalse(ProcessProtectionPolicy.IsOpenBotImage(Path.Combine(roaming, "Other", "node.exe")));

        var roots = new[] { @"C:\Apps\OpenBot\", "" };
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage(@"C:\apps\openbot\helper.exe", roots));
        Assert.IsFalse(ProcessProtectionPolicy.IsOpenBotImage(@"C:\Apps\OpenBot", roots));
        Assert.IsFalse(ProcessProtectionPolicy.IsOpenBotImage(@"C:\Apps\OpenBotX\a.exe", roots));
        Assert.IsTrue(ProcessProtectionPolicy.IsOpenBotImage("\0bad", roots), "an unreadable path is treated as protected");

        var service = new ProcessTerminationService(new Targets(Safe with { Name = "claude", OpenBotOwned = true }), reviewed: true);
        service.SetEnabled(true);
        Assert.AreEqual("protected", service.Terminate(Id, 101, 1000, true).Result);
        Assert.IsFalse(ProcessTerminationService.Allowed(Safe with { Name = "claude", OpenBotOwned = true }));
        Assert.IsTrue(ProcessTerminationService.Allowed(Safe with { Name = "claude" }));
    }

    private sealed class Targets(ProcessSafetyFacts? facts) : IProcessTerminationTargets
    {
        internal int Opens, Kills; internal bool Disposed; internal bool Success = true;
        public IProcessTerminationTarget? Open(int pid) { Opens++; return facts is null ? null : new Target(this, facts); }
        private sealed class Target(Targets owner, ProcessSafetyFacts facts) : IProcessTerminationTarget
        {
            public ProcessSafetyFacts Facts => facts;
            public bool Terminate() { owner.Kills++; return owner.Success; }
            public void Dispose() => owner.Disposed = true;
        }
    }
}
