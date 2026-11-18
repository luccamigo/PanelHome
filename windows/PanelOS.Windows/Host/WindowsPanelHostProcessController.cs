using System.Diagnostics;

namespace PanelOS.Windows.Host;

public sealed class WindowsPanelHostProcessController : IPanelHostProcessController
{
    public IReadOnlyList<PanelHostProcessInfo> FindRunning(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var found = new List<PanelHostProcessInfo>(processes.Length);
        foreach (var process in processes)
        {
            try
            {
                found.Add(new PanelHostProcessInfo(
                    process.Id,
                    new DateTimeOffset(process.StartTime),
                    TryReadPath(process)));
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
                // The process ended between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public IPanelHostProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "O Windows não retornou um processo para PanelHost.");
        return new WindowsPanelHostProcess(process);
    }

    private static string? TryReadPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
        {
            // Reading the module of another user's process is not permitted;
            // the agent is still reported, just without its path.
            return null;
        }
    }

    private sealed class WindowsPanelHostProcess : IPanelHostProcess
    {
        private readonly Process _process;
        private int _outputStarted;

        internal WindowsPanelHostProcess(Process process)
        {
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
            _process.OutputDataReceived += (_, args) => Publish(
                PanelHostLogStream.StandardOutput,
                args.Data);
            _process.ErrorDataReceived += (_, args) => Publish(
                PanelHostLogStream.StandardError,
                args.Data);
        }

        public event EventHandler<PanelHostOutputEventArgs>? OutputReceived;

        public event EventHandler? Exited;

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                        System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            }
        }

        public void BeginReadOutput()
        {
            if (Interlocked.Exchange(ref _outputStarted, 1) != 0)
            {
                return;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public async Task<bool> StopAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (HasExited)
            {
                return true;
            }

            try
            {
                // PanelHost runs without a console window here, so there is no
                // Ctrl+C to send and no window to close: the owned process is
                // ended directly, together with anything it started.
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
            {
                return HasExited;
            }

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutSource.Token,
                cancellationToken);
            try
            {
                await _process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or InvalidOperationException)
            {
                // Cancelled, or the handle was released while waiting.
                return HasExited;
            }
        }

        public void Dispose() => _process.Dispose();

        private void Publish(PanelHostLogStream stream, string? text)
        {
            if (text is null)
            {
                return;
            }

            OutputReceived?.Invoke(
                this,
                new PanelHostOutputEventArgs(stream, text));
        }
    }
}
