using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace PanelDisplayBroker;

/// <summary>
/// The elevated broker's own log. It runs hidden and a failure used to leave no trace.
///
/// The file lives under %ProgramData% and not in the user profile on purpose: this process is
/// elevated, so a log path that a medium-integrity process controls (a junction in the profile)
/// would turn "append a line" into "append to any file". The directory is created by the broker with
/// a protected ACL (SYSTEM and Administrators full, Users read), an existing directory is accepted
/// only when it is not a reparse point, has a trusted owner and grants write access only to trusted principals;
/// the same is required of the parent. If any check fails the broker logs to stderr only.
///
/// Never log the nonce or other secrets: callers register them with <see cref="Redact"/> and every
/// line is scrubbed as well.
/// </summary>
internal sealed class BrokerLog : IDisposable
{
    internal const string FileName = "panelbroker.log";

    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    private readonly object _gate = new();
    private readonly List<string> _secrets = [];
    private StreamWriter? _writer;

    private BrokerLog(StreamWriter? writer) => _writer = writer;

    /// <summary>A log that writes nowhere.</summary>
    internal static BrokerLog Null => new(null);

    internal bool IsWritingToFile => _writer is not null;

    internal static string DefaultDirectory => Path.GetDirectoryName(
        PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerExitCodes.LogFilePath)!;

    internal static bool IsTrustedOwner(SecurityIdentifier owner) =>
        owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
        owner.Equals(TrustedInstaller);

    internal static BrokerLog Open(
        string? directory = null,
        Func<SecurityIdentifier, bool>? trustedOwner = null,
        Action<string>? refused = null,
        Func<SecurityIdentifier, bool>? trustedWriter = null)
    {
        directory ??= DefaultDirectory;
        trustedOwner ??= IsTrustedOwner;
        try
        {
            EnsureTrustedDirectory(directory, trustedOwner, trustedWriter ?? IsTrustedOwner);
            var path = Path.Combine(directory, FileName);
            Roll(path);
            // Never append to an existing inode: it may have been planted as a hardlink.
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

            return new BrokerLog(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or SecurityException or
            InvalidOperationException or ArgumentException or NotSupportedException)
        {
            refused?.Invoke(
                $"[PanelDisplayBroker] Broker log disabled: {exception.GetType().Name}: {exception.Message}");
            return Null;
        }
    }

    /// <summary>Directory (and its parent) must exist as plain directories owned by a trusted principal.</summary>
    internal static void EnsureTrustedDirectory(
        string directory, Func<SecurityIdentifier, bool> trustedOwner,
        Func<SecurityIdentifier, bool>? trustedWriter = null)
    {
        var full = Path.GetFullPath(directory);
        var parent = Path.GetDirectoryName(full)
            ?? throw new IOException("The broker log directory has no parent.");
        EnsureOne(parent, trustedOwner, trustedWriter ?? IsTrustedOwner);
        EnsureOne(full, trustedOwner, trustedWriter ?? IsTrustedOwner);
    }

    private static void EnsureOne(
        string path, Func<SecurityIdentifier, bool> trustedOwner,
        Func<SecurityIdentifier, bool> trustedWriter)
    {
        if (!Directory.Exists(path))
        {
            var createdSecurity = new DirectorySecurity();
            createdSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            createdSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            createdSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            createdSecurity.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(createdSecurity);
        }

        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"'{path}' is a reparse point.");
        }

        var security = info.GetAccessControl();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !trustedOwner(owner))
        {
            throw new UnauthorizedAccessException($"'{path}' is not owned by a trusted principal.");
        }

        EnsureTrustedAcl(security, trustedWriter);
    }

    internal static void EnsureTrustedAcl(DirectorySecurity security, Func<SecurityIdentifier, bool> trustedWriter)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            throw new UnauthorizedAccessException("The broker log directory has an unrestricted ACL.");

        const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000; // GENERIC_WRITE / GENERIC_ALL
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & mutation) != 0 &&
                !trustedWriter((SecurityIdentifier)rule.IdentityReference))
                throw new UnauthorizedAccessException("The broker log directory grants write access to an untrusted principal.");
        }
    }

    private static void Roll(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists)
        {
            // Rename the directory entry; never open/write the previous file or its target.
            File.Move(path, path + ".1", overwrite: true);
        }
    }

    /// <summary>Text that must never reach the file (nonce, pipe name).</summary>
    internal void Redact(params string?[] secrets)
    {
        lock (_gate)
        {
            foreach (var secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 6)
                {
                    _secrets.Add(secret);
                }
            }
        }
    }

    internal void Info(string stage, string message) => Write("INFO ", stage, message);

    internal void Error(string stage, Exception exception, int exitCode, string reason)
    {
        var text = $"{reason}; {exception.GetType().FullName}: {FirstLine(exception.Message)} " +
            $"(HRESULT 0x{exception.HResult:X8}); exit code {exitCode}";
        if (exitCode == PanelOS.DisplayBroker.Protocol.PanelDisplayBrokerExitCodes.Unexpected)
        {
            var detail = exception.ToString();
            text += Environment.NewLine + (detail.Length > 4096 ? detail[..4096] : detail);
        }

        Write("ERROR", stage, text);
    }

    private void Write(string level, string stage, string message)
    {
        var line =
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z pid={Environment.ProcessId} {level} [{stage}] {message}";
        lock (_gate)
        {
            foreach (var secret in _secrets)
            {
                line = line.Replace(secret, "<redacted>", StringComparison.Ordinal);
            }

            try
            {
                _writer?.WriteLine(line);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // Logging must never decide how the broker ends.
            }
        }
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
