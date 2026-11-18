using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Development;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    [TestMethod]
    public async Task LoadReflectsTheStoredPreferencesAndTheRegistry()
    {
        var startup = new FakeStartupRegistration { Enabled = true };
        var store = new InMemoryControlCenterSettingsStore
        {
            Settings = new ControlCenterSettings
            {
                MinimizeToTray = true,
                StartPanelHostWithControlCenter = true,
                StopPanelHostOnExit = true,
                DevelopmentMode = true,
            },
        };
        var viewModel = Create(store, startup);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.StartControlCenterWithWindows);
        Assert.IsTrue(viewModel.MinimizeToTray);
        Assert.IsTrue(viewModel.StartPanelHostWithControlCenter);
        Assert.IsTrue(viewModel.StopPanelHostOnExit);
        Assert.IsTrue(viewModel.DevelopmentMode);
        Assert.IsNull(store.Saved, "carregar não pode regravar");
    }

    [TestMethod]
    public async Task DefaultsEndTheAgentWithTheWindowAndHideDevelopmentTools()
    {
        var viewModel = Create();

        await viewModel.LoadAsync(CancellationToken.None);

        // Sair do Control Center encerra o agente que ele mesmo iniciou.
        Assert.IsTrue(viewModel.StopPanelHostOnExit);
        Assert.IsFalse(viewModel.StartPanelHostWithControlCenter);
        Assert.IsFalse(viewModel.RestartPanelHostAfterCrash);
        Assert.IsFalse(viewModel.DevelopmentMode);
    }

    [TestMethod]
    public async Task UnsupportedStartupIsSurfacedWithItsReason()
    {
        var viewModel = Create(startup: new FakeStartupRegistration(isSupported: false));

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.IsControlCenterStartupSupported);
        Assert.AreEqual(
            "Executável não encontrado.",
            viewModel.ControlCenterStartupUnsupportedReason);
    }

    [TestMethod]
    public async Task TogglingStartupWritesTheRunEntry()
    {
        var startup = new FakeStartupRegistration();
        var viewModel = Create(startup: startup);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.StartControlCenterWithWindows = true;

        Assert.IsTrue(startup.Enabled);
        StringAssert.Contains(viewModel.StatusMessage!, "Control Center");

        viewModel.StartControlCenterWithWindows = false;

        Assert.IsFalse(startup.Enabled);
    }

    [TestMethod]
    public async Task AFailedStartupWriteSnapsTheSwitchBack()
    {
        var startup = new FakeStartupRegistration
        {
            Failure = new UnauthorizedAccessException("sem permissão"),
        };
        var viewModel = Create(startup: startup);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.StartControlCenterWithWindows = true;

        Assert.IsFalse(viewModel.StartControlCenterWithWindows);
        StringAssert.Contains(viewModel.StatusMessage!, "sem permissão");
    }

    [TestMethod]
    public async Task PreferencesArePersisted()
    {
        var store = new InMemoryControlCenterSettingsStore();
        var viewModel = Create(store);
        await viewModel.LoadAsync(CancellationToken.None);

        viewModel.MinimizeToTray = true;
        viewModel.StartPanelHostWithControlCenter = true;
        viewModel.StopPanelHostOnExit = true;
        await viewModel.SaveAsync(CancellationToken.None);

        Assert.IsNotNull(store.Saved);
        Assert.IsTrue(store.Saved.MinimizeToTray);
        Assert.IsTrue(store.Saved.StartPanelHostWithControlCenter);
        Assert.IsTrue(store.Saved.StopPanelHostOnExit);
    }

    [TestMethod]
    public async Task TheRestartPolicyReachesTheManager()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = Create(host: host);
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsFalse(host.RestartPolicy.Enabled);

        viewModel.RestartPanelHostAfterCrash = true;

        Assert.IsTrue(host.RestartPolicy.Enabled);
        Assert.AreEqual(3, host.RestartPolicy.MaximumRestarts);
        Assert.AreEqual(TimeSpan.FromSeconds(60), host.RestartPolicy.Window);

        viewModel.RestartPanelHostAfterCrash = false;

        Assert.IsFalse(host.RestartPolicy.Enabled);
    }

    [TestMethod]
    public async Task TheAgentPathIsShown()
    {
        var host = new FakePanelHostProcessManager
        {
            Executable = new PanelHostExecutable(
                @"C:\PanelOS\PanelHost.exe",
                PanelHostExecutableSource.ApplicationFolder),
        };
        var viewModel = Create(host: host);

        await viewModel.LoadAsync(CancellationToken.None);

        StringAssert.Contains(viewModel.PanelHostExecutableText, "PanelHost.exe");

        host.Executable = null;
        var missing = Create(host: host);
        StringAssert.Contains(missing.PanelHostExecutableText, "não foi encontrado");
    }

    [TestMethod]
    public async Task AdbToolsStayInaccessibleUntilDevelopmentModeIsOn()
    {
        var runner = new FakeProcessRunner();
        var viewModel = Create(adb: new AdbDevelopmentTransport(
            new FakeAdbLocator(),
            runner));
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.DevelopmentMode);
        Assert.IsFalse(viewModel.ProbeAdbCommand.CanExecute(null));
        Assert.IsFalse(viewModel.ConfigureUsbTransportCommand.CanExecute(null));

        viewModel.DevelopmentMode = true;

        Assert.IsTrue(viewModel.ProbeAdbCommand.CanExecute(null));
        Assert.IsTrue(viewModel.ConfigureUsbTransportCommand.CanExecute(null));
        Assert.AreEqual(0, runner.Calls.Count, "nada é executado só por ligar o modo");
    }

    [TestMethod]
    public async Task ProbingReportsTheDevice()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueDevices("List of devices attached\nR58M12345XY\tdevice");
        var viewModel = Create(adb: new AdbDevelopmentTransport(
            new FakeAdbLocator(),
            runner));
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.DevelopmentMode = true;

        await viewModel.ProbeAdbCommand.ExecuteAsync(CancellationToken.None);

        StringAssert.Contains(viewModel.AdbSummary, "R58M12345XY");
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.AdbLevel);
    }

    [TestMethod]
    public async Task AMissingAdbIsReportedAsAProblem()
    {
        var viewModel = Create(adb: new AdbDevelopmentTransport(
            new FakeAdbLocator { Path = null },
            new FakeProcessRunner()));
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.DevelopmentMode = true;

        await viewModel.ProbeAdbCommand.ExecuteAsync(CancellationToken.None);

        StringAssert.Contains(viewModel.AdbSummary, "adb.exe");
        Assert.AreEqual(PanelStatusLevel.Bad, viewModel.AdbLevel);
    }

    [TestMethod]
    public async Task ConfiguringTheUsbTransportReportsTheResult()
    {
        var runner = new FakeProcessRunner();
        runner.EnqueueDevices("List of devices attached\nR58M12345XY\tdevice");
        var viewModel = Create(adb: new AdbDevelopmentTransport(
            new FakeAdbLocator(),
            runner));
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.DevelopmentMode = true;

        await viewModel.ConfigureUsbTransportCommand.ExecuteAsync(
            CancellationToken.None);

        StringAssert.Contains(viewModel.AdbSummary, "8765");
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.AdbLevel);
    }

    [TestMethod]
    public void WithoutAnAdbTransportTheCommandsStayDisabled()
    {
        var viewModel = Create();

        viewModel.DevelopmentMode = true;

        Assert.IsFalse(viewModel.IsAdbAvailable);
        Assert.IsFalse(viewModel.ProbeAdbCommand.CanExecute(null));
    }

    private static SettingsViewModel Create(
        InMemoryControlCenterSettingsStore? store = null,
        FakeStartupRegistration? startup = null,
        FakePanelHostProcessManager? host = null,
        IAdbDevelopmentTransport? adb = null) =>
        new(
            store ?? new InMemoryControlCenterSettingsStore(),
            startup ?? new FakeStartupRegistration(),
            host ?? new FakePanelHostProcessManager(),
            adb);
}
