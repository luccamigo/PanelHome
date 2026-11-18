using System.Diagnostics;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Development;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

internal sealed class FakePanelHostExecutableLocator : IPanelHostExecutableLocator
{
    public PanelHostExecutable? Executable { get; set; } =
        new(@"C:\PanelOS\PanelHost.exe", PanelHostExecutableSource.ApplicationFolder);

    public int Calls { get; private set; }

    public PanelHostExecutable? Locate()
    {
        Calls++;
        return Executable;
    }
}

/// <summary>
/// Stands in for the operating system. No PanelHost is ever started by a unit
/// test; the fake reports what the manager should do about it.
/// </summary>
internal sealed class FakePanelHostProcessController : IPanelHostProcessController
{
    private int _nextId = 4200;

    public List<PanelHostProcessInfo> Running { get; } = [];

    public List<FakePanelHostProcess> Started { get; } = [];

    public Exception? StartFailure { get; set; }

    public ProcessStartInfo? LastStartInfo { get; private set; }

    public FakePanelHostProcess? Last =>
        Started.Count == 0 ? null : Started[^1];

    public IReadOnlyList<PanelHostProcessInfo> FindRunning(string processName) =>
        [.. Running];

    public IPanelHostProcess Start(ProcessStartInfo startInfo)
    {
        LastStartInfo = startInfo;
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        var process = new FakePanelHostProcess(_nextId++);
        Started.Add(process);
        return process;
    }
}

internal sealed class FakePanelHostProcess : IPanelHostProcess
{
    internal FakePanelHostProcess(int id) => Id = id;

    public event EventHandler<PanelHostOutputEventArgs>? OutputReceived;

    public event EventHandler? Exited;

    public int Id { get; }

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    public bool OutputStarted { get; private set; }

    public bool StopRequested { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Set to false to simulate a process that ignores the stop.</summary>
    public bool StopSucceeds { get; set; } = true;

    public void BeginReadOutput() => OutputStarted = true;

    public Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        StopRequested = true;
        if (StopSucceeds)
        {
            SimulateExit(0);
        }

        return Task.FromResult(StopSucceeds);
    }

    public void SimulateExit(int exitCode)
    {
        if (HasExited)
        {
            return;
        }

        HasExited = true;
        ExitCode = exitCode;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void EmitOutput(PanelHostLogStream stream, string text) =>
        OutputReceived?.Invoke(this, new PanelHostOutputEventArgs(stream, text));

    public void Dispose() => Disposed = true;
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>Scriptable manager for the view-model tests.</summary>
internal sealed class FakePanelHostProcessManager : IPanelHostProcessManager
{
    private PanelHostStatus _status = PanelHostStatus.NotRunning;

    public event EventHandler<PanelHostStatusChangedEventArgs>? StatusChanged;

    public PanelHostStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            StatusChanged?.Invoke(this, new PanelHostStatusChangedEventArgs(value));
        }
    }

    public PanelHostLogBuffer Log { get; } = new();

    public PanelHostTransport Transport { get; set; } = PanelHostTransport.NativeUsb;

    public PanelHostRestartPolicy RestartPolicy { get; set; } =
        PanelHostRestartPolicy.Disabled;

    public PanelHostExecutable? Executable { get; set; }

    public PanelHostOperationResult StartResult { get; set; } =
        PanelHostOperationResult.Ok("iniciado");

    public PanelHostOperationResult StopResult { get; set; } =
        PanelHostOperationResult.Ok("encerrado");

    public PanelHostOperationResult RestartResult { get; set; } =
        PanelHostOperationResult.Ok("reiniciado");

    public int StartCalls { get; private set; }

    public int StopCalls { get; private set; }

    public int RestartCalls { get; private set; }

    public int RefreshCalls { get; private set; }

    public bool Disposed { get; private set; }

    public PanelHostExecutable? LocateExecutable() => Executable;

    public PanelHostStatus Refresh()
    {
        RefreshCalls++;
        return _status;
    }

    public Task<PanelHostOperationResult> StartAsync(
        CancellationToken cancellationToken)
    {
        StartCalls++;
        return Task.FromResult(StartResult);
    }

    public Task<PanelHostOperationResult> StopAsync(
        CancellationToken cancellationToken)
    {
        StopCalls++;
        return Task.FromResult(StopResult);
    }

    public Task<PanelHostOperationResult> RestartAsync(
        CancellationToken cancellationToken)
    {
        RestartCalls++;
        return Task.FromResult(RestartResult);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeAdbLocator : IAdbLocator
{
    public string? Path { get; set; } = @"C:\Android\platform-tools\adb.exe";

    public string? Locate() => Path;
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessRunResult> _results = new();

    public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } =
        [];

    public Exception? Failure { get; set; }

    public ProcessRunResult Default { get; set; } = new(0, string.Empty, string.Empty);

    public void Enqueue(ProcessRunResult result) => _results.Enqueue(result);

    public void EnqueueDevices(string output) =>
        Enqueue(new ProcessRunResult(0, output, string.Empty));

    public Task<ProcessRunResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Calls.Add((executablePath, arguments));
        if (Failure is not null)
        {
            throw Failure;
        }

        if (_results.Count > 0) return Task.FromResult(_results.Dequeue());
        if (arguments.Count >= 2 &&
            arguments[^2] == "reverse" && arguments[^1] == "--list")
        {
            return Task.FromResult(new ProcessRunResult(
                0,
                "R58M12345XY tcp:8765 tcp:8765\nR58M12345XY tcp:8766 tcp:8766\n",
                string.Empty));
        }
        return Task.FromResult(Default);
    }
}

