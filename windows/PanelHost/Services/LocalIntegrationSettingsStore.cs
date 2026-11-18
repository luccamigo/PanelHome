using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using PanelHost.Security.Wifi;

namespace PanelHost.Services;

internal sealed record LocalIntegrationSettings(bool Enabled = false, int Port = 8777,
    string? KeyHash = null, string? KeySuffix = null, long Generation = 0, int PermissionTimeoutSeconds = 60);

internal sealed class LocalIntegrationSettingsStore(string? directory = null)
{
    private readonly string _directory = directory ?? Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "PanelOS", "Integrations");

    internal LocalIntegrationSettings Load()
    {
        var path = Path.Combine(_directory, "settings.dpapi");
        if (!File.Exists(path)) return new();
        var bytes = WifiDpapi.Transform(File.ReadAllBytes(path), false);
        try
        {
            var settings = JsonSerializer.Deserialize<LocalIntegrationSettings>(bytes) ?? throw new InvalidDataException();
            if (!ValidPort(settings.Port) || settings.PermissionTimeoutSeconds is not (30 or 60 or 90 or 120) ||
                settings.KeyHash is not null && Convert.FromBase64String(settings.KeyHash).Length != 32) throw new InvalidDataException();
            return settings;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal void Save(LocalIntegrationSettings settings)
    {
        Directory.CreateDirectory(_directory);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new CryptographicException();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(acl);
        var path = Path.Combine(_directory, "settings.dpapi");
        var temp = path + "." + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        try
        {
            File.WriteAllBytes(temp, WifiDpapi.Transform(bytes, true));
            File.Move(temp, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    internal static bool ValidPort(int port) => port is >= 1024 and <= 65535 &&
        port is not (>= 8765 and <= 8767) and not (40001 or 40002);
}

internal static class LocalIntegrationKey
{
    internal static string Create() => "panelos_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static byte[] Hash(string value) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
    internal static bool Matches(string? presented, string? hash)
    {
        var candidate = Hash(presented ?? "");
        var expected = hash is null ? new byte[32] : Convert.FromBase64String(hash);
        var equal = CryptographicOperations.FixedTimeEquals(candidate, expected);
        var format = presented?.Length == 51 && presented.StartsWith("panelos_", StringComparison.Ordinal) &&
            presented.AsSpan(8).ToArray().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        return equal & format & (hash is not null);
    }
}
