using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelDisplayBroker;
using PanelOS.DisplayBroker.Protocol;

namespace PanelHost.Tests;

/// <summary>
/// Bench 02:29 and 02:32: the elevated broker connected to the host pipe and left within 120-270 ms
/// with exit code 0, so the host saw "Pipe is broken". Cause: the broker's pipe client used
/// PipeOptions.CurrentUserOnly, whose owner check compares the pipe owner (the user SID, the host
/// is not elevated) with WindowsIdentity.Owner (BUILTIN\Administrators for an elevated token); the
/// refusal was swallowed and the process ended with 0, with no log. The PAN-1 run on this PC had
/// already logged the same "not owned by the current user" exception for elevated client to
/// normal server. These tests cover the pieces that can be checked without elevation.
/// </summary>
[TestClass]
public sealed class PanelDisplayBrokerRootCauseTests
{
    private static readonly SecurityIdentifier User = new("S-1-5-21-111-222-333-1000");
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-111-222-333-1001");
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    // ---- pipe owner -------------------------------------------------------------------------

    [TestMethod]
    public void An_elevated_broker_accepts_a_pipe_owned_by_the_user()
    {
        // Elevated token: User = the user SID, Owner = Administrators. The host (not elevated) owns the
        // pipe as the user. .NET's own check (owner == Owner) refuses this; ours must accept it.
        Assert.IsTrue(BrokerPipeOwner.IsTrusted(owner: User, user: User, tokenOwner: Administrators));
    }

    [TestMethod]
    public void An_elevated_host_pipe_owned_by_administrators_is_accepted_too()
    {
        Assert.IsTrue(BrokerPipeOwner.IsTrusted(owner: Administrators, user: User, tokenOwner: Administrators));
    }

    [TestMethod]
    public void A_pipe_owned_by_someone_else_or_without_an_owner_is_refused()
    {
        Assert.IsFalse(BrokerPipeOwner.IsTrusted(owner: OtherUser, user: User, tokenOwner: Administrators));
        Assert.IsFalse(BrokerPipeOwner.IsTrusted(owner: null, user: User, tokenOwner: Administrators));
        Assert.IsFalse(BrokerPipeOwner.IsTrusted(owner: User, user: null, tokenOwner: null));
    }

    [TestMethod]
    public async Task A_real_pipe_made_like_the_hosts_passes_the_brokers_check_and_the_check_reads_the_real_owner()
    {
        var name = $"PanelOS.DisplayBroker.Test.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly,
            4096, 64 * 1024);
        // The broker's client options: no CurrentUserOnly; BrokerPipeOwner.Verify does the check.
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await waiting;

        BrokerPipeOwner.Verify(client);

        var owner = (SecurityIdentifier)client.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        Assert.IsFalse(BrokerPipeOwner.IsTrusted(owner, OtherUser, OtherUser),
            "the real owner is compared, not a constant");
    }

    /// <summary>
    /// The broker's Program.cs cannot be run end to end without elevation, so its wiring is guarded by
    /// reading the source: the client must not use CurrentUserOnly again and must verify the owner.
    /// </summary>
    [TestMethod]
    public void The_broker_pipe_client_does_not_use_CurrentUserOnly_and_verifies_the_owner()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "PanelDisplayBroker", "Program.cs"));
        if (!File.Exists(source)) Assert.Inconclusive("Program.cs was not found beside the test project.");
        var code = string.Join('\n', File.ReadAllLines(source)
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var start = code.IndexOf("new NamedPipeClientStream(", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, "the pipe client was not found");
        var clientConstruction = code[start..code.IndexOf("))", start, StringComparison.Ordinal)];
        Assert.IsFalse(clientConstruction.Contains("CurrentUserOnly"),
            "an elevated client with CurrentUserOnly is refused by .NET (owner != WindowsIdentity.Owner)");
        StringAssert.Contains(code, "BrokerPipeOwner.Verify(pipe)");
    }

    // ---- exit codes -------------------------------------------------------------------------

    [DataTestMethod]
    [DataRow(typeof(UnauthorizedAccessException), "Connect", PanelDisplayBrokerExitCodes.PipeOwnerRejected)]
    [DataRow(typeof(UnauthorizedAccessException), "Handshake", PanelDisplayBrokerExitCodes.PipeOwnerRejected)]
    [DataRow(typeof(TimeoutException), "Connect", PanelDisplayBrokerExitCodes.PipeConnectFailed)]
    [DataRow(typeof(IOException), "Connect", PanelDisplayBrokerExitCodes.PipeConnectFailed)]
    [DataRow(typeof(IOException), "Handshake", PanelDisplayBrokerExitCodes.HandshakeFailed)]
    [DataRow(typeof(JsonException), "Handshake", PanelDisplayBrokerExitCodes.ProtocolError)]
    [DataRow(typeof(InvalidDataException), "Serve", PanelDisplayBrokerExitCodes.ProtocolError)]
    [DataRow(typeof(InvalidOperationException), "Serve", PanelDisplayBrokerExitCodes.Unexpected)]
    // Once the handshake is accepted, an I/O error is the host closing the lease: an orderly end.
    [DataRow(typeof(IOException), "Serve", PanelDisplayBrokerExitCodes.Ok)]
    public void Failures_get_their_own_exit_code(Type exceptionType, string stageName, int expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "synthetic")!;

        var (code, reason) = BrokerExit.Classify(exception, Enum.Parse<BrokerStage>(stageName));

        Assert.AreEqual(expected, code);
        Assert.IsFalse(string.IsNullOrWhiteSpace(reason));
    }

    [TestMethod]
    public void Exit_codes_are_distinct_and_described()
    {
        int[] codes =
        [
            PanelDisplayBrokerExitCodes.InvalidArguments, PanelDisplayBrokerExitCodes.NotElevated,
            PanelDisplayBrokerExitCodes.AuthenticationFailed, PanelDisplayBrokerExitCodes.ParentUnavailable,
            PanelDisplayBrokerExitCodes.ShutdownTimeout, PanelDisplayBrokerExitCodes.RequestTimeout,
            PanelDisplayBrokerExitCodes.PipeConnectFailed, PanelDisplayBrokerExitCodes.PipeOwnerRejected,
            PanelDisplayBrokerExitCodes.HandshakeFailed, PanelDisplayBrokerExitCodes.ProtocolError,
            PanelDisplayBrokerExitCodes.FrameMailboxUnavailable, PanelDisplayBrokerExitCodes.Unexpected,
        ];
        Assert.AreEqual(codes.Length, codes.Distinct().Count());
        Assert.IsFalse(codes.Contains(PanelDisplayBrokerExitCodes.Ok), "0 is only the orderly end");
        foreach (var code in codes)
        {
            Assert.IsFalse(PanelDisplayBrokerExitCodes.Describe(code).StartsWith("unknown"));
        }

        StringAssert.StartsWith(PanelDisplayBrokerExitCodes.Describe(-532462766), "unknown");
        // The watchdog codes the broker already used must match the shared table.
        Assert.AreEqual(PanelDisplayBrokerExitCodes.RequestTimeout, BrokerWatchdog.RequestTimeoutExitCode);
        Assert.AreEqual(PanelDisplayBrokerExitCodes.ShutdownTimeout, BrokerWatchdog.ShutdownTimeoutExitCode);
    }

    // ---- broker log -------------------------------------------------------------------------

    private static string NewTestDirectory()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PanelOS.BrokerLogTests", Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(parent, "Broker");
        Directory.CreateDirectory(directory);
        using var identity = WindowsIdentity.GetCurrent();
        foreach (var path in new[] { parent, directory })
        {
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { identity.User!, Administrators, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(acl);
        }
        return directory;
    }

    private static bool CurrentUserOwnsIt(SecurityIdentifier owner)
    {
        using var identity = WindowsIdentity.GetCurrent();
        return owner.Equals(identity.User) || owner.Equals(identity.Owner);
    }

    [TestMethod]
    public void The_log_records_the_stage_the_cause_and_the_exit_code_and_never_the_nonce()
    {
        var directory = NewTestDirectory();
        const string nonce = "0123456789ABCDEF0123456789ABCDEF";
        using (var log = BrokerLog.Open(directory, CurrentUserOwnsIt, trustedWriter: sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid)))
        {
            Assert.IsTrue(log.IsWritingToFile);
            log.Redact(nonce);
            log.Info("start", $"hello with {nonce}");
            log.Error(
                "connect",
                new UnauthorizedAccessException($"Could not connect; token {nonce}"),
                PanelDisplayBrokerExitCodes.PipeOwnerRejected,
                "access denied or the pipe owner was rejected");
        }

        var text = File.ReadAllText(Path.Combine(directory, BrokerLog.FileName));
        StringAssert.Contains(text, "[connect]");
        StringAssert.Contains(text, "UnauthorizedAccessException");
        StringAssert.Contains(text, "exit code 9");
        StringAssert.Contains(text, $"pid={Environment.ProcessId}");
        Assert.IsFalse(text.Contains(nonce), "the nonce must never reach the file");
        StringAssert.Contains(text, "<redacted>");
    }

    [TestMethod]
    public void An_unexpected_exception_keeps_its_stack_in_the_log()
    {
        var directory = NewTestDirectory();
        using (var log = BrokerLog.Open(directory, CurrentUserOwnsIt, trustedWriter: sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid)))
        {
            try { throw new InvalidOperationException("boom"); }
            catch (InvalidOperationException exception)
            {
                log.Error("serve", exception, PanelDisplayBrokerExitCodes.Unexpected, "unexpected exception");
            }
        }

        var text = File.ReadAllText(Path.Combine(directory, BrokerLog.FileName));
        StringAssert.Contains(text, "boom");
        StringAssert.Contains(text, "at PanelHost.Tests.PanelDisplayBrokerRootCauseTests");
    }

    [TestMethod]
    public void A_directory_that_is_a_junction_is_refused_so_the_elevated_broker_cannot_be_redirected()
    {
        var real = NewTestDirectory();
        var link = Path.Combine(Path.GetDirectoryName(real)!, Guid.NewGuid().ToString("N"));
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        made.WaitForExit();
        if (made.ExitCode != 0 || !Directory.Exists(link))
        {
            Assert.Inconclusive("Could not create a junction on this machine.");
        }

        string? message = null;
        using var log = BrokerLog.Open(link, _ => true, text => message = text, _ => true);

        Assert.IsFalse(log.IsWritingToFile);
        StringAssert.Contains(message, "reparse point");
        Assert.IsFalse(File.Exists(Path.Combine(real, BrokerLog.FileName)), "nothing is written through the link");
        Directory.Delete(link);
    }

    [TestMethod]
    public void A_directory_owned_by_an_untrusted_principal_is_refused()
    {
        var directory = NewTestDirectory();
        string? message = null;

        using var log = BrokerLog.Open(directory, _ => false, text => message = text);

        Assert.IsFalse(log.IsWritingToFile);
        StringAssert.Contains(message, "trusted principal");
        Assert.IsFalse(File.Exists(Path.Combine(directory, BrokerLog.FileName)));
    }

    [TestMethod]
    public void Only_administrators_system_and_trusted_installer_own_the_real_log_directory()
    {
        Assert.IsTrue(BrokerLog.IsTrustedOwner(Administrators));
        Assert.IsTrue(BrokerLog.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.IsTrue(BrokerLog.IsTrustedOwner(
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")));
        Assert.IsFalse(BrokerLog.IsTrustedOwner(User));
    }

    [TestMethod]
    public void A_missing_directory_is_created_with_a_protected_acl_that_only_lets_users_read()
    {
        var parent = NewTestDirectory();
        var directory = Path.Combine(parent, "Broker");

        BrokerLog.EnsureTrustedDirectory(directory, CurrentUserOwnsIt, sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid));

        var security = new DirectoryInfo(directory).GetAccessControl();
        Assert.IsTrue(security.AreAccessRulesProtected, "no inherited rules");
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        bool Has(WellKnownSidType sid, FileSystemRights rights) => rules.Any(rule =>
            rule.IdentityReference.Equals(new SecurityIdentifier(sid, null)) &&
            rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & rights) == rights);
        Assert.IsTrue(Has(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl));
        Assert.IsTrue(Has(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl));
        Assert.IsTrue(Has(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute));
        Assert.IsFalse(rules.Any(rule =>
            rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)) &&
            (rule.FileSystemRights & FileSystemRights.WriteData) != 0), "Users cannot write");
    }

    [TestMethod]
    public void A_log_above_the_limit_is_rolled_over_before_appending()
    {
        var directory = NewTestDirectory();
        var path = Path.Combine(directory, BrokerLog.FileName);
        File.WriteAllText(path, new string('x', (256 * 1024) + 10));

        using (var log = BrokerLog.Open(directory, CurrentUserOwnsIt, trustedWriter: sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid)))
        {
            log.Info("start", "after the roll");
        }

        Assert.IsTrue(File.Exists(path + ".1"));
        Assert.IsTrue(new FileInfo(path + ".1").Length > (256 * 1024));
        StringAssert.Contains(File.ReadAllText(path), "after the roll");
        Assert.IsTrue(new FileInfo(path).Length < 1024);
    }

    [TestMethod]
    public void A_log_that_refused_to_open_never_throws_when_used()
    {
        using var log = BrokerLog.Open(NewTestDirectory(), _ => false);
        log.Info("start", "ignored");
        log.Error("connect", new IOException("x"), PanelDisplayBrokerExitCodes.PipeConnectFailed, "reason");
        log.Redact("secretsecret");
        Assert.IsFalse(log.IsWritingToFile);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void A_user_writable_parent_or_log_directory_is_refused(bool changeParent)
    {
        var directory = NewTestDirectory();
        var changed = new DirectoryInfo(changeParent ? Path.GetDirectoryName(directory)! : directory);
        var acl = changed.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Write, AccessControlType.Allow));
        changed.SetAccessControl(acl);
        using var log = BrokerLog.Open(directory, CurrentUserOwnsIt,
            trustedWriter: sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid));
        Assert.IsFalse(log.IsWritingToFile);
        Assert.IsFalse(File.Exists(Path.Combine(directory, BrokerLog.FileName)));
    }

    [TestMethod]
    public void Even_a_small_existing_log_is_renamed_and_a_new_file_is_created()
    {
        var directory = NewTestDirectory();
        var path = Path.Combine(directory, BrokerLog.FileName);
        File.WriteAllText(path, "old log");
        using (var log = BrokerLog.Open(directory, CurrentUserOwnsIt,
            trustedWriter: sid => BrokerLog.IsTrustedOwner(sid) || CurrentUserOwnsIt(sid)))
            log.Info("test", "new log");
        Assert.AreEqual("old log", File.ReadAllText(path + ".1"));
        Assert.IsFalse(File.ReadAllText(path).Contains("old log"));
        StringAssert.Contains(File.ReadAllText(path), "new log");
    }

    [TestMethod]
    [DataRow("GW")]
    [DataRow("GA")]
    public void An_untrusted_generic_write_or_all_acl_is_refused(string right)
    {
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm($"D:(A;;{right};;;BU)");
        Assert.ThrowsException<UnauthorizedAccessException>(() => BrokerLog.EnsureTrustedAcl(acl, BrokerLog.IsTrustedOwner));
    }

    // ---- the real executable, without elevation ---------------------------------------------

    private static string? FindBrokerExecutable()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PanelDisplayBroker.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "PanelDisplayBroker", "bin", "Debug", "net8.0-windows10.0.19041.0", "PanelDisplayBroker.exe")),
        })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static int RunBroker(string executable, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        })!;
        process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(15000), "the broker must exit by itself");
        return process.ExitCode;
    }

    [TestMethod]
    public void The_real_broker_reports_invalid_arguments_and_missing_elevation_with_their_own_codes_and_creates_no_log_directory()
    {
        var executable = FindBrokerExecutable();
        if (executable is null) Assert.Inconclusive("PanelDisplayBroker.exe was not found beside the tests.");
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Assert.Inconclusive("The test process is elevated; the not-elevated path cannot be reached.");
            }
        }

        var logDirectory = BrokerLog.DefaultDirectory;
        var existedBefore = Directory.Exists(logDirectory);

        var invalid = RunBroker(executable!, "--bogus");
        var notElevated = RunBroker(executable!,
            "--pipe PanelOS.Test --frame-map PanelOS.Test --nonce 0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF --parent-pid 4 --parent-start-utc-ticks 1");

        Assert.AreEqual(PanelDisplayBrokerExitCodes.InvalidArguments, invalid);
        Assert.AreEqual(PanelDisplayBrokerExitCodes.NotElevated, notElevated);
        if (!existedBefore)
        {
            Assert.IsFalse(Directory.Exists(logDirectory),
                "a normal process must not create the elevated broker's log directory");
        }
    }
}
