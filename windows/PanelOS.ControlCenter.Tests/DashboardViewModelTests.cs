using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class DashboardViewModelTests
{
    [TestMethod]
    public async Task MissingDriverKeepsTheDashboardUsable()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.NotInstalled,
                Detail = "PanelDisplayDriver locate interface failed.",
            },
        };
        var viewModel = CreateViewModel(driver);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(
            "PanelDisplayDriver não encontrado",
            viewModel.DriverAvailabilityText);
        Assert.AreEqual(PanelStatusLevel.Bad, viewModel.DriverAvailabilityLevel);
        Assert.AreEqual("—", viewModel.DriverStateText);
        Assert.AreEqual("—", viewModel.MonitorText);
        Assert.AreEqual("—", viewModel.ResolutionText);
        Assert.IsFalse(viewModel.ConnectCommand.CanExecute(null));
        Assert.IsFalse(viewModel.DisconnectCommand.CanExecute(null));
        Assert.AreEqual(
            "PanelDisplayDriver locate interface failed.",
            viewModel.StatusMessage);
    }

    [TestMethod]
    public async Task ReadyDriverShowsLifecycleModeAndAllowsConnecting()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = PanelDriverLifecycle.Ready,
                Monitor = PanelMonitorConnection.Disconnected,
                ProtocolVersion = PanelDisplayDriverController.ExpectedProtocolVersion,
                ActiveMode = new PanelDisplayModeInfo(1280, 800, 60),
            },
        };
        var viewModel = CreateViewModel(driver);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Instalado", viewModel.DriverAvailabilityText);
        Assert.AreEqual("Pronto", viewModel.DriverStateText);
        Assert.AreEqual("1280 × 800", viewModel.ResolutionText);
        Assert.AreEqual("60 Hz", viewModel.RefreshRateText);
        Assert.IsTrue(viewModel.CanConnect);
        Assert.IsFalse(viewModel.CanDisconnect);
        Assert.IsNull(viewModel.StatusMessage);
    }

    [TestMethod]
    public async Task ConnectSendsTheCommandAndRefreshesTheSnapshot()
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
        var viewModel = CreateViewModel(driver);
        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.ConnectCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, driver.ConnectCalls);
        Assert.AreEqual("Monitor virtual conectado.", viewModel.StatusMessage);
        Assert.IsTrue(viewModel.CanDisconnect);
    }

    [TestMethod]
    public async Task ADeclinedElevationPromptIsExplainedInPlainWords()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = PanelDriverLifecycle.Ready,
                Monitor = PanelMonitorConnection.Disconnected,
            },
            ConnectResult = new PanelDriverCommandResult(
                PanelDriverCommandStatus.ElevationDeclined,
                PanelMonitorConnection.Disconnected,
                "elevation_declined"),
        };
        var viewModel = CreateViewModel(driver);
        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.ConnectCommand.ExecuteAsync(CancellationToken.None);

        StringAssert.Contains(viewModel.StatusMessage, "elevação não foi aprovada");
        Assert.IsFalse(viewModel.StatusMessage!.Contains("elevation_declined"));
        Assert.IsTrue(viewModel.CanConnect, "the operator can try again after approving the prompt");
    }

    [TestMethod]
    public async Task DisconnectSendsTheCommandAndRefreshesTheSnapshot()
    {
        var driver = new FakePanelDriverController
        {
            Snapshot = new PanelDriverSnapshot
            {
                Availability = PanelDriverAvailability.Available,
                Lifecycle = PanelDriverLifecycle.Ready,
                Monitor = PanelMonitorConnection.Connected,
            },
        };
        var viewModel = CreateViewModel(driver);
        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.DisconnectCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual(1, driver.DisconnectCalls);
        Assert.AreEqual("Monitor virtual desconectado.", viewModel.StatusMessage);
        Assert.IsTrue(viewModel.CanConnect);
    }

    [TestMethod]
    public async Task AStoppedAgentOffersOnlyStart()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = CreateViewModel(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Parado", viewModel.PanelHostStateText);
        Assert.AreEqual(PanelStatusLevel.Neutral, viewModel.PanelHostLevel);
        Assert.AreEqual("—", viewModel.PanelHostProcessText);
        Assert.AreEqual("—", viewModel.PanelHostOwnershipText);
        Assert.IsTrue(viewModel.StartPanelHostCommand.CanExecute(null));
        Assert.IsFalse(viewModel.StopPanelHostCommand.CanExecute(null));
        Assert.IsFalse(viewModel.RestartPanelHostCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AManagedAgentShowsItsPidAndOffersStopAndRestart()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 18420,
                OwnedByControlCenter = true,
            },
        };
        var viewModel = CreateViewModel(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Em execução", viewModel.PanelHostStateText);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.PanelHostLevel);
        Assert.AreEqual("PID 18420", viewModel.PanelHostProcessText);
        Assert.AreEqual(
            "Gerenciado pelo Control Center",
            viewModel.PanelHostOwnershipText);
        Assert.IsFalse(viewModel.StartPanelHostCommand.CanExecute(null));
        Assert.IsTrue(viewModel.StopPanelHostCommand.CanExecute(null));
        Assert.IsTrue(viewModel.RestartPanelHostCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task AnExternalAgentIsShownButNotControlled()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 991,
                OwnedByControlCenter = false,
                Detail = "Iniciado fora do Control Center.",
            },
        };
        var viewModel = CreateViewModel(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("Em execução", viewModel.PanelHostStateText);
        Assert.AreEqual("Iniciado externamente", viewModel.PanelHostOwnershipText);
        Assert.IsFalse(
            viewModel.StopPanelHostCommand.CanExecute(null),
            "um agente externo nunca é encerrado pelo Control Center");
        Assert.IsFalse(viewModel.RestartPanelHostCommand.CanExecute(null));
        Assert.IsFalse(viewModel.StartPanelHostCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task StartStopAndRestartReachTheManager()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = CreateViewModel(host: host);
        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.StartPanelHostCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, host.StartCalls);

        host.Status = new PanelHostStatus
        {
            State = PanelHostProcessState.Running,
            ProcessId = 5,
            OwnedByControlCenter = true,
        };
        await viewModel.RestartPanelHostCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, host.RestartCalls);

        await viewModel.StopPanelHostCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, host.StopCalls);
    }

    [TestMethod]
    public async Task AFailedHostOperationIsReportedOnTheCard()
    {
        var host = new FakePanelHostProcessManager
        {
            StartResult = PanelHostOperationResult.Error(
                PanelHostErrorCodes.ExecutableNotFound,
                "PanelHost.exe não foi encontrado."),
        };
        var viewModel = CreateViewModel(host: host);
        await viewModel.RefreshAsync(CancellationToken.None);

        await viewModel.StartPanelHostCommand.ExecuteAsync(CancellationToken.None);

        Assert.AreEqual("PanelHost.exe não foi encontrado.", viewModel.PanelHostDetail);
    }

    [TestMethod]
    public async Task AnUnexpectedExitIsSurfacedFromTheManager()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = CreateViewModel(host: host);
        await viewModel.RefreshAsync(CancellationToken.None);

        host.Status = new PanelHostStatus
        {
            State = PanelHostProcessState.Error,
            LastExitCode = 3,
            Detail = "PanelHost encerrou inesperadamente (código 3, após 2,0 s).",
        };

        Assert.AreEqual("Erro", viewModel.PanelHostStateText);
        Assert.AreEqual(PanelStatusLevel.Bad, viewModel.PanelHostLevel);
        StringAssert.Contains(viewModel.PanelHostDetail!, "inesperadamente");
        Assert.IsTrue(
            viewModel.StartPanelHostCommand.CanExecute(null),
            "depois de uma queda o agente pode ser iniciado de novo");
    }

    [TestMethod]
    public void TheTabletCardNeverClaimsAConnection()
    {
        var viewModel = CreateViewModel(host: new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 1,
                OwnedByControlCenter = true,
            },
        });

        // A running agent says nothing about the tablet, and the card says so.
        Assert.AreEqual("Indisponível", viewModel.TabletStatusText);
        StringAssert.Contains(viewModel.TabletDetail, "não significa");
    }

    private static DashboardViewModel CreateViewModel(
        FakePanelDriverController? driver = null,
        FakePanelHostProcessManager? host = null) =>
        new(
            driver ?? new FakePanelDriverController(),
            host ?? new FakePanelHostProcessManager());
}
