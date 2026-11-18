namespace PanelOS.Windows.Development;

public interface IAdbLocator
{
    /// <summary>Full path of adb.exe, or null when it is not installed.</summary>
    string? Locate();
}

/// <summary>
/// Looks for the Android platform tools in the usual places. Nothing here is
/// required for the Control Center to work; a machine without the Android SDK
/// simply reports that ADB is missing.
/// </summary>
public sealed class AdbLocator : IAdbLocator
{
    private const string ExecutableName = "adb.exe";

    private readonly Func<string?> _configuredPath;

    public AdbLocator(Func<string?>? configuredPath = null) =>
        _configuredPath = configuredPath ?? (static () => null);

    public string? Locate()
    {
        var configured = _configuredPath()?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (var candidate in Candidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // The default Android Studio installation.
        yield return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "Android",
            "Sdk",
            "platform-tools",
            ExecutableName);

        foreach (var variable in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(root))
            {
                yield return Path.Combine(root, "platform-tools", ExecutableName);
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
        {
            var candidate = TryCombine(directory, ExecutableName);
            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }

    private static string? TryCombine(string directory, string fileName)
    {
        try
        {
            return Path.Combine(directory, fileName);
        }
        catch (ArgumentException)
        {
            // A malformed PATH entry is not worth failing over.
            return null;
        }
    }
}
