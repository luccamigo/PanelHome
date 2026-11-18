namespace PanelOS.Windows.Host;

public enum PanelHostExecutableSource
{
    /// <summary>A path the user configured explicitly.</summary>
    Configured,

    /// <summary>Next to the Control Center - the shipped layout.</summary>
    ApplicationFolder,

    /// <summary>A build output found in the repository during development.</summary>
    DevelopmentBuild,
}

public sealed record PanelHostExecutable(
    string Path,
    PanelHostExecutableSource Source);

public interface IPanelHostExecutableLocator
{
    /// <summary>The agent to launch, or null when none was found.</summary>
    PanelHostExecutable? Locate();
}

/// <summary>
/// Finds PanelHost.exe without any machine-specific path baked in.
/// </summary>
/// <remarks>
/// The order encodes the two layouts that matter. Shipped, the agent sits next
/// to the Control Center:
/// <code>
/// PanelOS/
///   PanelOS.ControlCenter.exe
///   PanelHost.exe
/// </code>
/// During development it only exists as a build output, so the locator walks up
/// from the running assembly looking for the repository, then picks the newest
/// PanelHost.exe under windows/PanelHost/bin. An explicitly configured path
/// always wins over both.
/// </remarks>
public sealed class PanelHostExecutableLocator : IPanelHostExecutableLocator
{
    private const string ExecutableName = "PanelHost.exe";
    private const int MaximumParentDirectories = 8;

    private readonly Func<string?> _configuredPath;
    private readonly string _applicationDirectory;

    public PanelHostExecutableLocator(
        Func<string?>? configuredPath = null,
        string? applicationDirectory = null)
    {
        _configuredPath = configuredPath ?? (static () => null);
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
    }

    public PanelHostExecutable? Locate()
    {
        var configured = _configuredPath()?.Trim();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
        {
            return new PanelHostExecutable(
                Path.GetFullPath(configured),
                PanelHostExecutableSource.Configured);
        }

        var beside = Path.Combine(_applicationDirectory, ExecutableName);
        if (File.Exists(beside))
        {
            return new PanelHostExecutable(
                Path.GetFullPath(beside),
                PanelHostExecutableSource.ApplicationFolder);
        }

        var development = FindDevelopmentBuild();
        return development is null
            ? null
            : new PanelHostExecutable(
                development,
                PanelHostExecutableSource.DevelopmentBuild);
    }

    private string? FindDevelopmentBuild()
    {
        var projectDirectory = FindPanelHostProjectDirectory();
        if (projectDirectory is null)
        {
            return null;
        }

        var binDirectory = Path.Combine(projectDirectory, "bin");
        if (!Directory.Exists(binDirectory))
        {
            return null;
        }

        try
        {
            // Newest build wins, whichever configuration produced it.
            return Directory
                .EnumerateFiles(
                    binDirectory,
                    ExecutableName,
                    SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
                ?.FullName;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? FindPanelHostProjectDirectory()
    {
        var directory = new DirectoryInfo(_applicationDirectory);
        for (var depth = 0; depth < MaximumParentDirectories && directory is not null;
            depth++)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "windows",
                "PanelHost");
            if (File.Exists(Path.Combine(candidate, "PanelHost.csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
