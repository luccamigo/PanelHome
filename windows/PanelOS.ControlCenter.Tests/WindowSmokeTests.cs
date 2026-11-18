using System.Windows;
using System.IO;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.ControlCenter.Views;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// XAML resource keys are resolved at load time, not at build time. This loads
/// the application resources and every view once so a missing style or
/// converter fails here instead of in front of the user.
/// </summary>
[TestClass]
public sealed class WindowSmokeTests
{
    [TestMethod]
    public void EveryViewLoadsAgainstTheApplicationResources()
    {
        RunOnStaThread(() =>
        {
            var application = Application.Current ?? new App();
            if (application.Resources.MergedDictionaries.Count == 0)
            {
                // Loads App.xaml: the theme dictionary and the section templates.
                ((App)application).InitializeComponent();
            }

            Assert.IsNotNull(application.TryFindResource("PanelBackgroundBrush"));
            Assert.IsNotNull(application.TryFindResource("StatusLevelToBrush"));
            Assert.IsNotNull(application.TryFindResource("StatusLevelToGeometry"));
            Assert.IsNotNull(application.TryFindResource("PanelSectionCard"));
            Assert.IsNotNull(application.TryFindResource("PanelDisclosure"));
            Assert.IsNotNull(application.TryFindResource("IconStatusGood"));

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
                new LoopMediaViewModel(new PanelOS.Windows.LoopMedia.JsonLoopMediaStore(
                    Path.Combine(Path.GetTempPath(), "PanelOS.Smoke", Guid.NewGuid().ToString("N"), "media.json"),
                    Path.Combine(Path.GetTempPath(), "PanelOS.Smoke", Guid.NewGuid().ToString("N"), "media")),
                    new LoopMediaFilePicker()));

            var window = new MainWindow(viewModel);
            Assert.AreSame(viewModel, window.DataContext);

            _ = new OverviewView();
            _ = new DeviceView();
            _ = new StreamDeckView();
            _ = new DiagnosticsView();
            _ = new SettingsView();
            _ = new LoopMediaView();
            _ = new WifiView();
            _ = new IntegrationsView();
            _ = new Controls.QrCodeControl();

            // The shared controls resolve theme resources of their own, and
            // both carry bindings a missing converter would only break here.
            _ = new Controls.StatusBadge();
            _ = new Controls.FactRow();
        });
    }

    [TestMethod]
    public void TheWifiPageLaysOutWithAnOpenInvitationAPendingRequestAndDevices()
    {
        RunOnStaThread(() =>
        {
            var application = Application.Current ?? new App();
            if (application.Resources.MergedDictionaries.Count == 0) ((App)application).InitializeComponent();
            var client = new StubWifiClient();
            var viewModel = new WifiPairingViewModel(client, new QrCoderEncoder(), new ConfirmationService(), (_, _) => Task.CompletedTask);
            viewModel.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
            var view = new WifiView { DataContext = viewModel };
            view.Measure(new Size(900, 3000));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();
            Assert.IsTrue(view.DesiredSize.Height > 100);
            var qr = FindChild<Controls.QrCodeControl>(view);
            Assert.IsNotNull(qr);
            Assert.IsTrue(qr!.ActualWidth > 100, "the QR is drawn at a readable size");
            Assert.AreEqual(qr.ActualWidth, qr.ActualHeight, "square tile");
        });
    }

    private sealed class StubWifiClient : PanelOS.Windows.Wifi.IWifiAdminClient
    {
        public Task<PanelOS.Windows.Wifi.WifiAdminResponse> SendAsync(PanelOS.Windows.Wifi.WifiAdminRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PanelOS.Windows.Wifi.WifiAdminResponse(true, null, new PanelOS.Windows.Wifi.WifiAdminStatus(
                true, 150, "192.168.15.4", 40002, 40001,
                new PanelOS.Windows.Wifi.WifiAdminPairing("qr", "Pending", 590, new string('x', 700), null,
                    new PanelOS.Windows.Wifi.WifiAdminPending("Tablet da sala", new string('A', 64), 40)),
                [new PanelOS.Windows.Wifi.WifiAdminDevice(new string('B', 64), "Tablet", "Active", DateTimeOffset.UtcNow, true)])));
    }

    [TestMethod]
    public void HookPreviewKeepsTheActionsInsideThePageAndNeverWritesOnLayout()
    {
        RunOnStaThread(() =>
        {
            var application = Application.Current ?? new Application();
            if (application.TryFindResource("PanelBackgroundBrush") is null)
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("/PanelOS.ControlCenter;component/Theme/PanelTheme.xaml", UriKind.Relative) });
            var root = Path.Combine(Path.GetTempPath(), "panelos-preview-layout-" + Guid.NewGuid().ToString("N"));
            var build = Path.Combine(root, "build");
            Directory.CreateDirectory(build);
            try
            {
                foreach (var name in new[] { "PanelOS.ClaudeHook.exe", "PanelOS.ClaudeHook.dll", "PanelOS.ClaudeHook.deps.json", "PanelOS.ClaudeHook.runtimeconfig.json" })
                    File.WriteAllText(Path.Combine(build, name), "fictional layout fixture");
                var settings = Path.Combine(root, "fictional-claude", "settings.json");
                var model = new IntegrationsViewModel(new StubLocalAdmin(),
                    new PanelOS.Windows.Management.ClaudeHookInstaller(settings, Path.Combine(build, "PanelOS.ClaudeHook.exe"), Path.Combine(root, "stable")),
                    new NoClipboard(), new NoConfirmation());
                model.RefreshAsync(CancellationToken.None).GetAwaiter().GetResult();
                model.PreviewInstallCommand.Execute(null);
                var view = new IntegrationsView { DataContext = model };
                view.Measure(new Size(900, 650));
                view.Arrange(new Rect(0, 0, 900, 650));
                view.UpdateLayout();
                Assert.IsTrue(model.DialogOpen);
                Assert.IsFalse(File.Exists(settings), "Binding and layout must never confirm an installation");
                var cancel = (Button)view.FindName("CancelHook");
                Assert.IsTrue(cancel.ActualHeight > 0);
                var top = cancel.TranslatePoint(new Point(), view).Y;
                Assert.IsTrue(top >= 0 && top + cancel.ActualHeight <= view.ActualHeight,
                    $"Cancel must fit: top={top}, height={cancel.ActualHeight}, page={view.ActualHeight}");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    private sealed class StubLocalAdmin : PanelOS.Windows.Management.ILocalIntegrationAdmin
    {
        public Task<PanelOS.Windows.Management.LocalIntegrationAdminResult> SendAsync(PanelOS.Windows.Management.LocalIntegrationAdminRequest request, CancellationToken token = default) =>
            Task.FromResult(new PanelOS.Windows.Management.LocalIntegrationAdminResult(false, 8777, "disabled", null, 0, null, 0, 60));
    }
    private sealed class NoClipboard : IClipboardService { public void SetText(string text) { } }
    private sealed class NoConfirmation : IConfirmationService { public bool Confirm(string title, string message) => false; }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            Assert.Fail(
                $"{failure.GetType().Name}: {failure.Message}");
        }
    }
}
