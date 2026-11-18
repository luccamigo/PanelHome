using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

internal sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; private set; }

    public void SetText(string text) => Text = text;
}

[TestClass]
public sealed class DiagnosticsViewModelTests
{
    [TestMethod]
    public async Task DriverAndAgentAreBothReported()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Running,
                ProcessId = 321,
                OwnedByControlCenter = true,
            },
            Executable = new PanelHostExecutable(
                @"C:\PanelOS\PanelHost.exe",
                PanelHostExecutableSource.ApplicationFolder),
        };
        var viewModel = Create(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.Rows.Any(
            row => row.Label == "PanelHost" && row.Value.Contains("321")));
        Assert.IsTrue(viewModel.Rows.Any(
            row => row.Label == "Executável do PanelHost" &&
                row.Value.Contains("PanelHost.exe")));
        Assert.IsTrue(viewModel.Rows.Any(row => row.Label == "Logs do PanelHost"));
        Assert.AreEqual(7, viewModel.RuntimeMetrics.Count);
    }

    [TestMethod]
    public async Task AnAgentThatCrashedShowsItsExitCode()
    {
        var host = new FakePanelHostProcessManager
        {
            Status = new PanelHostStatus
            {
                State = PanelHostProcessState.Error,
                LastExitCode = 7,
                LastRunDuration = TimeSpan.FromSeconds(4),
            },
        };
        var viewModel = Create(host: host);

        await viewModel.RefreshAsync(CancellationToken.None);

        var row = viewModel.Rows.First(
            entry => entry.Label == "Última saída do PanelHost");
        StringAssert.Contains(row.Value, "7");
    }

    [TestMethod]
    public void ExistingLogLinesAreShownWhenTheScreenOpens()
    {
        var host = new FakePanelHostProcessManager();
        host.Log.Append(PanelHostLogStream.StandardOutput, "linha anterior");

        var viewModel = Create(host: host);

        Assert.AreEqual(1, viewModel.LogEntries.Count);
        Assert.IsTrue(viewModel.HasLogEntries);
    }

    [TestMethod]
    public void NewLinesAppearAsTheAgentPrintsThem()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = Create(host: host, clipboard: new FakeClipboardService());

        host.Log.Append(PanelHostLogStream.StandardOutput, "[PanelHost] pronto");

        Assert.AreEqual(1, viewModel.LogEntries.Count);
        StringAssert.Contains(viewModel.LogEntries[0].Display, "[PanelHost] pronto");
        Assert.IsTrue(viewModel.CopyLogCommand.CanExecute(null));
    }

    [TestMethod]
    public void TheVisibleLogIsBounded()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = Create(host: host);

        for (var index = 0; index < 1200; index++)
        {
            host.Log.Append(PanelHostLogStream.StandardOutput, $"linha {index}");
        }

        Assert.AreEqual(PanelHostLogBuffer.DefaultCapacity, viewModel.LogEntries.Count);
        StringAssert.Contains(viewModel.LogEntries[^1].Text, "1199");
    }

    [TestMethod]
    public void CopyPutsTheVisibleLinesOnTheClipboard()
    {
        var host = new FakePanelHostProcessManager();
        var clipboard = new FakeClipboardService();
        var viewModel = Create(host: host, clipboard: clipboard);
        host.Log.Append(PanelHostLogStream.StandardOutput, "primeira");
        host.Log.Append(PanelHostLogStream.StandardError, "segunda");

        viewModel.CopyLogCommand.Execute(null);

        StringAssert.Contains(clipboard.Text!, "primeira");
        StringAssert.Contains(clipboard.Text!, "segunda");
    }

    [TestMethod]
    public void ClearingTheViewKeepsTheAgentRecording()
    {
        var host = new FakePanelHostProcessManager();
        var viewModel = Create(host: host);
        host.Log.Append(PanelHostLogStream.StandardOutput, "antes");

        viewModel.ClearLogViewCommand.Execute(null);

        Assert.AreEqual(0, viewModel.LogEntries.Count);
        Assert.IsFalse(viewModel.HasLogEntries);
        Assert.IsFalse(viewModel.ClearLogViewCommand.CanExecute(null));

        host.Log.Append(PanelHostLogStream.StandardOutput, "depois");

        Assert.AreEqual(1, viewModel.LogEntries.Count);
    }

    private static DiagnosticsViewModel Create(
        FakePanelDriverController? driver = null,
        FakePanelHostProcessManager? host = null,
        FakeClipboardService? clipboard = null) =>
        new(
            driver ?? new FakePanelDriverController(),
            new InMemoryStreamDeckConfigurationStore(),
            host ?? new FakePanelHostProcessManager(),
            clipboard);
}
