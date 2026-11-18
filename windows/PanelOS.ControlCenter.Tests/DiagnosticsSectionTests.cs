using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The Diagnostics summary: a failure said in Portuguese first, with the raw
/// form kept for whoever needs it.
/// </summary>
[TestClass]
public sealed class DiagnosticsSectionTests
{
    [TestMethod]
    public async Task AccessDeniedIsTranslatedAndTheCodeIsKept()
    {
        var viewModel = Create(new PanelDriverSnapshot
        {
            Availability = PanelDriverAvailability.AccessDenied,
            Detail = "ERROR_ACCESS_DENIED (5)",
        });

        await viewModel.RefreshAsync(CancellationToken.None);

        var summary = viewModel.Groups.Single(
            group => group.Section == DiagnosticSection.Summary);
        var driver = summary.Rows.First();

        StringAssert.Contains(driver.Value, "acesso negado");
        Assert.AreEqual(PanelStatusLevel.Bad, driver.Level);

        // The Win32 form is not the headline, but it is not thrown away either.
        Assert.IsTrue(driver.HasTechnical);
        Assert.AreEqual("ERROR_ACCESS_DENIED (5)", driver.Technical);
        Assert.IsTrue(driver.HasHint);
        StringAssert.Contains(driver.Hint!, "administrador");
    }

    [TestMethod]
    public async Task TheSummaryNamesThePipeItCouldNotReach()
    {
        var viewModel = Create();

        await viewModel.RefreshAsync(CancellationToken.None);

        var management = viewModel.Rows.Single(
            row => row.Label == "Canal de gerenciamento");
        StringAssert.Contains(management.Value, "Não foi possível conectar");
        StringAssert.Contains(management.Technical!, "PanelOS.Host.v1");
    }

    [TestMethod]
    public async Task TheTransportSectionDoesNotGuessHowTheTabletConnected()
    {
        var viewModel = Create();

        await viewModel.RefreshAsync(CancellationToken.None);

        var transport = viewModel.Groups.Single(
            group => group.Section == DiagnosticSection.Transport);
        var row = transport.Rows.Single(item => item.Label == "Transporte");

        // The agent accepts the session over a local socket; which physical
        // path the tablet used is not in the snapshot, so it is reported as
        // unpublished instead of inferred from the port.
        StringAssert.Contains(row.Value, "Não publicado");
        Assert.IsTrue(row.HasHint);
    }

    [TestMethod]
    public async Task TheVideoSectionKeepsEveryRateEvenWhenNoneWereMeasured()
    {
        var viewModel = Create();

        await viewModel.RefreshAsync(CancellationToken.None);

        var video = viewModel.Groups.Single(
            group => group.Section == DiagnosticSection.Video);

        Assert.IsTrue(video.IsVideo);
        foreach (var label in new[]
        {
            "Captura do driver",
            "Recebido pelo host",
            "Enviado ao tablet",
            "Renderizado no tablet",
            "Frames perdidos",
        })
        {
            var row = video.Rows.Single(item => item.Label == label);
            Assert.AreEqual("—", row.Value, label);
        }

        // The tablet's own render rate is absent by construction and says why.
        var render = video.Rows.Single(item => item.Label == "Renderizado no tablet");
        StringAssert.Contains(render.Hint!, "Management v1");
    }

    [TestMethod]
    public async Task MeasuredVideoRatesReachTheVideoSection()
    {
        var management = new FakeManagementClient();
        var viewModel = new DiagnosticsViewModel(
            new FakePanelDriverController(),
            new InMemoryStreamDeckConfigurationStore(),
            new FakePanelHostProcessManager(),
            managementClient: management);

        management.Publish(Snapshots.Healthy());
        await viewModel.RefreshAsync(CancellationToken.None);

        var video = viewModel.Groups.Single(
            group => group.Section == DiagnosticSection.Video);
        Assert.AreEqual(
            "59,1 FPS",
            video.Rows.Single(row => row.Label == "Enviado ao tablet").Value);
        Assert.AreEqual(
            "0 frames",
            video.Rows.Single(row => row.Label == "Frames perdidos").Value);
    }

    [TestMethod]
    public async Task EveryRowThatCarriesAPathAlsoCarriesItForCopying()
    {
        var viewModel = Create();

        await viewModel.RefreshAsync(CancellationToken.None);

        var files = viewModel.Groups.Single(
            group => group.Section == DiagnosticSection.Files);
        Assert.IsTrue(files.Rows.Count > 0);
        Assert.IsTrue(files.Rows.All(row => row.HasTechnical));
    }

    private static DiagnosticsViewModel Create(PanelDriverSnapshot? snapshot = null) =>
        new(
            new FakePanelDriverController
            {
                Snapshot = snapshot ?? new PanelDriverSnapshot
                {
                    Availability = PanelDriverAvailability.Unknown,
                },
            },
            new InMemoryStreamDeckConfigurationStore(),
            new FakePanelHostProcessManager());
}
