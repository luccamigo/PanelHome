using System.Diagnostics;

namespace PanelOS.Windows.Host;

public sealed record PanelHostProcessInfo(
    int Id,
    DateTimeOffset StartedAt,
    string? ExecutablePath);

public sealed class PanelHostOutputEventArgs : EventArgs
{
    public PanelHostOutputEventArgs(PanelHostLogStream stream, string text)
    {
        Stream = stream;
        Text = text;
    }

    public PanelHostLogStream Stream { get; }

    public string Text { get; }
}

/// <summary>
/// A PanelHost process the Control Center owns.
/// </summary>
public interface IPanelHostProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    event EventHandler<PanelHostOutputEventArgs>? OutputReceived;

    event EventHandler? Exited;

    /// <summary>Starts pumping stdout/stderr into <see cref="OutputReceived"/>.</summary>
    void BeginReadOutput();

    /// <summary>
    /// Ends the process and waits for it. Returns false when it was still alive
    /// after the timeout.
    /// </summary>
    Task<bool> StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// The operating system boundary of the host manager: find running agents and
/// start a new one. Abstracted so the lifecycle can be tested without ever
/// launching a real PanelHost.
/// </summary>
public interface IPanelHostProcessController
{
    IReadOnlyList<PanelHostProcessInfo> FindRunning(string processName);

    IPanelHostProcess Start(ProcessStartInfo startInfo);
}
