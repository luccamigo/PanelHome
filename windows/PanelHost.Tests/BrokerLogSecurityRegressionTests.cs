using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelDisplayBroker;

namespace PanelHost.Tests;

// Regressions adapted from local security probes. Temp paths only.
// Owner/writer predicates are injected to isolate link handling without elevation.
[TestClass]
public sealed class BrokerLogSecurityRegressionTests
{
    private static string Dir()
    {
        var d = Path.Combine(Path.GetTempPath(), "security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static bool Cmd(string args)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + args)
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    [TestMethod]
    public void P1_symlink_to_missing_target_makes_the_broker_create_the_target()
    {
        var dir = Dir();
        var target = Path.Combine(Dir(), "created-by-broker.txt");
        if (!Cmd($"mklink \"{Path.Combine(dir, BrokerLog.FileName)}\" \"{target}\"")) Assert.Inconclusive("no symlink");
        string? msg = null;
        using var log = BrokerLog.Open(dir, _ => true, t => msg = t, trustedWriter: _ => true);
        Console.WriteLine($"P1 refused={log.IsWritingToFile == false} msg={msg} targetCreated={File.Exists(target)}");
        Assert.IsFalse(File.Exists(target), "the elevated broker must not create the symlink target");
    }

    [TestMethod]
    public void P2_hardlink_to_a_file_gets_log_lines_appended()
    {
        var dir = Dir();
        var victim = Path.Combine(Dir(), "victim.txt");
        File.WriteAllText(victim, "ORIGINAL\n");
        if (!Cmd($"mklink /H \"{Path.Combine(dir, BrokerLog.FileName)}\" \"{victim}\"")) Assert.Inconclusive("no hardlink");
        using (var log = BrokerLog.Open(dir, _ => true, trustedWriter: _ => true))
        {
            log.Info("probe", "marker-line");
        }
        var text = File.ReadAllText(victim);
        Console.WriteLine($"P2 victim now: {text.Replace("\n", " | ")}");
        Assert.IsFalse(text.Contains("marker-line"), "the broker must not append through a hardlink");
    }

    [TestMethod]
    public void P3_existing_directory_acl_is_not_inspected()
    {
        var dir = Dir(); // inherits the user's temp ACL: user-writable, not protected
        using var log = BrokerLog.Open(dir, _ => true);
        Console.WriteLine($"P3 accepted a directory with an unprotected inherited ACL: {log.IsWritingToFile}");
        Assert.IsFalse(log.IsWritingToFile, "an existing directory with a user-writable ACL should be refused");
    }

    [TestMethod]
    public void P4_short_secret_and_pipe_name_in_exception_are_scrubbed()
    {
        var dir = Dir();
        using (var log = BrokerLog.Open(dir, _ => true, trustedWriter: _ => true))
        {
            log.Redact("PanelOS.DisplayBroker.abcdef123456", "abc");
            log.Error("connect", new IOException("pipe PanelOS.DisplayBroker.abcdef123456 broke"), 8, "r");
        }
        var text = File.ReadAllText(Path.Combine(dir, BrokerLog.FileName));
        Console.WriteLine("P4 " + text.Trim());
        Assert.IsFalse(text.Contains("abcdef123456"));
    }
}
