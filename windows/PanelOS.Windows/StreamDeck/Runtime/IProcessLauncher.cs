using System.Diagnostics;

namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Starts a process from a fully prepared <see cref="ProcessStartInfo"/>.
/// </summary>
/// <remarks>
/// The whole start info crosses this boundary on purpose: a test can then
/// assert exactly what would have been launched - that the file name is the
/// configured executable and never cmd.exe or powershell.exe, and that
/// UseShellExecute is what the action requires.
/// </remarks>
public interface IProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class WindowsProcessLauncher : IProcessLauncher
{
    public void Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using var process = Process.Start(startInfo);
    }
}

/// <summary>
/// Filesystem questions the executor asks before launching. Abstracted so the
/// "executable missing" path is testable without creating files.
/// </summary>
public interface IFileSystemProbe
{
    bool FileExists(string path);

    bool DirectoryExists(string path);
}

public sealed class WindowsFileSystemProbe : IFileSystemProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);
}
