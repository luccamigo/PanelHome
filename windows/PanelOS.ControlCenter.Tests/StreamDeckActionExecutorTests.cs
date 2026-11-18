using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckActionExecutorTests
{
    private const string ExecutablePath = @"C:\Apps\Editor\editor.exe";

    private FakeKeyboardInputService _keyboard = new();
    private RecordingProcessLauncher _launcher = new();
    private FakeFileSystemProbe _fileSystem = new();
    private RecordingStreamDeckLog _log = new();
    private InMemoryStreamDeckConfigurationStore _store = new();

    [TestInitialize]
    public void CreateExecutor()
    {
        _keyboard = new FakeKeyboardInputService();
        _launcher = new RecordingProcessLauncher();
        _fileSystem = new FakeFileSystemProbe();
        _log = new RecordingStreamDeckLog();
        _store = new InMemoryStreamDeckConfigurationStore();
        _fileSystem.Files.Add(ExecutablePath);
        _fileSystem.Directories.Add(@"C:\Apps\Editor");
    }

    [TestMethod]
    public async Task UnknownButtonIdIsReported()
    {
        var result = await Executor().ExecuteAsync("btn-inexistente", default);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(StreamDeckErrorCodes.ButtonNotFound, result.ErrorCode);
        Assert.AreEqual(0, _launcher.Started.Count);
        Assert.AreEqual(0, _keyboard.Events.Count);
    }

    [TestMethod]
    public async Task ConfiguredButtonIsResolvedById()
    {
        var result = await Executor().ExecuteAsync("btn-play-pause", default);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(1, _keyboard.Batches);
    }

    [TestMethod]
    public async Task DisabledButtonRefusesToRun()
    {
        var button = Button(new MediaCommandAction());
        button.Enabled = false;

        var result = await Executor().ExecuteButtonAsync(button, default);

        Assert.AreEqual(StreamDeckErrorCodes.ButtonDisabled, result.ErrorCode);
        Assert.AreEqual(0, _keyboard.Events.Count);
    }

    [TestMethod]
    public async Task ButtonWithoutAnActionIsReported()
    {
        var result = await Executor().ExecuteButtonAsync(Button(null), default);

        Assert.AreEqual(StreamDeckErrorCodes.ActionNotConfigured, result.ErrorCode);
    }

    [TestMethod]
    public async Task PanelModeIsNotSimulated()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new PanelModeAction { Mode = PanelMode.Ambient }),
            default);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(StreamDeckErrorCodes.UnsupportedAction, result.ErrorCode);
        StringAssert.Contains(result.Message, "PanelHost");
    }

    [TestMethod]
    public async Task AbsoluteVolumeIsNotSupportedYet()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new VolumeCommandAction
            {
                Command = VolumeCommand.SetLevel,
                Level = 40,
            }),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.UnsupportedAction, result.ErrorCode);
        Assert.AreEqual(0, _keyboard.Events.Count);
    }

    [TestMethod]
    public async Task MissingExecutableIsReported()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new LaunchApplicationAction
            {
                ExecutablePath = @"C:\Apps\Removido\ghost.exe",
            }),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.ApplicationNotFound, result.ErrorCode);
        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public async Task LaunchStartsTheConfiguredExecutableDirectly()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new LaunchApplicationAction
            {
                ExecutablePath = ExecutablePath,
                Arguments = "--nova-janela",
            }),
            default);

        Assert.IsTrue(result.Success, result.Message);
        var started = _launcher.Last!;
        Assert.AreEqual(ExecutablePath, started.FileName);
        Assert.AreEqual("--nova-janela", started.Arguments);
        Assert.IsFalse(
            started.UseShellExecute,
            "um executável é iniciado sem passar pelo shell");
        Assert.AreEqual(@"C:\Apps\Editor", started.WorkingDirectory);
    }

    [TestMethod]
    public async Task ConfiguredWorkingDirectoryMustExist()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new LaunchApplicationAction
            {
                ExecutablePath = ExecutablePath,
                WorkingDirectory = @"C:\Apps\Inexistente",
            }),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.ExecutionFailed, result.ErrorCode);
        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public async Task LaunchFailureIsReportedInsteadOfThrown()
    {
        _launcher.Failure = new InvalidOperationException("acesso negado");

        var result = await Executor().ExecuteButtonAsync(
            Button(new LaunchApplicationAction { ExecutablePath = ExecutablePath }),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.ExecutionFailed, result.ErrorCode);
        StringAssert.Contains(result.Message, "acesso negado");
    }

    [TestMethod]
    public async Task HttpAndHttpsUrlsAreOpenedThroughTheShell()
    {
        foreach (var url in new[]
                 {
                     "https://panelos.invalid/painel",
                     "http://panelos.invalid",
                     "mailto:alguem@panelos.invalid",
                 })
        {
            var result = await Executor().ExecuteButtonAsync(
                Button(new OpenUrlAction { Url = url }),
                default);

            Assert.IsTrue(result.Success, $"{url}: {result.Message}");
            Assert.IsTrue(
                _launcher.Last!.UseShellExecute,
                "abrir uma URL usa o aplicativo padrão do usuário");
        }
    }

    [TestMethod]
    public async Task DangerousUrlSchemesAreRefused()
    {
        foreach (var url in new[]
                 {
                     @"file:///C:/Windows/System32/cmd.exe",
                     "javascript:alert(1)",
                     "vbscript:msgbox(1)",
                     "ms-settings:privacy",
                     "não é uma url",
                 })
        {
            var result = await Executor().ExecuteButtonAsync(
                Button(new OpenUrlAction { Url = url }),
                default);

            Assert.AreEqual(
                StreamDeckErrorCodes.InvalidUrl,
                result.ErrorCode,
                $"{url} deveria ser recusada");
        }

        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public async Task ShortcutIsSentAsAStructuredKeySequence()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Control | PanelKeyModifiers.Shift,
                Key = PanelKey.S,
            }),
            default);

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(
            "Control down, Shift down, S down, S up, Shift up, Control up",
            _keyboard.Describe());
    }

    [TestMethod]
    public async Task ShortcutWithoutAKeyIsUnconfigured()
    {
        var result = await Executor().ExecuteButtonAsync(
            Button(new KeyboardShortcutAction
            {
                Modifiers = PanelKeyModifiers.Windows,
            }),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.ActionNotConfigured, result.ErrorCode);
        Assert.AreEqual(0, _keyboard.Events.Count);
    }

    [TestMethod]
    public async Task MediaCommandsSendTheirMediaKey()
    {
        var executor = Executor();

        await executor.ExecuteButtonAsync(
            Button(new MediaCommandAction { Command = MediaCommand.Next }),
            default);

        Assert.AreEqual(
            "MediaNextTrack down, MediaNextTrack up",
            _keyboard.Describe());
    }

    [TestMethod]
    public async Task VolumeCommandsSendTheirVolumeKey()
    {
        var executor = Executor();

        await executor.ExecuteButtonAsync(
            Button(new VolumeCommandAction { Command = VolumeCommand.MuteToggle }),
            default);

        Assert.AreEqual("VolumeMute down, VolumeMute up", _keyboard.Describe());
    }

    [TestMethod]
    public async Task KeyboardFailureIsReportedInsteadOfThrown()
    {
        _keyboard.Failure = new InvalidOperationException("SendInput falhou");

        var result = await Executor().ExecuteButtonAsync(
            Button(new MediaCommandAction()),
            default);

        Assert.AreEqual(StreamDeckErrorCodes.ExecutionFailed, result.ErrorCode);
    }

    [TestMethod]
    public async Task LogsCarryTheOutcomeButNoConfiguration()
    {
        await Executor().ExecuteButtonAsync(
            Button(new LaunchApplicationAction
            {
                ExecutablePath = ExecutablePath,
                Arguments = "--token segredo",
            }),
            default);

        StringAssert.Contains(_log.Text, "[StreamDeck] Action=LaunchApplication");
        StringAssert.Contains(_log.Text, "[StreamDeck] Success");
        Assert.IsFalse(_log.Text.Contains("segredo", StringComparison.Ordinal));
        Assert.IsFalse(_log.Text.Contains(ExecutablePath, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task FailureLogsCarryTheErrorCode()
    {
        await Executor().ExecuteAsync("btn-inexistente", default);

        StringAssert.Contains(
            _log.Text,
            $"[StreamDeck] Failed code={StreamDeckErrorCodes.ButtonNotFound}");
    }

    private WindowsStreamDeckActionExecutor Executor() =>
        new(_store, _keyboard, _launcher, _fileSystem, _log);

    private static StreamDeckButton Button(PanelAction? action) => new()
    {
        Id = "btn-test",
        Label = "Teste",
        Action = action,
    };
}
