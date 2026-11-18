using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// What the Dashboard cards show and, just as importantly, which actions they
/// offer: an operation that cannot work is absent, not greyed out.
/// </summary>
[TestClass]
public sealed class DashboardPresentationTests
{
    [TestMethod]
    public async Task AStoppedAgentOffersOnlyStart()
    {
        var viewModel = Create(host: new FakePanelHostProcessManager());

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.CanStartPanelHost);
        Assert.IsFalse(viewModel.CanStopPanelHost);
        Assert.IsFalse(viewModel.CanRestartPanelHost);
        Assert.AreEqual("—", viewModel.PanelHostUptimeText);
        Assert.IsNull(viewModel.PanelHostBusyText);
        Assert.IsFalse(viewModel.IsPanelHostBusy);
    }

    [TestMethod]
    public async Task AManagedAgentOffersRestartAndStopButNotStart()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 4242,
                OwnedByControlCenter = true,
                StartedAt = DateTimeOffset.Now.AddMinutes(-90),
            },
        };
        var viewModel = Create(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.CanStartPanelHost);
        Assert.IsTrue(viewModel.CanStopPanelHost);
        Assert.IsTrue(viewModel.CanRestartPanelHost);
        Assert.AreEqual("PID 4242", viewModel.PanelHostProcessText);
        StringAssert.Contains(viewModel.PanelHostUptimeText, "1h");
    }

    [TestMethod]
    public async Task AnExternalAgentIsReportedButNeverRestarted()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 7,
                OwnedByControlCenter = false,
            },
        };
        var viewModel = Create(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Iniciado externamente", viewModel.PanelHostOwnershipText);
        Assert.IsFalse(viewModel.CanStopPanelHost);
        Assert.IsFalse(viewModel.CanRestartPanelHost);
        Assert.IsFalse(viewModel.CanStartPanelHost);
    }

    [DataTestMethod]
    [DataRow(PanelHostProcessState.Starting, "Iniciando…")]
    [DataRow(PanelHostProcessState.Stopping, "Encerrando…")]
    public async Task ATransitionIsStatedWhileItIsHappening(
        PanelHostProcessState state,
        string expected)
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus { State = state },
        };
        var viewModel = Create(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(expected, viewModel.PanelHostBusyText);
        Assert.IsTrue(viewModel.IsPanelHostBusy);

        // Nothing may be started or stopped while a transition is in flight.
        Assert.IsFalse(viewModel.CanStartPanelHost);
        Assert.IsFalse(viewModel.CanStopPanelHost);
    }

    [TestMethod]
    public async Task TheDriverCardReportsTheProtocolItActuallyRead()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = PanelDriverLifecycle.Ready,
                ProtocolVersion = 5,
            },
        };
        var viewModel = Create(driver);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Protocolo v5", viewModel.DriverProtocolText);
    }

    [TestMethod]
    public async Task AnUnavailableDriverInventsNoProtocol()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.NotInstalled,
            },
        };
        var viewModel = Create(driver);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("—", viewModel.DriverProtocolText);
        Assert.IsFalse(viewModel.CanConnect);
        Assert.IsFalse(viewModel.CanDisconnect);
    }

    [TestMethod]
    public async Task TheDisplayCardOffersOnlyTheDirectionItCanGo()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = PanelDriverLifecycle.Ready,
                Monitor = PanelMonitorConnection.Disconnected,
            },
        };
        var viewModel = Create(driver);
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.CanConnect);
        Assert.IsFalse(viewModel.CanDisconnect);
        Assert.AreEqual("Desconectado", viewModel.MonitorText);

        driver.Snapshot = driver.Snapshot with
        {
            Monitor = PanelMonitorConnection.Connected,
        };
        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.CanConnect);
        Assert.IsTrue(viewModel.CanDisconnect);
        Assert.AreEqual("Conectado", viewModel.MonitorText);
    }

    [TestMethod]
    public void TheStreamDeckCardStartsEmptyAndNeverClaimsATablet()
    {
        var viewModel = Create();

        Assert.AreEqual("Sem perfil", viewModel.StreamDeckStatusText);
        Assert.AreEqual(PanelStatusLevel.Neutral, viewModel.StreamDeckLevel);
        Assert.AreEqual("—", viewModel.StreamDeckRevisionText);

        // A running agent says nothing about the tablet, and the card says so.
        Assert.AreEqual("Indisponível", viewModel.TabletStatusText);
        StringAssert.Contains(viewModel.TabletDetail, "não significa");
    }

    private static DashboardViewModel Create(
        FakePanelDriverController? driver = null,
        FakePanelHostProcessManager? host = null) =>
        new(
            driver ?? new FakePanelDriverController(),
            host ?? new FakePanelHostProcessManager());
}
