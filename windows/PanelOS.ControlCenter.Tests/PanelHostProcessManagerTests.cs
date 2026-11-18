using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.Host;
using PanelOS.Windows.Development;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class PanelHostProcessManagerTests
{
    private FakePanelHostExecutableLocator _locator = new();
    private FakePanelHostProcessController _controller = new();
    private PanelHostLogBuffer _log = new();
    private TestTimeProvider _time = new();

    [TestInitialize]
    public void Reset()
    {
        _locator = new FakePanelHostExecutableLocator();
        _controller = new FakePanelHostProcessController();
        _log = new PanelHostLogBuffer();
        _time = new TestTimeProvider();
    }

    [TestMethod]
    public void NothingRunningIsReportedAsStopped()
    {
        using var manager = CreateManager();

        var status = manager.Refresh();

        Assert.AreEqual(PanelHostProcessState.Stopped, status.State);
        Assert.IsFalse(status.IsRunning);
        Assert.IsNull(status.ProcessId);
        Assert.IsFalse(status.OwnedByControlCenter);
    }

    [TestMethod]
    public async Task StartLaunchesTheAgentAndTakesOwnership()
    {
        using var manager = CreateManager();

        var result = await manager.StartAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, _controller.Started.Count);
        Assert.IsTrue(_controller.Last!.OutputStarted);
        var status = manager.Status;
        Assert.AreEqual(PanelHostProcessState.Running, status.State);
        Assert.AreEqual(_controller.Last.Id, status.ProcessId);
        Assert.IsTrue(status.OwnedByControlCenter);
        Assert.AreEqual(@"C:\PanelOS\PanelHost.exe", status.ExecutablePath);
    }

    [TestMethod]
    public async Task TheAgentIsStartedDirectlyAndNeverThroughAShell()
    {
        using var manager = CreateManager();

        await manager.StartAsync(CancellationToken.None);

        var startInfo = _controller.LastStartInfo!;
        Assert.AreEqual(@"C:\PanelOS\PanelHost.exe", startInfo.FileName);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
        Assert.AreEqual(string.Empty, startInfo.Arguments);

        // Launched with no arguments the agent picks its development carrier,
        // which a plain start has no way to make reachable. The product carrier
        // is stated instead.
        CollectionAssert.AreEqual(
            new[] { "--transport", "usb" },
            startInfo.ArgumentList.ToArray());
        Assert.IsFalse(startInfo.Environment.ContainsKey(
            DevelopmentSessionToken.HostEnvironmentVariable));
    }

    [TestMethod]
    public async Task Development_token_reaches_the_host_only_through_its_environment()
    {
        var session = new DevelopmentSessionToken(() => Enumerable.Repeat((byte)0x5a, 32).ToArray());
        var token = session.Rotate();
        using var manager = CreateManager(session);
        manager.Transport = PanelHostTransport.AdbTcp;

        await manager.StartAsync(CancellationToken.None);

        var startInfo = _controller.LastStartInfo!;
        Assert.AreEqual(token, startInfo.Environment[DevelopmentSessionToken.HostEnvironmentVariable]);
        CollectionAssert.AreEqual(
            new[] { "--transport", "adb" },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task The_product_carrier_is_never_given_a_development_credential()
    {
        var session = new DevelopmentSessionToken(
            () => Enumerable.Repeat((byte)0x5a, 32).ToArray());
        session.Rotate();
        using var manager = CreateManager(session);

        await manager.StartAsync(CancellationToken.None);

        // A token in the environment is what makes the agent open the ADB
        // carrier's gate; the USB carrier has no use for one and is not handed
        // a credential just because a development session happened earlier.
        var startInfo = _controller.LastStartInfo!;
        Assert.IsFalse(startInfo.Environment.ContainsKey(
            DevelopmentSessionToken.HostEnvironmentVariable));
        CollectionAssert.AreEqual(
            new[] { "--transport", "usb" },
            startInfo.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task The_launcher_mints_a_token_when_no_development_session_ran()
    {
        // PanelHost refuses to open the ADB/TCP carrier without this credential
        // and exits, which is what "the agent will not start" looks like from
        // the window. The launcher supplies it.
        var session = new DevelopmentSessionToken(
            () => Enumerable.Repeat((byte)0x27, 32).ToArray());
        using var manager = CreateManager(session);
        manager.Transport = PanelHostTransport.AdbTcp;

        await manager.StartAsync(CancellationToken.None);

        var supplied = _controller.LastStartInfo!
            .Environment[DevelopmentSessionToken.HostEnvironmentVariable];
        Assert.IsFalse(string.IsNullOrWhiteSpace(supplied));
        Assert.AreEqual(session.CurrentToken, supplied);
    }

    [TestMethod]
    public async Task An_agent_that_explains_itself_is_reported_with_the_reason()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);

        var process = _controller.Started[0];
        process.EmitOutput(
            PanelHostLogStream.StandardError,
            "[PanelHost] faltou o token da sessão.");
        process.SimulateExit(2);

        // The exit code alone sends the operator to the log; the agent already
        // said what was wrong, so the status repeats it.
        StringAssert.Contains(manager.Status.Detail!, "código 2");
        StringAssert.Contains(manager.Status.Detail!, "faltou o token da sessão.");
    }

    [TestMethod]
    public async Task StartingTwiceDoesNotLaunchASecondAgent()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);

        var second = await manager.StartAsync(CancellationToken.None);

        Assert.IsFalse(second.Success);
        Assert.AreEqual(PanelHostErrorCodes.AlreadyRunning, second.ErrorCode);
        Assert.AreEqual(1, _controller.Started.Count);
    }

    [TestMethod]
    public async Task AnExternalAgentBlocksStartingAnother()
    {
        _controller.Running.Add(new PanelHostProcessInfo(
            777,
            DateTimeOffset.Now,
            @"C:\Outro\PanelHost.exe"));
        using var manager = CreateManager();

        var result = await manager.StartAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.AlreadyRunning, result.ErrorCode);
        Assert.AreEqual(0, _controller.Started.Count);
        Assert.AreEqual(777, manager.Status.ProcessId);
        Assert.IsFalse(manager.Status.OwnedByControlCenter);
        Assert.IsTrue(manager.Status.IsExternal);
    }

    [TestMethod]
    public void AnExternalAgentIsDetectedButNotAdopted()
    {
        _controller.Running.Add(new PanelHostProcessInfo(
            42,
            DateTimeOffset.Now,
            @"C:\Outro\PanelHost.exe"));
        using var manager = CreateManager();

        var status = manager.Refresh();

        Assert.AreEqual(PanelHostProcessState.Running, status.State);
        Assert.AreEqual(42, status.ProcessId);
        Assert.IsFalse(status.OwnedByControlCenter);
        StringAssert.Contains(status.Detail!, "fora do Control Center");
    }

    [TestMethod]
    public async Task StopEndsAnAgentTheManagerStarted()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        var process = _controller.Last!;

        var result = await manager.StopAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsTrue(process.StopRequested);
        Assert.AreEqual(PanelHostProcessState.Stopped, manager.Status.State);
    }

    [TestMethod]
    public async Task StopRefusesAnAgentItDoesNotOwn()
    {
        _controller.Running.Add(new PanelHostProcessInfo(
            555,
            DateTimeOffset.Now,
            null));
        using var manager = CreateManager();
        manager.Refresh();

        var result = await manager.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.NotOwned, result.ErrorCode);
        Assert.AreEqual(PanelHostProcessState.Running, manager.Status.State);
    }

    [TestMethod]
    public async Task StopWithoutAnAgentIsReported()
    {
        using var manager = CreateManager();

        var result = await manager.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.NotRunning, result.ErrorCode);
    }

    [TestMethod]
    public async Task AnAgentThatIgnoresTheStopIsReportedAsAFailure()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        _controller.Last!.StopSucceeds = false;

        var result = await manager.StopAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.StopFailed, result.ErrorCode);
        Assert.AreEqual(PanelHostProcessState.Error, manager.Status.State);
    }

    [TestMethod]
    public async Task RestartReplacesTheProcess()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        var first = manager.Status.ProcessId;

        var result = await manager.RestartAsync(CancellationToken.None);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(2, _controller.Started.Count);
        Assert.AreNotEqual(first, manager.Status.ProcessId);
        Assert.IsTrue(manager.Status.OwnedByControlCenter);
    }

    [TestMethod]
    public async Task RestartRefusesAnExternalAgent()
    {
        _controller.Running.Add(new PanelHostProcessInfo(
            9,
            DateTimeOffset.Now,
            null));
        using var manager = CreateManager();

        var result = await manager.RestartAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.NotOwned, result.ErrorCode);
        Assert.AreEqual(0, _controller.Started.Count);
    }

    [TestMethod]
    public async Task AMissingExecutableIsReportedInsteadOfThrowing()
    {
        _locator.Executable = null;
        using var manager = CreateManager();

        var result = await manager.StartAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.ExecutableNotFound, result.ErrorCode);
        Assert.AreEqual(PanelHostProcessState.Error, manager.Status.State);
        StringAssert.Contains(manager.Status.Detail!, "PanelHost.exe");
    }

    [TestMethod]
    public async Task AFailedLaunchIsReportedInsteadOfThrowing()
    {
        _controller.StartFailure = new InvalidOperationException("acesso negado");
        using var manager = CreateManager();

        var result = await manager.StartAsync(CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(PanelHostErrorCodes.StartFailed, result.ErrorCode);
        StringAssert.Contains(result.Message, "acesso negado");
    }

    [TestMethod]
    public async Task AnUnexpectedExitBecomesAnErrorWithItsCodeAndDuration()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(12));

        _controller.Last!.SimulateExit(3);

        var status = manager.Status;
        Assert.AreEqual(PanelHostProcessState.Error, status.State);
        Assert.AreEqual(3, status.LastExitCode);
        Assert.AreEqual(TimeSpan.FromSeconds(12), status.LastRunDuration);
        StringAssert.Contains(status.Detail!, "inesperadamente");
        StringAssert.Contains(_log.ToPlainText(), "inesperadamente");
    }

    [TestMethod]
    public async Task AnExpectedExitIsNotReportedAsACrash()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);

        await manager.StopAsync(CancellationToken.None);

        Assert.AreEqual(PanelHostProcessState.Stopped, manager.Status.State);
        Assert.IsNull(manager.Status.Detail);
    }

    [TestMethod]
    public async Task AnErrorStateSurvivesARefresh()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        _controller.Last!.SimulateExit(1);

        var status = manager.Refresh();

        Assert.AreEqual(PanelHostProcessState.Error, status.State);
        StringAssert.Contains(status.Detail!, "inesperadamente");
    }

    [TestMethod]
    public async Task OutputFromTheOwnedAgentIsCaptured()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);

        _controller.Last!.EmitOutput(
            PanelHostLogStream.StandardOutput,
            "[PanelHost] Listening only on 127.0.0.1:8765");
        _controller.Last.EmitOutput(PanelHostLogStream.StandardError, "aviso");

        var lines = _log.Snapshot();
        Assert.IsTrue(lines.Any(line => line.Text.Contains("127.0.0.1:8765")));
        Assert.IsTrue(lines.Any(
            line => line.Stream == PanelHostLogStream.StandardError));
    }

    [TestMethod]
    public async Task AutomaticRestartIsOffByDefault()
    {
        using var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);

        _controller.Last!.SimulateExit(1);

        Assert.AreEqual(1, _controller.Started.Count);
        Assert.AreEqual(PanelHostProcessState.Error, manager.Status.State);
    }

    [TestMethod]
    public async Task AutomaticRestartStopsAfterTheAllowedAttempts()
    {
        using var manager = CreateManager();
        manager.RestartPolicy = PanelHostRestartPolicy.Default;
        await manager.StartAsync(CancellationToken.None);

        // Three crashes are retried; the fourth is not, so a broken agent
        // cannot spin forever.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var process = _controller.Last!;
            process.SimulateExit(1);
            await WaitForRestartAsync(_controller, attempt + 2);
        }

        Assert.AreEqual(4, _controller.Started.Count);
        Assert.AreEqual(PanelHostProcessState.Error, manager.Status.State);
        StringAssert.Contains(manager.Status.Detail!, "pausado");
        StringAssert.Contains(_log.ToPlainText(), "Reinício automático pausado");
    }

    [TestMethod]
    public async Task RestartAttemptsAgeOutOfTheWindow()
    {
        using var manager = CreateManager();
        manager.RestartPolicy = PanelHostRestartPolicy.Default;
        await manager.StartAsync(CancellationToken.None);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            _controller.Last!.SimulateExit(1);
            await WaitForRestartAsync(_controller, attempt + 2);
            _time.Advance(TimeSpan.FromSeconds(61));
        }

        Assert.AreEqual(4, _controller.Started.Count);
        Assert.AreEqual(PanelHostProcessState.Running, manager.Status.State);
    }

    [TestMethod]
    public async Task DisposingDoesNotEndTheAgent()
    {
        var manager = CreateManager();
        await manager.StartAsync(CancellationToken.None);
        var process = _controller.Last!;

        manager.Dispose();

        Assert.IsFalse(
            process.StopRequested,
            "fechar o Control Center não derruba o PanelHost");
        Assert.IsTrue(process.Disposed, "o handle é liberado");
    }

    private static async Task WaitForRestartAsync(
        FakePanelHostProcessController controller,
        int expectedCount)
    {
        // The manager restarts on a pool thread; give it a bounded moment.
        for (var attempt = 0; attempt < 100 && controller.Started.Count < expectedCount;
            attempt++)
        {
            await Task.Delay(10);
        }
    }

    private PanelHostProcessManager CreateManager(
        IDevelopmentSessionTokenProvider? developmentSession = null) =>
        new(_locator, _controller, _log, _time, developmentSession);
}
