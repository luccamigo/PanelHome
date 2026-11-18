using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.Management;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class IntegrationsViewModelTests
{
    private sealed class Admin : ILocalIntegrationAdmin
    {
        internal readonly List<LocalIntegrationAdminRequest> Requests = [];
        internal LocalIntegrationAdminResult Result = new(false, 8777, "disabled", null, 0, null, 0, 60);
        public Task<LocalIntegrationAdminResult> SendAsync(LocalIntegrationAdminRequest request, CancellationToken token = default)
        { Requests.Add(request); return Task.FromResult(Result); }
    }
    private sealed class Confirmation : IConfirmationService
    {
        internal bool Answer;
        public bool Confirm(string title, string message) => Answer;
    }
    private sealed class Clipboard : IClipboardService
    {
        internal string? Text;
        public void SetText(string text) => Text = text;
    }

    [TestMethod]
    public async Task SecurityReviewKeepsProcessGateDisabledAndSendsNothing()
    {
        using var fixture = new Fixture();
        await fixture.Model.RefreshAsync(CancellationToken.None);
        Assert.IsFalse(fixture.Model.ToggleProcessesCommand.CanExecute(null));
        await fixture.Model.ToggleProcessesCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, fixture.Admin.Requests.Count);
        Assert.AreEqual("status", fixture.Admin.Requests.Single().Action);
        StringAssert.Contains(fixture.Model.ProcessReason, "revisão");
    }

    [TestMethod]
    public async Task KeyIsShownOnlyAfterRotateAndNeverEntersAnExample()
    {
        using var fixture = new Fixture();
        await fixture.Model.RefreshAsync(CancellationToken.None);
        fixture.Admin.Result = fixture.Admin.Result with { KeySuffix = "fake", KeyGeneration = 1, OneTimeKey = "panelos_example_test_only" };
        await fixture.Model.RotateKeyCommand.ExecuteAsync(CancellationToken.None);
        Assert.IsTrue(fixture.Model.HasOneTimeKey);
        fixture.Model.CopyKeyCommand.Execute(null);
        Assert.AreEqual("panelos_example_test_only", fixture.Clipboard.Text);
        fixture.Model.CopyExampleCommand.Execute(null);
        StringAssert.Contains(fixture.Clipboard.Text!, "<chave>");
        Assert.IsFalse(fixture.Clipboard.Text!.Contains("panelos_example_test_only", StringComparison.Ordinal));
        fixture.Model.ClearSensitive();
        Assert.IsNull(fixture.Model.OneTimeKey);
        Assert.IsFalse(fixture.Model.CopyKeyCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task DecliningRotationDoesNotInvalidateTheKey()
    {
        using var fixture = new Fixture();
        fixture.Admin.Result = fixture.Admin.Result with { KeySuffix = "fake" };
        await fixture.Model.RefreshAsync(CancellationToken.None);
        await fixture.Model.RotateKeyCommand.ExecuteAsync(CancellationToken.None);
        Assert.AreEqual(1, fixture.Admin.Requests.Count);
    }

    [TestMethod]
    public void InstallPreviewAndCancelDoNotWriteTheSettingsFile()
    {
        using var fixture = new Fixture();
        fixture.Model.PreviewInstallCommand.Execute(null);
        Assert.IsTrue(fixture.Model.DialogOpen);
        StringAssert.Contains(fixture.Model.PreviewText, "PermissionRequest");
        Assert.IsFalse(File.Exists(fixture.Settings));
        fixture.Model.CancelHookCommand.Execute(null);
        Assert.IsFalse(fixture.Model.DialogOpen);
        Assert.IsFalse(File.Exists(fixture.Settings));
        Assert.AreEqual("", fixture.Model.PreviewText);
    }

    [TestMethod]
    public void ConfirmWritesExactlyTheReviewedPreviewOnlyInTheTemporaryFixture()
    {
        using var fixture = new Fixture();
        fixture.Model.PreviewInstallCommand.Execute(null);
        var preview = fixture.Model.PreviewText;
        fixture.Model.ConfirmHookCommand.Execute(null);
        Assert.AreEqual(preview, File.ReadAllText(fixture.Settings));
        Assert.IsFalse(fixture.Model.DialogOpen);
        Assert.AreEqual("Instalado", fixture.Model.HookStatus);
        Assert.IsTrue(File.Exists(fixture.Model.Backup));
        fixture.Model.PreviewRemovalCommand.Execute(null);
        fixture.Model.ConfirmHookCommand.Execute(null);
        Assert.IsFalse(File.Exists(fixture.Settings));
    }

    [TestMethod]
    public void PreviewShowsTheStableFolderAndEveryFileWithItsHashBeforeWriting()
    {
        using var fixture = new Fixture();
        fixture.Model.PreviewInstallCommand.Execute(null);
        StringAssert.Contains(fixture.Model.DialogBody, fixture.Stable);
        StringAssert.Contains(fixture.Model.DialogBody, "aparecem na tela do tablet");
        StringAssert.Contains(fixture.Model.DialogBody, "senhas ou tokens");
        Assert.IsTrue(fixture.Model.HasPreviewFiles);
        var lines = fixture.Model.PreviewFilesText.Split('\n');
        Assert.AreEqual(4, lines.Length);
        foreach (var line in lines) StringAssert.Matches(line, new System.Text.RegularExpressions.Regex(@"^PanelOS\.ClaudeHook\S* +[0-9A-F]{64} +\d+ bytes$"));
        StringAssert.Contains(fixture.Model.PreviewText, fixture.Stable.Replace("\\", "\\\\"));
        Assert.IsFalse(Directory.Exists(fixture.Stable));
        Assert.IsFalse(File.Exists(fixture.Settings));
        fixture.Model.CancelHookCommand.Execute(null);
        Assert.IsFalse(fixture.Model.HasPreviewFiles);
    }

    [TestMethod]
    public async Task OutdatedAndChangedHookFilesAreReportedWithWhatToDo()
    {
        using var fixture = new Fixture();
        fixture.Model.PreviewInstallCommand.Execute(null);
        fixture.Model.ConfirmHookCommand.Execute(null);
        Assert.AreEqual("Instalado", fixture.Model.HookStatus);
        File.AppendAllText(fixture.Settings, "\n");
        await fixture.Model.RefreshAsync(CancellationToken.None);
        StringAssert.Contains(fixture.Model.HookStatus, "configurações alterado");
        File.AppendAllText(Path.Combine(fixture.Stable, "PanelOS.ClaudeHook.dll"), "changed fixture");
        await fixture.Model.RefreshAsync(CancellationToken.None);
        StringAssert.Contains(fixture.Model.HookStatus, "hook alterado");
        File.AppendAllText(Path.Combine(Path.GetDirectoryName(fixture.Stable)!, "build", "PanelOS.ClaudeHook.exe"), "new fixture build");
        await fixture.Model.RefreshAsync(CancellationToken.None);
        StringAssert.Contains(fixture.Model.HookStatus, "desatualizado");
        StringAssert.Contains(fixture.Model.HookStatus, "remova e instale de novo");
    }

    [TestMethod]
    public void MissingHookFilesStopThePreviewWithoutWriting()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(Path.GetDirectoryName(fixture.Stable)!, "build", "PanelOS.ClaudeHook.dll"));
        fixture.Model.PreviewInstallCommand.Execute(null);
        Assert.IsFalse(fixture.Model.DialogOpen);
        StringAssert.Contains(fixture.Model.Outcome, "Faltam arquivos do hook");
        Assert.IsFalse(File.Exists(fixture.Settings));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "panelos-integrations-ui-" + Guid.NewGuid().ToString("N"));
        internal string Settings => Path.Combine(_root, "settings.json");
        internal Admin Admin { get; } = new();
        internal Clipboard Clipboard { get; } = new();
        internal Confirmation Confirmation { get; } = new();
        internal IntegrationsViewModel Model { get; }
        internal string Stable => Path.Combine(_root, "stable-hook");
        internal Fixture()
        {
            // Never the real %LOCALAPPDATA%: the stable folder is also inside the temporary root.
            var build = Path.Combine(_root, "build");
            Directory.CreateDirectory(build);
            foreach (var name in new[] { "PanelOS.ClaudeHook.exe", "PanelOS.ClaudeHook.dll", "PanelOS.ClaudeHook.deps.json", "PanelOS.ClaudeHook.runtimeconfig.json" })
                File.WriteAllText(Path.Combine(build, name), "fake " + name);
            Model = new(Admin, new ClaudeHookInstaller(Settings, Path.Combine(build, "PanelOS.ClaudeHook.exe"), Stable), Clipboard, Confirmation);
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
