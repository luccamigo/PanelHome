using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// What the landing page reports, and - just as importantly - what it refuses
/// to report. Every problem it raises has to come from something that was
/// actually measured.
/// </summary>
[TestClass]
public sealed class OverviewViewModelTests
{
    [TestMethod]
    public void AMissingDriverIsTheWorstProblemAndReadsAsASentence()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.NotInstalled,
                Detail = "ERROR_FILE_NOT_FOUND",
            },
        };
        var (overview, device) = Create(driver);

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(PanelStatusLevel.Bad, overview.HealthLevel);
        Assert.AreEqual("Precisa de atenção", overview.HealthText);

        var problem = overview.Problems.First();
        Assert.AreEqual(PanelStatusLevel.Bad, problem.Level);
        StringAssert.Contains(problem.Title, "driver de display");

        // The Win32 form is kept, but behind a disclosure rather than as the
        // headline: an operator is told what to do, an engineer can still see
        // what the driver said.
        Assert.IsTrue(problem.HasTechnical);
        Assert.AreEqual("ERROR_FILE_NOT_FOUND", problem.Technical);
        StringAssert.Contains(problem.Detail, "Instale o driver");
    }

    [TestMethod]
    public void AccessDeniedSaysWhatToDoAboutIt()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.AccessDenied,
                Detail = "ERROR_ACCESS_DENIED",
            },
        };
        var (overview, device) = Create(driver);

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        var problem = overview.Problems.Single(
            item => item.Technical == "ERROR_ACCESS_DENIED");
        StringAssert.Contains(problem.Detail, "administrador");
    }

    [TestMethod]
    public void AStoppedAgentIsRaisedWithTheActionThatFixesIt()
    {
        var (overview, device) = Create();

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        var problem = overview.Problems.Single(
            item => item.Title.Contains("PanelHost", StringComparison.Ordinal));
        Assert.IsTrue(problem.HasAction);
        Assert.AreSame(device.StartPanelHostCommand, problem.Action);
        Assert.AreEqual(PanelStatusLevel.Warning, problem.Level);
    }

    [TestMethod]
    public void WithoutTheManagementChannelNothingDownstreamIsClaimed()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 99,
                OwnedByControlCenter = true,
            },
        };
        var (overview, device) = Create(host: host);

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        // The agent is up but the IPC is silent, so the tablet, the display and
        // the video pipeline are unobservable. Listing them as disconnected
        // would be reporting an absence of data as a measurement.
        Assert.AreEqual(1, overview.Problems.Count);
        StringAssert.Contains(overview.Problems[0].Title, "IPC local");
        Assert.IsFalse(overview.Problems.Any(
            problem => problem.Title.Contains("tablet", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void AHealthySystemRaisesNothing()
    {
        var management = new FakeManagementClient();
        var (overview, device) = CreateConnected(management, out _);

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        management.Publish(Snapshots.Healthy());

        Assert.AreEqual(0, overview.Problems.Count);
        Assert.IsFalse(overview.HasProblems);
        Assert.AreEqual(PanelStatusLevel.Good, overview.HealthLevel);
        Assert.AreEqual("Tudo em ordem", overview.HealthText);
    }

    [TestMethod]
    public void AnIdleAgentIsHealthyWithNotesRatherThanBroken()
    {
        var management = new FakeManagementClient();
        var (overview, device) = CreateConnected(management, out _);

        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        management.Publish(Snapshots.Idle());

        // A tablet nobody has opened yet is worth listing, but it is not a
        // failure - the verdict stays good and the note stays informational.
        var tablet = overview.Problems.Single(
            problem => problem.Level == PanelStatusLevel.Neutral);
        StringAssert.Contains(tablet.Title, "tablet");
        Assert.IsFalse(tablet.HasAction);

        // The display, however, is a warning: the driver is ready and nothing
        // is being captured.
        Assert.IsTrue(overview.Problems.Any(
            problem => problem.Level == PanelStatusLevel.Warning));
        Assert.AreEqual(PanelStatusLevel.Warning, overview.HealthLevel);
    }

    [TestMethod]
    public void DroppedFramesAreRaisedWithTheCountThatWasMeasured()
    {
        var management = new FakeManagementClient();
        var (overview, device) = CreateConnected(management, out _);
        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        var snapshot = Snapshots.Healthy();
        management.Publish(snapshot with
        {
            Video = snapshot.Video with { SequenceGap = 7 },
        });

        var problem = overview.Problems.Single(
            item => item.Title.Contains("Frames", StringComparison.Ordinal));
        Assert.AreEqual(PanelStatusLevel.Warning, problem.Level);
        Assert.AreEqual("7 frames", problem.Technical);
    }

    [TestMethod]
    public void AnAgentWithoutAPublishedProfileIsOfferedTheEditor()
    {
        var management = new FakeManagementClient();
        var (overview, device) = CreateConnected(management, out _);
        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();

        management.Publish(Snapshots.Healthy() with
        {
            StreamDeck = new PanelOS.Windows.Management.StreamDeckManagementSnapshot(
                null,
                null,
                null,
                0,
                0),
        });

        var problem = overview.Problems.Single(
            item => item.Title.Contains("Stream Deck", StringComparison.Ordinal));
        Assert.AreSame(device.OpenStreamDeckCommand, problem.Action);
    }

    [TestMethod]
    public void OptionalServiceWarningsComeFromManagementStateNotLogText()
    {
        var management = new FakeManagementClient();
        var (overview, device) = CreateConnected(management, out _);
        device.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
        var snapshot = Snapshots.Healthy();

        management.Publish(snapshot with
        {
            Services = new HostServicesManagementSnapshot(
                new HostServiceManagementSnapshot(
                    "degraded",
                    "sensor_unavailable"),
                new HostServiceManagementSnapshot("running")),
        });

        var problem = overview.Problems.Single(item =>
            item.Technical == "sensor_unavailable");
        StringAssert.Contains(problem.Title, "telemetria");
        Assert.AreEqual(PanelStatusLevel.Warning, problem.Level);
    }

    private static (OverviewViewModel Overview, DashboardViewModel Device) Create(
        FakePanelDriverController? driver = null,
        FakePanelHostProcessManager? host = null)
    {
        var device = new DashboardViewModel(
            driver ?? new FakePanelDriverController(),
            host ?? new FakePanelHostProcessManager());
        return (new OverviewViewModel(device), device);
    }

    /// <summary>
    /// A running, managed agent with a ready driver and a live management
    /// channel - the state in which the downstream questions are answerable.
    /// </summary>
    private static (OverviewViewModel Overview, DashboardViewModel Device) CreateConnected(
        FakeManagementClient management,
        out FakePanelHostProcessManager host)
    {
        host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 4242,
                OwnedByControlCenter = true,
            },
        };
        var device = new DashboardViewModel(
            new FakePanelDriverController
            {
                Snapshot = new PanelDriverSnapshot
                {
                    Availability = PanelDriverAvailability.Available,
                    Lifecycle = PanelDriverLifecycle.Ready,
                    Monitor = PanelMonitorConnection.Connected,
                },
            },
            host,
            managementClient: management);
        return (new OverviewViewModel(device), device);
    }
}
