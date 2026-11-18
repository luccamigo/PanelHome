using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Host;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// Grouping of the diagnostic rows and the log viewer's filter, which is the
/// only part of Diagnostics with state of its own.
/// </summary>
[TestClass]
public sealed class DiagnosticsLogViewTests
{
    [TestMethod]
    public async Task RowsAreFiledUnderTheComponentTheyDescribe()
    {
        var viewModel = Create(out _);

        await viewModel.RefreshAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                "Resumo",
                "Host",
                "Transporte",
                "Display",
                "Vídeo",
                "Stream Deck",
                "Arquivos",
                "Logs",
            },
            viewModel.Groups.Select(group => group.Title).ToArray());

        // Grouping is presentation: every row still appears exactly once.
        Assert.AreEqual(
            viewModel.Rows.Count,
            viewModel.Groups.Sum(group => group.Rows.Count));

        // A section with nothing measured is left out rather than shown as a
        // heading over nothing. The log segment is the one exception: it
        // carries the viewer, not rows.
        Assert.IsTrue(viewModel.Groups.All(
            group => group.IsLogs || group.Rows.Count > 0));
        Assert.AreEqual(1, viewModel.Groups.Count(group => group.IsLogs));
        Assert.AreEqual(0, viewModel.Groups.Single(group => group.IsLogs).Rows.Count);
    }

    [TestMethod]
    public async Task TheLogIsNeverThePageTheOperatorLandsOn()
    {
        var viewModel = Create(out _);

        // Before the first refresh nothing has been measured, so the log is the
        // only group that exists. It still must not be the front door: a
        // diagnostics page that opens on a log is the dump this layout replaced.
        Assert.IsFalse(viewModel.IsLogsSelected);

        await viewModel.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(DiagnosticSection.Summary, viewModel.SelectedGroup!.Section);
        Assert.IsFalse(viewModel.IsLogsSelected);
    }

    [TestMethod]
    public async Task RefreshingKeepsTheSegmentTheOperatorWasReading()
    {
        var viewModel = Create(out _);
        await viewModel.RefreshAsync(CancellationToken.None);

        viewModel.SelectedGroup =
            viewModel.Groups.Single(group => group.Title == "Vídeo");
        await viewModel.RefreshAsync(CancellationToken.None);

        // The rows are rebuilt, so the group instance is new; what must
        // survive is which section is on screen.
        Assert.AreEqual(DiagnosticSection.Video, viewModel.SelectedGroup!.Section);
        Assert.IsFalse(viewModel.IsLogsSelected);
    }

    [TestMethod]
    public void TheStreamFilterNarrowsTheLogWithoutDiscardingIt()
    {
        var viewModel = Create(out var host);
        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] pronto");
        host.Log.Append(PanelHostLogStream.StandardError, "[Video] encoder falhou");
        host.Log.Append(PanelHostLogStream.Manager, "PanelHost iniciado");

        // The stream is the only severity the capture actually carries;
        // anything finer would mean parsing the text.
        viewModel.SelectedLogStream = viewModel.LogStreamFilters.Single(
            filter => filter.Stream == PanelHostLogStream.StandardError);

        Assert.AreEqual(3, viewModel.LogEntries.Count);
        Assert.AreEqual(1, viewModel.VisibleLogEntries.Count);
        Assert.IsTrue(viewModel.IsLogFiltered);
        StringAssert.Contains(viewModel.VisibleLogEntries[0].Text, "encoder falhou");

        viewModel.SelectedLogStream = viewModel.LogStreamFilters[0];

        Assert.AreEqual(3, viewModel.VisibleLogEntries.Count);
        Assert.IsFalse(viewModel.IsLogFiltered);
    }

    [TestMethod]
    public void AnEmptyLogSaysSoAndOffersNothingToCopy()
    {
        var viewModel = Create(out _);

        Assert.IsFalse(viewModel.HasLogEntries);
        Assert.IsFalse(viewModel.HasVisibleLogEntries);
        Assert.IsFalse(viewModel.IsLogFiltered);
        Assert.AreEqual("0 linhas", viewModel.LogCountText);
        Assert.IsFalse(viewModel.CopyLogCommand.CanExecute(null));
    }

    [TestMethod]
    public void TheFilterHidesLinesWithoutDiscardingThem()
    {
        var viewModel = Create(out var host);
        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] encoder pronto");
        host.Log.Append(PanelHostLogStream.StandardOutput, "[StreamDeck] invoke button=x");
        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] primeiro frame");

        viewModel.LogFilter = "streamdeck";

        Assert.AreEqual(3, viewModel.LogEntries.Count);
        Assert.AreEqual(1, viewModel.VisibleLogEntries.Count);
        Assert.IsTrue(viewModel.IsLogFiltered);
        Assert.AreEqual("1 de 3 linhas", viewModel.LogCountText);

        viewModel.LogFilter = string.Empty;

        Assert.AreEqual(3, viewModel.VisibleLogEntries.Count);
        Assert.AreEqual("3 linhas", viewModel.LogCountText);
    }

    [TestMethod]
    public void AFilterThatMatchesNothingIsDistinctFromAnEmptyLog()
    {
        var viewModel = Create(out var host);
        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] pronto");

        viewModel.LogFilter = "inexistente";

        // The view distinguishes "nothing captured" from "nothing matches", so
        // both facts have to be observable.
        Assert.IsTrue(viewModel.HasLogEntries);
        Assert.IsFalse(viewModel.HasVisibleLogEntries);
        Assert.IsFalse(viewModel.CopyLogCommand.CanExecute(null));
    }

    [TestMethod]
    public void ANewLineIsShownOnlyWhenItMatchesTheFilter()
    {
        var viewModel = Create(out var host);
        viewModel.LogFilter = "streamdeck";

        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] ignorado");
        Assert.AreEqual(0, viewModel.VisibleLogEntries.Count);

        host.Log.Append(PanelHostLogStream.StandardOutput, "[StreamDeck] success");
        Assert.AreEqual(1, viewModel.VisibleLogEntries.Count);
        Assert.AreEqual(2, viewModel.LogEntries.Count);
    }

    [TestMethod]
    public void ClearingTheViewEmptiesBothTheBufferAndTheFilteredList()
    {
        var viewModel = Create(out var host);
        host.Log.Append(PanelHostLogStream.StandardOutput, "[Video] pronto");

        viewModel.ClearLogViewCommand.Execute(null);

        Assert.AreEqual(0, viewModel.LogEntries.Count);
        Assert.AreEqual(0, viewModel.VisibleLogEntries.Count);
        Assert.IsFalse(viewModel.HasVisibleLogEntries);
    }

    [TestMethod]
    public void AutoScrollIsOnByDefault()
    {
        var viewModel = Create(out _);

        Assert.IsTrue(viewModel.AutoScrollLog);

        viewModel.AutoScrollLog = false;
        Assert.IsFalse(viewModel.AutoScrollLog);
    }

    private static DiagnosticsViewModel Create(out FakePanelHostProcessManager host)
    {
        host = new FakePanelHostProcessManager();
        return new DiagnosticsViewModel(
            new FakePanelDriverController(),
            new InMemoryStreamDeckConfigurationStore(),
            host);
    }
}
