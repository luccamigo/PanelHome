using System.Diagnostics;
using PanelOS.Windows.Development;

namespace PanelOS.Windows.Host;

/// <summary>
/// Starts, stops and watches the PanelHost agent.
/// </summary>
/// <remarks>
/// <para>
/// The agent is launched directly through <see cref="ProcessStartInfo"/>. No
/// shell, no PowerShell and no start-panel-dev.ps1: that script stays as the
/// development entry point, and the Control Center does not depend on it.
/// </para>
/// <para>
/// Ownership is the rule that shapes the rest. An agent this manager started is
/// tracked, its output is captured and it can be stopped. An agent the user
/// started elsewhere is only reported - never adopted, never stopped, never
/// killed when the Control Center exits.
/// </para>
/// </remarks>
public sealed class PanelHostProcessManager : IPanelHostProcessManager
{
    /// <summary>Image name Windows reports for the agent.</summary>
    public const string ProcessName = "PanelHost";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IPanelHostExecutableLocator _locator;
    private readonly IPanelHostProcessController _controller;
    private readonly TimeProvider _time;
    private readonly IDevelopmentSessionTokenProvider? _developmentSessionToken;
    private string? _lastErrorLine;

    /// <inheritdoc />
    public PanelHostTransport Transport { get; set; } = PanelHostTransport.NativeUsb;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _restarts = new();

    private IPanelHostProcess? _owned;
    private DateTimeOffset? _ownedStartedAt;
    private string? _ownedPath;
    private bool _stopRequested;
    private bool _disposed;
    private PanelHostStatus _status = PanelHostStatus.NotRunning;

    public PanelHostProcessManager(
        IPanelHostExecutableLocator locator,
        IPanelHostProcessController controller,
        PanelHostLogBuffer? log = null,
        TimeProvider? timeProvider = null,
        IDevelopmentSessionTokenProvider? developmentSessionToken = null)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(controller);
        _locator = locator;
        _controller = controller;
        Log = log ?? new PanelHostLogBuffer();
        _time = timeProvider ?? TimeProvider.System;
        _developmentSessionToken = developmentSessionToken;
    }

    public event EventHandler<PanelHostStatusChangedEventArgs>? StatusChanged;

    public PanelHostStatus Status => Volatile.Read(ref _status);

    public PanelHostLogBuffer Log { get; }

    public PanelHostRestartPolicy RestartPolicy { get; set; } =
        PanelHostRestartPolicy.Disabled;

    public PanelHostExecutable? LocateExecutable() => _locator.Locate();

    public PanelHostStatus Refresh()
    {
        var owned = _owned;
        if (owned is { HasExited: false })
        {
            return Status;
        }

        var external = _controller.FindRunning(ProcessName).FirstOrDefault();
        if (external is not null)
        {
            return Publish(new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = external.Id,
                StartedAt = external.StartedAt,
                ExecutablePath = external.ExecutablePath,
                OwnedByControlCenter = false,
                Detail = "Iniciado fora do Control Center.",
            });
        }

        // An unexpected exit keeps its explanation until something starts again.
        var current = Status;
        if (current.State == PanelHostProcessState.Error)
        {
            return current;
        }

        return Publish(PanelHostStatus.NotRunning with
        {
            LastExitCode = current.LastExitCode,
            LastRunDuration = current.LastRunDuration,
        });
    }

    public async Task<PanelHostOperationResult> StartAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return StartCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PanelHostOperationResult> StopAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PanelHostOperationResult> RestartAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_owned is null)
            {
                var running = Refresh();
                if (running.IsExternal)
                {
                    return NotOwned("reiniciar");
                }
            }
            else
            {
                var stop = await StopCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!stop.Success)
                {
                    return stop;
                }
            }

            return StartCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Releasing the handle does not end the agent: keeping PanelHost alive
        // when the window closes is the default, and stopping it is an explicit
        // decision the shell makes before disposing.
        var owned = _owned;
        _owned = null;
        if (owned is not null)
        {
            owned.Exited -= OnOwnedProcessExited;
            owned.OutputReceived -= OnOwnedProcessOutput;
            owned.Dispose();
        }

        _gate.Dispose();
    }

    private PanelHostOperationResult StartCore()
    {
        if (_owned is { HasExited: false })
        {
            return PanelHostOperationResult.Error(
                PanelHostErrorCodes.AlreadyRunning,
                "O PanelHost já está em execução.");
        }

        var external = _controller.FindRunning(ProcessName).FirstOrDefault();
        if (external is not null)
        {
            Publish(new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = external.Id,
                StartedAt = external.StartedAt,
                ExecutablePath = external.ExecutablePath,
                OwnedByControlCenter = false,
                Detail = "Iniciado fora do Control Center.",
            });
            return PanelHostOperationResult.Error(
                PanelHostErrorCodes.AlreadyRunning,
                "Já existe um PanelHost em execução, iniciado fora do Control " +
                "Center.");
        }

        var executable = _locator.Locate();
        if (executable is null)
        {
            var message =
                "PanelHost.exe não foi encontrado. Compile o projeto ou " +
                "informe o caminho em Configurações.";
            Publish(PanelHostStatus.NotRunning with
            {
                State = PanelHostProcessState.Error,
                Detail = message,
            });
            return PanelHostOperationResult.Error(
                PanelHostErrorCodes.ExecutableNotFound,
                message);
        }

        Publish(new PanelHostStatus
        {
            State = PanelHostProcessState.Starting,
            ExecutablePath = executable.Path,
            OwnedByControlCenter = true,
        });

        try
        {
            var process = _controller.Start(CreateStartInfo(executable.Path));
            _owned = process;
            _ownedPath = executable.Path;
            _ownedStartedAt = _time.GetLocalNow();
            _stopRequested = false;
            _lastErrorLine = null;

            process.Exited += OnOwnedProcessExited;
            process.OutputReceived += OnOwnedProcessOutput;
            process.BeginReadOutput();

            Log.Append(
                PanelHostLogStream.Manager,
                $"PanelHost iniciado (PID {process.Id}) a partir de " +
                $"{executable.Source}.");
            Publish(new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = process.Id,
                StartedAt = _ownedStartedAt,
                ExecutablePath = executable.Path,
                OwnedByControlCenter = true,
            });
            return PanelHostOperationResult.Ok("PanelHost iniciado.");
        }
        catch (Exception exception)
        {
            var message = $"Não foi possível iniciar o PanelHost: {exception.Message}";
            Log.Append(PanelHostLogStream.Manager, message);
            Publish(PanelHostStatus.NotRunning with
            {
                State = PanelHostProcessState.Error,
                ExecutablePath = executable.Path,
                Detail = message,
            });
            return PanelHostOperationResult.Error(
                PanelHostErrorCodes.StartFailed,
                message);
        }
    }

    private async Task<PanelHostOperationResult> StopCoreAsync(
        CancellationToken cancellationToken)
    {
        var owned = _owned;
        if (owned is null || owned.HasExited)
        {
            var current = Refresh();
            return current.IsExternal
                ? NotOwned("parar")
                : PanelHostOperationResult.Error(
                    PanelHostErrorCodes.NotRunning,
                    "O PanelHost não está em execução.");
        }

        Publish(Status with { State = PanelHostProcessState.Stopping });
        _stopRequested = true;

        var stopped = await owned
            .StopAsync(StopTimeout, cancellationToken)
            .ConfigureAwait(false);
        DetachOwned();

        if (!stopped)
        {
            var message = "O PanelHost não respondeu ao pedido de encerramento.";
            Publish(PanelHostStatus.NotRunning with
            {
                State = PanelHostProcessState.Error,
                Detail = message,
            });
            return PanelHostOperationResult.Error(
                PanelHostErrorCodes.StopFailed,
                message);
        }

        Log.Append(PanelHostLogStream.Manager, "PanelHost encerrado.");
        Publish(PanelHostStatus.NotRunning);
        return PanelHostOperationResult.Ok("PanelHost encerrado.");
    }

    private static PanelHostOperationResult NotOwned(string verb) =>
        PanelHostOperationResult.Error(
            PanelHostErrorCodes.NotOwned,
            $"Este PanelHost foi iniciado fora do Control Center; " +
            $"{verb} um processo que não é nosso não é oferecido.");

    private ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,

        // Direct CreateProcess with the pipes attached: no shell parses this,
        // and the agent's console output becomes the log the UI shows.
        //
        // The stream encodings are left at their defaults on purpose. The agent
        // is a console application that writes through the OEM code page, and
        // so does the default reader here; pinning UTF-8 would turn every
        // accented word into a replacement character. Reading it exactly on a
        // host whose console page differs would need a code-page provider,
        // which is not worth a dependency for log cosmetics.
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Launched with no arguments the agent picks its development carrier,
        // which then needs a credential and an adb reverse pair before a tablet
        // can reach it. The carrier is therefore always stated.
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add(Transport.ToCommandLineValue());

        // Only the development carrier is gated on a credential, and the agent
        // refuses to open it without one - so the launcher mints it rather than
        // forwarding whatever an earlier step happened to leave behind. The
        // product carrier needs none, and is not given one.
        if (Transport == PanelHostTransport.AdbTcp &&
            _developmentSessionToken is { } session)
        {
            startInfo.Environment[DevelopmentSessionToken.HostEnvironmentVariable] =
                session.EnsureToken();
        }

        return startInfo;
    }

    private void OnOwnedProcessOutput(object? sender, PanelHostOutputEventArgs e)
    {
        // The agent explains itself on stderr before it exits. Keeping the last
        // line means the failure can be reported as what went wrong rather than
        // as a bare exit code with the reason buried in the log.
        if (e.Stream == PanelHostLogStream.StandardError &&
            !string.IsNullOrWhiteSpace(e.Text))
        {
            _lastErrorLine = e.Text.Trim();
        }

        Log.Append(e.Stream, e.Text);
    }

    private void OnOwnedProcessExited(object? sender, EventArgs e)
    {
        if (sender is not IPanelHostProcess process ||
            !ReferenceEquals(process, _owned))
        {
            return;
        }

        var exitCode = process.ExitCode;
        var duration = _ownedStartedAt is { } startedAt
            ? _time.GetLocalNow() - startedAt
            : (TimeSpan?)null;

        if (_stopRequested)
        {
            // StopCoreAsync is still waiting on this process; it detaches and
            // disposes it once the wait returns. Doing it here would pull the
            // handle out from under that wait.
            return;
        }

        DetachOwned();

        var message =
            $"PanelHost encerrou inesperadamente (código {exitCode?.ToString() ?? "?"}" +
            (duration is { } elapsed
                ? $", após {FormatDuration(elapsed)}"
                : string.Empty) +
            ")." +
            (_lastErrorLine is { Length: > 0 } reason ? $" {reason}" : string.Empty);
        Log.Append(PanelHostLogStream.Manager, message);
        Publish(new PanelHostStatus
        {
            State = PanelHostProcessState.Error,
            ExecutablePath = _ownedPath,
            LastExitCode = exitCode,
            LastRunDuration = duration,
            Detail = message,
        });

        TryAutomaticRestart();
    }

    private void TryAutomaticRestart()
    {
        var policy = RestartPolicy;
        if (!policy.Enabled || _disposed)
        {
            return;
        }

        var now = _time.GetLocalNow();
        while (_restarts.Count > 0 && now - _restarts.Peek() > policy.Window)
        {
            _restarts.Dequeue();
        }

        if (_restarts.Count >= policy.MaximumRestarts)
        {
            var message =
                $"Reinício automático pausado: {policy.MaximumRestarts} tentativas " +
                $"em {policy.Window.TotalSeconds:0} segundos.";
            Log.Append(PanelHostLogStream.Manager, message);
            Publish(Status with { Detail = message });
            return;
        }

        _restarts.Enqueue(now);
        Log.Append(PanelHostLogStream.Manager, "Reiniciando o PanelHost.");
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The Control Center closed while the restart was queued.
            }
        });
    }

    private void DetachOwned()
    {
        var owned = _owned;
        _owned = null;
        _ownedStartedAt = null;
        if (owned is null)
        {
            return;
        }

        owned.Exited -= OnOwnedProcessExited;
        owned.OutputReceived -= OnOwnedProcessOutput;
        owned.Dispose();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1
            ? $"{duration.TotalMinutes:0.0} min"
            : $"{duration.TotalSeconds:0.0} s";

    private PanelHostStatus Publish(PanelHostStatus status)
    {
        Volatile.Write(ref _status, status);
        StatusChanged?.Invoke(this, new PanelHostStatusChangedEventArgs(status));
        return status;
    }
}
