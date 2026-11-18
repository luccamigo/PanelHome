using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Driver;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public void TheShellExposesItsSectionsWithOverviewSelected()
    {
        var (viewModel, _) = Create();

        CollectionAssert.AreEqual(
            new[]
            {
                "Visão geral",
                "Dispositivo",
                "Stream Deck",
                "Diagnóstico",
                "Mídia em tela cheia",
                "Configurações",
            },
            viewModel.Sections.Select(section => section.Name).ToArray());
        Assert.AreSame(viewModel.Overview, viewModel.SelectedSection.Content);
        Assert.AreEqual("Consultando driver…", viewModel.DriverStatusText);
        Assert.AreEqual(PanelStatusLevel.Neutral, viewModel.DriverStatusLevel);
    }

    [TestMethod]
    public void EverySectionCarriesItsOwnHeaderAndIcon()
    {
        var (viewModel, _) = Create();

        // The page header is written once in the shell, so a section without a
        // description would render a blank line on its page.
        foreach (var section in viewModel.Sections)
        {
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(section.Description),
                $"{section.Name} precisa de uma descrição");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(section.IconKey),
                $"{section.Name} precisa de um ícone");
        }

        CollectionAssert.AllItemsAreUnique(
            viewModel.Sections.Select(section => section.IconKey).ToArray());
    }

    [TestMethod]
    public void AQuickActionOpensThePageThatOwnsTheTask()
    {
        var (viewModel, _) = Create();

        viewModel.Device.OpenStreamDeckCommand.Execute(null);
        Assert.AreEqual(PanelSection.StreamDeck, viewModel.SelectedSection.Section);
        Assert.AreSame(viewModel.StreamDeck, viewModel.SelectedSection.Content);

        viewModel.Device.OpenLogsCommand.Execute(null);
        Assert.AreEqual(PanelSection.Diagnostics, viewModel.SelectedSection.Section);
    }

    [TestMethod]
    public void NavigateSelectsTheRequestedSection()
    {
        var (viewModel, _) = Create();

        viewModel.Navigate(PanelSection.Settings);

        Assert.AreEqual(PanelSection.Settings, viewModel.SelectedSection.Section);
        Assert.AreSame(viewModel.Settings, viewModel.SelectedSection.Content);
    }

    [TestMethod]
    public async Task AMissingDriverIsAnnouncedInTheFooter()
    {
        var (viewModel, driver) = Create();
        driver.Snapshot = new PanelDriverSnapshot
        {
            Availability = PanelDriverAvailability.NotInstalled,
        };

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual("Driver não encontrado", viewModel.DriverStatusText);
        Assert.AreEqual(PanelStatusLevel.Bad, viewModel.DriverStatusLevel);
    }

    [TestMethod]
    public async Task AConnectedDisplayIsAnnouncedInTheFooter()
    {
        var (viewModel, driver) = Create();
        driver.Snapshot = new PanelDriverSnapshot
        {
            Availability = PanelDriverAvailability.Available,
            Lifecycle = PanelDriverLifecycle.Ready,
            Monitor = PanelMonitorConnection.Connected,
        };

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.AreEqual("Display conectado", viewModel.DriverStatusText);
        Assert.AreEqual(PanelStatusLevel.Good, viewModel.DriverStatusLevel);
    }

    [TestMethod]
    public async Task InitializeLoadsEverySectionEvenWithoutADriver()
    {
        var (viewModel, driver) = Create();
        driver.Snapshot = new PanelDriverSnapshot
        {
            Availability = PanelDriverAvailability.NotInstalled,
            Detail = "interface não encontrada",
        };

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.IsTrue(viewModel.Diagnostics.Rows.Count > 0);
        Assert.AreEqual(12, viewModel.StreamDeck.Cells.Count);
        Assert.AreEqual(7, viewModel.Diagnostics.RuntimeMetrics.Count);
        Assert.IsTrue(viewModel.Diagnostics.Rows.Any(
            row => row.Value == "não encontrada"));
    }

    [TestMethod]
    public async Task TheDashboardCardFollowsWhatTheEditorPersisted()
    {
        var (viewModel, _) = Create();

        await viewModel.InitializeAsync(CancellationToken.None);

        // The shell wires the editor's summary into the Dashboard, so the card
        // reports the saved configuration without reading the file itself.
        Assert.AreEqual("Configurado", viewModel.Device.StreamDeckStatusText);
        StringAssert.Contains(viewModel.Device.StreamDeckDetail, "Main");
    }

    [TestMethod]
    public void TheHeaderStripCarriesTheFourComponentsOnEveryPage()
    {
        var (viewModel, _) = Create();

        // The strip answers "is the panel working?" without opening a page,
        // so it is populated before anything has been refreshed.
        CollectionAssert.AreEqual(
            new[] { "Agente", "Tablet", "Display", "Vídeo" },
            viewModel.Statuses.Select(status => status.Label).ToArray());
    }

    [TestMethod]
    public async Task TheHeaderStripFollowsTheDeviceState()
    {
        var (viewModel, driver) = Create();
        driver.Snapshot = new PanelDriverSnapshot
        {
            Availability = PanelDriverAvailability.Available,
            Lifecycle = PanelDriverLifecycle.Ready,
            Monitor = PanelMonitorConnection.Connected,
        };

        await viewModel.InitializeAsync(CancellationToken.None);

        var display = viewModel.Statuses.Single(status => status.Label == "Display");
        Assert.AreEqual("Conectado", display.Text);
        Assert.AreEqual(PanelStatusLevel.Good, display.Level);

        // Nothing observes the tablet or the video pipeline without the
        // management channel, and the strip says so rather than inventing one.
        Assert.AreEqual(
            "Indisponível",
            viewModel.Statuses.Single(status => status.Label == "Tablet").Text);
        Assert.AreEqual(
            "—",
            viewModel.Statuses.Single(status => status.Label == "Vídeo").Text);
    }

    [TestMethod]
    public void WifiAppearsAfterStreamDeckWhenItsViewModelIsGivenAndNeverOtherwise()
    {
        var (without, _) = Create();
        Assert.IsFalse(without.Sections.Any(section => section.Section == PanelSection.Wifi));

        var driver = new FakePanelDriverController();
        var configurationStore = new InMemoryStreamDeckConfigurationStore();
        var panelHost = new FakePanelHostProcessManager();
        var device = new DashboardViewModel(driver, panelHost);
        var wifi = new WifiPairingViewModel(
            new PanelOS.Windows.Wifi.WifiAdminClient("PanelOS.Wifi.Admin.test.absent"),
            new QrCoderEncoder(),
            new ConfirmationService());
        var withWifi = new MainViewModel(
            new OverviewViewModel(device),
            device,
            new StreamDeckEditorViewModel(configurationStore),
            new DiagnosticsViewModel(driver, configurationStore, panelHost),
            new SettingsViewModel(new InMemoryControlCenterSettingsStore(), new FakeStartupRegistration(), panelHost),
            CreateLoopMedia(),
            wifi);

        CollectionAssert.AreEqual(
            new[] { PanelSection.Overview, PanelSection.Device, PanelSection.StreamDeck, PanelSection.Wifi, PanelSection.Diagnostics, PanelSection.LoopMedia, PanelSection.Settings },
            withWifi.Sections.Select(section => section.Section).ToArray());
        Assert.AreSame(wifi, withWifi.Sections.Single(section => section.Section == PanelSection.Wifi).Content);
        CollectionAssert.AllItemsAreUnique(withWifi.Sections.Select(section => section.IconKey).ToArray());
    }
    private sealed class ScriptedWifiClient : PanelOS.Windows.Wifi.IWifiAdminClient
    {
        public PanelOS.Windows.Wifi.WifiAdminPending? Pending;
        public Task<PanelOS.Windows.Wifi.WifiAdminResponse> SendAsync(PanelOS.Windows.Wifi.WifiAdminRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PanelOS.Windows.Wifi.WifiAdminResponse(true, null, new PanelOS.Windows.Wifi.WifiAdminStatus(true, 100, "192.168.15.4", 40002, 40001,
                new("qr", "Pending", 500, "{}", null, Pending), [])));
    }

    private sealed class OneModuleQr : IQrEncoder
    {
        public IReadOnlyList<string> Encode(string text) => ["1"];
    }

    [TestMethod]
    public async Task The_wifi_entry_shows_an_attention_dot_exactly_while_a_request_waits()
    {
        var driver = new FakePanelDriverController();
        var configurationStore = new InMemoryStreamDeckConfigurationStore();
        var panelHost = new FakePanelHostProcessManager();
        var device = new DashboardViewModel(driver, panelHost);
        var client = new ScriptedWifiClient();
        var wifi = new WifiPairingViewModel(client, new OneModuleQr(), new ConfirmationService(), (_, _) => Task.CompletedTask);
        var main = new MainViewModel(
            new OverviewViewModel(device), device, new StreamDeckEditorViewModel(configurationStore),
            new DiagnosticsViewModel(driver, configurationStore, panelHost),
            new SettingsViewModel(new InMemoryControlCenterSettingsStore(), new FakeStartupRegistration(), panelHost), CreateLoopMedia(), wifi);
        var entry = main.Sections.Single(section => section.Section == PanelSection.Wifi);
        Assert.IsFalse(entry.HasAttention);
        Assert.IsFalse(main.Sections.Where(section => section != entry).Any(section => section.HasAttention));

        client.Pending = new("Tablet", new string('A', 64), 50);
        await wifi.RefreshAsync(CancellationToken.None);
        Assert.IsTrue(entry.HasAttention, "A request is waiting: the dot shows on the menu even from another page.");

        client.Pending = null;
        await wifi.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(entry.HasAttention, "It goes away when the request is decided or expires.");
    }
    private static (MainViewModel ViewModel, FakePanelDriverController Driver) Create()
    {
        var driver = new FakePanelDriverController();
        var configurationStore = new InMemoryStreamDeckConfigurationStore();
        var panelHost = new FakePanelHostProcessManager();
        var device = new DashboardViewModel(driver, panelHost);
        var viewModel = new MainViewModel(
            new OverviewViewModel(device),
            device,
            new StreamDeckEditorViewModel(configurationStore),
            new DiagnosticsViewModel(driver, configurationStore, panelHost),
            new SettingsViewModel(
                new InMemoryControlCenterSettingsStore(),
                new FakeStartupRegistration(),
                panelHost),
            CreateLoopMedia());
        return (viewModel, driver);
    }

    private static LoopMediaViewModel CreateLoopMedia()
    {
        // Explicit isolated paths: shell tests must not read the operator's media configuration.
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PanelOS.MainViewModel.Tests", Guid.NewGuid().ToString("N"));
        return new LoopMediaViewModel(
            new PanelOS.Windows.LoopMedia.JsonLoopMediaStore(
                System.IO.Path.Combine(directory, "media.json"), System.IO.Path.Combine(directory, "media")),
            new LoopMediaFilePicker());
    }

    [TestMethod]
    public void TheMediaPageNavigatesWithoutReplacingThePhase1Sections()
    {
        var (main, _) = Create();
        main.Navigate(PanelSection.LoopMedia);
        Assert.AreSame(main.LoopMedia, main.SelectedSection.Content);
        main.Navigate(PanelSection.Diagnostics);
        Assert.AreSame(main.Diagnostics, main.SelectedSection.Content);
        Assert.AreEqual("IconVideo", main.Sections.Single(section => section.Section == PanelSection.LoopMedia).IconKey);
    }
}
