namespace PanelOS.Windows.Host;

/// <summary>
/// How often a crashed agent may be restarted before the manager gives up.
/// </summary>
/// <remarks>
/// A crash that repeats immediately is a configuration problem, not a blip.
/// Restarting without a ceiling would turn it into a loop that hides the cause,
/// so the manager stops trying and says so.
/// </remarks>
public sealed record PanelHostRestartPolicy(
    bool Enabled,
    int MaximumRestarts,
    TimeSpan Window)
{
    public static PanelHostRestartPolicy Disabled { get; } =
        new(false, 3, TimeSpan.FromSeconds(60));

    public static PanelHostRestartPolicy Default { get; } =
        new(true, 3, TimeSpan.FromSeconds(60));
}

/// <summary>
/// Owns the PanelHost agent's lifecycle for the Control Center: it can start
/// one, adopt nothing it did not start, and report what it sees.
/// </summary>
public interface IPanelHostProcessManager : IDisposable
{
    PanelHostStatus Status { get; }

    /// <summary>Output captured from an agent this Control Center started.</summary>
    PanelHostLogBuffer Log { get; }

    /// <summary>Restart-after-crash behaviour; off by default.</summary>
    PanelHostRestartPolicy RestartPolicy { get; set; }

    /// <summary>
    /// Carrier the next launch opens. Changing it does not touch a running
    /// agent: the carrier is fixed when the process starts, so a change takes
    /// effect on the next start or restart.
    /// </summary>
    PanelHostTransport Transport { get; set; }

    event EventHandler<PanelHostStatusChangedEventArgs>? StatusChanged;

    /// <summary>The agent that would be launched, or null when none was found.</summary>
    PanelHostExecutable? LocateExecutable();

    /// <summary>Re-reads the system state, including externally started agents.</summary>
    PanelHostStatus Refresh();

    Task<PanelHostOperationResult> StartAsync(CancellationToken cancellationToken);

    Task<PanelHostOperationResult> StopAsync(CancellationToken cancellationToken);

    Task<PanelHostOperationResult> RestartAsync(CancellationToken cancellationToken);
}
