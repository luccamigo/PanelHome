using System.Collections.ObjectModel;
using System.Windows.Input;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// One thing that is wrong, or merely incomplete, said the way an operator can
/// act on it.
/// </summary>
/// <remarks>
/// The title is a sentence in Portuguese, not an error code: "Não foi possível
/// consultar o driver: acesso negado" rather than ERROR_ACCESS_DENIED. The code
/// still matters when someone is debugging, so it goes in
/// <see cref="Technical"/>, which the page keeps collapsed.
/// </remarks>
public sealed class PanelProblem
{
    public PanelProblem(
        string title,
        string detail,
        PanelStatusLevel level,
        string? technical = null,
        string? actionLabel = null,
        ICommand? action = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Title = title;
        Detail = detail;
        Level = level;
        Technical = string.IsNullOrWhiteSpace(technical) ? null : technical;
        ActionLabel = actionLabel;
        Action = action;
    }

    public string Title { get; }

    /// <summary>What follows from it, and what to do about it.</summary>
    public string Detail { get; }

    public PanelStatusLevel Level { get; }

    /// <summary>The raw detail from the driver or the agent, when there is one.</summary>
    public string? Technical { get; }

    public string? ActionLabel { get; }

    public ICommand? Action { get; }

    public bool HasAction => Action is not null && ActionLabel is not null;

    public bool HasTechnical => Technical is not null;
}

/// <summary>
/// The landing page. It answers, without navigating anywhere: is the agent up,
/// is the tablet connected, is the display live, is video flowing, which
/// Stream Deck profile is active - and if any of that is not true, what to do.
/// </summary>
/// <remarks>
/// It owns no state of its own beyond the problem list: every fact comes from
/// <see cref="Device"/>, so this page and the Device page cannot drift apart.
/// </remarks>
public sealed class OverviewViewModel : ObservableObject
{
    private readonly DashboardViewModel _device;

    public OverviewViewModel(DashboardViewModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _device.SnapshotChanged += (_, _) => Rebuild();
        Rebuild();
    }

    /// <summary>Live device state, shared with the Device page.</summary>
    public DashboardViewModel Device => _device;

    /// <summary>
    /// Everything that needs attention, worst first. Empty is the good case and
    /// the page says so rather than showing an empty box.
    /// </summary>
    public ObservableCollection<PanelProblem> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>Headline verdict: the worst level among the problems.</summary>
    public PanelStatusLevel HealthLevel { get; private set; } = PanelStatusLevel.Neutral;

    public string HealthText { get; private set; } = Strings.Get("OverviewDegraded");

    public string HealthDetail { get; private set; } =
        Strings.Get("OverviewDegradedDetail");

    private void Rebuild()
    {
        Problems.Clear();
        foreach (var problem in Collect())
        {
            Problems.Add(problem);
        }

        // Only a warning or a failure lowers the verdict. An informational note
        // - "no tablet has connected yet" - is worth listing but is not a
        // reason to tell the operator something is broken.
        HealthLevel = Problems.Any(problem => problem.Level == PanelStatusLevel.Bad)
            ? PanelStatusLevel.Bad
            : Problems.Any(problem => problem.Level == PanelStatusLevel.Warning)
                ? PanelStatusLevel.Warning
                : PanelStatusLevel.Good;

        (HealthText, HealthDetail) = HealthLevel switch
        {
            PanelStatusLevel.Bad =>
                (Strings.Get("OverviewBroken"), Strings.Get("OverviewBrokenDetail")),
            PanelStatusLevel.Warning =>
                (Strings.Get("OverviewDegraded"), Strings.Get("OverviewDegradedDetail")),
            _ => (Strings.Get("OverviewHealthy"), Strings.Get("OverviewHealthyDetail")),
        };

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(HealthLevel));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthDetail));
    }

    /// <summary>
    /// Derives the list from measured state only. Nothing here parses a log
    /// line: if the management channel did not report it, it is not claimed.
    /// </summary>
    private IEnumerable<PanelProblem> Collect()
    {
        var problems = new List<PanelProblem>();
        var snapshot = _device.Snapshot;

        switch (snapshot.Availability)
        {
            case PanelDriverAvailability.NotInstalled:
                problems.Add(new PanelProblem(
                    Strings.Get("ProblemDriverMissingTitle"),
                    Strings.Get("ProblemDriverMissingDetail"),
                    PanelStatusLevel.Bad,
                    snapshot.Detail));
                break;
            case PanelDriverAvailability.AccessDenied:
                problems.Add(new PanelProblem(
                    Strings.Get("ProblemDriverAccessTitle"),
                    Strings.Get("ProblemDriverAccessDetail"),
                    PanelStatusLevel.Bad,
                    snapshot.Detail));
                break;
            case PanelDriverAvailability.Error:
                problems.Add(new PanelProblem(
                    Strings.Get("ProblemDriverErrorTitle"),
                    Strings.Get("DriverUnavailable"),
                    PanelStatusLevel.Bad,
                    snapshot.Detail,
                    Strings.Get("ProblemActionOpenDiagnostics"),
                    _device.OpenLogsCommand));
                break;
            case PanelDriverAvailability.Available
                when snapshot.Lifecycle == PanelDriverLifecycle.Stopped:
                problems.Add(new PanelProblem(
                    Strings.Get("ProblemDriverStoppedTitle"),
                    Strings.Get("ProblemDriverStoppedDetail"),
                    PanelStatusLevel.Warning,
                    snapshot.Detail));
                break;
        }

        var host = _device.PanelHostStatus;
        if (host.State == PanelHostProcessState.Error)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemHostErrorTitle"),
                Strings.Get("ProblemHostStoppedDetail"),
                PanelStatusLevel.Bad,
                _device.PanelHostDetail,
                Strings.Get("ProblemActionStartHost"),
                _device.StartPanelHostCommand));
        }
        else if (!_device.IsPanelHostRunning && !_device.IsPanelHostBusy)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemHostStoppedTitle"),
                Strings.Get("ProblemHostStoppedDetail"),
                PanelStatusLevel.Warning,
                _device.PanelHostDetail,
                Strings.Get("ProblemActionStartHost"),
                _device.StartPanelHostCommand));
        }

        // Beyond this point everything is observed through the management IPC.
        // Without it the remaining questions are unanswerable, and saying so is
        // the honest report - not a list of things that look disconnected.
        if (!_device.IsManagementConnected)
        {
            if (_device.IsPanelHostRunning)
            {
                problems.Add(new PanelProblem(
                    Strings.Get("ProblemManagementTitle"),
                    Strings.Get("ProblemManagementDetail"),
                    PanelStatusLevel.Warning,
                    _device.ManagementText,
                    Strings.Get("ProblemActionOpenDiagnostics"),
                    _device.OpenLogsCommand));
            }

            return problems;
        }

        if (_device.RuntimeSnapshot?.Services?.Telemetry is
            { State: "degraded" } telemetry)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemTelemetryTitle"),
                Strings.Get("ProblemTelemetryDetail"),
                PanelStatusLevel.Warning,
                telemetry.ErrorCode,
                Strings.Get("ProblemActionOpenDiagnostics"),
                _device.OpenLogsCommand));
        }

        if (_device.RuntimeSnapshot?.Services?.Media is
            { State: "degraded" } media)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemMediaTitle"),
                Strings.Get("ProblemMediaDetail"),
                PanelStatusLevel.Warning,
                media.ErrorCode,
                Strings.Get("ProblemActionOpenDiagnostics"),
                _device.OpenLogsCommand));
        }

        if (!_device.IsMonitorConnected)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemDisplayTitle"),
                Strings.Get("ProblemDisplayDetail"),
                PanelStatusLevel.Warning,
                null,
                _device.CanConnect ? Strings.Get("ProblemActionConnectDisplay") : null,
                _device.CanConnect ? _device.ConnectCommand : null));
        }

        if (!_device.IsTabletConnected)
        {
            // Not an error: a panel nobody has opened yet is the normal state
            // of a machine that just booted.
            problems.Add(new PanelProblem(
                Strings.Get("ProblemTabletTitle"),
                Strings.Get("ProblemTabletDetail"),
                PanelStatusLevel.Neutral));
        }

        if (_device.HasVideoDrops)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemVideoGapTitle"),
                Strings.Get("ProblemVideoGapDetail"),
                PanelStatusLevel.Warning,
                _device.VideoDropsText,
                Strings.Get("ProblemActionOpenDiagnostics"),
                _device.OpenLogsCommand));
        }

        if (!_device.HasPublishedStreamDeckProfile)
        {
            problems.Add(new PanelProblem(
                Strings.Get("ProblemStreamDeckTitle"),
                Strings.Get("ProblemStreamDeckDetail"),
                PanelStatusLevel.Warning,
                null,
                Strings.Get("ProblemActionOpenStreamDeck"),
                _device.OpenStreamDeckCommand));
        }

        return problems;
    }
}
