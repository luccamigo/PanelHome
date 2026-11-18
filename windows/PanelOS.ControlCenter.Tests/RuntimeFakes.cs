using System.Diagnostics;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Management;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.ControlCenter.Tests;

/// <summary>Records the keystrokes an action would have synthesized.</summary>
internal sealed class FakeKeyboardInputService : IKeyboardInputService
{
    private readonly List<KeyboardInputEvent> _events = [];

    public IReadOnlyList<KeyboardInputEvent> Events => _events;

    public int Batches { get; private set; }

    public Exception? Failure { get; set; }

    public string Describe() => string.Join(", ", _events);

    public void Send(IReadOnlyList<KeyboardInputEvent> events)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Batches++;
        _events.AddRange(events);
    }
}

/// <summary>Records what would have been started, without starting it.</summary>
internal sealed class RecordingProcessLauncher : IProcessLauncher
{
    private readonly List<ProcessStartInfo> _started = [];

    public IReadOnlyList<ProcessStartInfo> Started => _started;

    public ProcessStartInfo? Last => _started.Count == 0 ? null : _started[^1];

    public Exception? Failure { get; set; }

    public void Start(ProcessStartInfo startInfo)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        _started.Add(startInfo);
    }
}

internal sealed class FakeFileSystemProbe : IFileSystemProbe
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => Files.Contains(path);

    public bool DirectoryExists(string path) => Directories.Contains(path);
}

internal sealed class RecordingStreamDeckLog : IStreamDeckLog
{
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines => _lines;

    public string Text => string.Join("\n", _lines);

    public void Write(string message) => _lines.Add(message);
}

internal sealed class FakeApplicationPicker : IApplicationPicker
{
    public ApplicationPick? Result { get; set; }

    public int Calls { get; private set; }

    public ApplicationPick? PickApplication()
    {
        Calls++;
        return Result;
    }
}

internal sealed class FakeIconFilePicker : IIconFilePicker
{
    public string? Result { get; set; }

    public string? PickImageFile() => Result;
}

internal sealed class FakeIconImporter : IIconImporter
{
    public string? ImageIconId { get; set; } = "icon-abc123";

    public string? ExecutableIconId { get; set; } = "icon-exe456";

    public string? LastImportedImage { get; private set; }

    public string? LastImportedExecutable { get; private set; }

    public Task<string?> ImportImageAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        LastImportedImage = imagePath;
        return Task.FromResult(ImageIconId);
    }

    public Task<string?> ImportExecutableIconAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        LastImportedExecutable = executablePath;
        return Task.FromResult(ExecutableIconId);
    }

    public string? ResolveIconPath(string? iconId) =>
        iconId is null ? null : $@"C:\PanelOS.Tests\icons\{iconId}.png";
}

internal sealed class FakeConfirmationService : IConfirmationService
{
    public bool Answer { get; set; } = true;

    public int Calls { get; private set; }

    public string? LastMessage { get; private set; }

    public bool Confirm(string title, string message)
    {
        Calls++;
        LastMessage = message;
        return Answer;
    }
}

internal sealed class FakeActionExecutor : IStreamDeckActionExecutor
{
    public StreamDeckActionResult Result { get; set; } =
        StreamDeckActionResult.Ok("Executado no teste.");

    public string? LastButtonId { get; private set; }

    public StreamDeckButton? LastButton { get; private set; }

    public int Calls { get; private set; }

    public Task<StreamDeckActionResult> ExecuteAsync(
        string buttonId,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastButtonId = buttonId;
        return Task.FromResult(Result);
    }

    public Task<StreamDeckActionResult> ExecuteButtonAsync(
        StreamDeckButton button,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastButton = button;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// A management channel under the test's control: it publishes the snapshots
/// the case needs and drops the connection on demand.
/// </summary>
/// <remarks>
/// Shared rather than nested in one test class, because the same fake answers
/// three different questions - what the device view model reports, what the
/// Overview raises as a problem, and what Diagnostics measures.
/// </remarks>
internal sealed class FakeManagementClient : IPanelHostManagementClient
{
    public PanelHostManagementConnectionState ConnectionState { get; private set; }

    public PanelHostSnapshot? Snapshot { get; private set; }

    public event EventHandler<PanelHostManagementSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<PanelHostManagementConnectionChangedEventArgs>? ConnectionChanged;

    public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal void Publish(PanelHostSnapshot snapshot)
    {
        Snapshot = snapshot;
        ConnectionState = PanelHostManagementConnectionState.Connected;
        ConnectionChanged?.Invoke(
            this,
            new PanelHostManagementConnectionChangedEventArgs(ConnectionState));
        SnapshotChanged?.Invoke(
            this,
            new PanelHostManagementSnapshotChangedEventArgs(snapshot));
    }

    internal void Disconnect()
    {
        Snapshot = null;
        ConnectionState = PanelHostManagementConnectionState.Disconnected;
        SnapshotChanged?.Invoke(
            this,
            new PanelHostManagementSnapshotChangedEventArgs(null));
        ConnectionChanged?.Invoke(
            this,
            new PanelHostManagementConnectionChangedEventArgs(ConnectionState));
    }

    public void Dispose()
    {
    }
}
