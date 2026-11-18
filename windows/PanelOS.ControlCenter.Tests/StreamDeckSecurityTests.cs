using System.IO;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Runtime;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// The boundary the Stream Deck runtime has to hold: a button runs what the
/// user configured on this PC, and nothing in the design can be turned into
/// "run this text for me".
/// </summary>
[TestClass]
public sealed class StreamDeckSecurityTests
{
    private static readonly string[] Interpreters =
    [
        "cmd",
        "cmd.exe",
        "powershell",
        "powershell.exe",
        "pwsh.exe",
        "wscript.exe",
        "cscript.exe",
        "mshta.exe",
    ];

    private FakeKeyboardInputService _keyboard = new();
    private RecordingProcessLauncher _launcher = new();
    private FakeFileSystemProbe _fileSystem = new();

    [TestInitialize]
    public void Reset()
    {
        _keyboard = new FakeKeyboardInputService();
        _launcher = new RecordingProcessLauncher();
        _fileSystem = new FakeFileSystemProbe();
    }

    [TestMethod]
    public async Task NoActionEverStartsAnInterpreter()
    {
        var executor = Executor();
        foreach (var button in EveryActionKind())
        {
            await executor.ExecuteButtonAsync(button, default);
        }

        foreach (var started in _launcher.Started)
        {
            var name = Path.GetFileName(started.FileName);
            Assert.IsFalse(
                Interpreters.Contains(name, StringComparer.OrdinalIgnoreCase),
                $"{name} não pode ser iniciado por uma ação do Stream Deck.");
            Assert.IsFalse(
                started.Arguments.Contains("/c", StringComparison.OrdinalIgnoreCase),
                "nenhuma ação monta uma linha de comando de shell.");
        }
    }

    [TestMethod]
    public async Task AnInterpreterWithArgumentsIsRefused()
    {
        _fileSystem.Files.Add(@"C:\Windows\System32\cmd.exe");
        _fileSystem.Files.Add(@"C:\Windows\System32\powershell.exe");

        foreach (var path in new[]
                 {
                     @"C:\Windows\System32\cmd.exe",
                     @"C:\Windows\System32\powershell.exe",
                 })
        {
            var result = await Executor().ExecuteButtonAsync(
                Button(new LaunchApplicationAction
                {
                    ExecutablePath = path,
                    Arguments = "/c del /q C:\\dados",
                }),
                default);

            Assert.AreEqual(
                StreamDeckErrorCodes.ShellNotAllowed,
                result.ErrorCode,
                path);
        }

        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public async Task ScriptsAndShortcutsCannotBeLaunched()
    {
        foreach (var path in new[]
                 {
                     @"C:\Apps\deploy.bat",
                     @"C:\Apps\deploy.cmd",
                     @"C:\Apps\deploy.ps1",
                     @"C:\Apps\deploy.vbs",
                     @"C:\Apps\deploy.js",
                     @"C:\Apps\Editor.lnk",
                     @"C:\Apps\pacote.msi",
                 })
        {
            _fileSystem.Files.Add(path);
            var result = await Executor().ExecuteButtonAsync(
                Button(new LaunchApplicationAction { ExecutablePath = path }),
                default);

            Assert.AreEqual(
                StreamDeckErrorCodes.ShellNotAllowed,
                result.ErrorCode,
                path);
        }

        Assert.AreEqual(0, _launcher.Started.Count);
    }

    [TestMethod]
    public async Task AnActionFromANewerBuildIsNeverExecuted()
    {
        var payload = JsonDocument.Parse(
            """{"kind":"runScript","script":"Remove-Item C:\\ -Recurse"}""");
        var button = Button(new UnknownPanelAction("runScript", payload.RootElement));

        var result = await Executor().ExecuteButtonAsync(button, default);

        Assert.AreEqual(StreamDeckErrorCodes.UnsupportedAction, result.ErrorCode);
        Assert.AreEqual(0, _launcher.Started.Count);
        Assert.AreEqual(0, _keyboard.Events.Count);
    }

    [TestMethod]
    public void TheRemoteEntryPointTakesAnIdentifierAndNothingElse()
    {
        // The tablet must not be able to hand the PC something to run: the only
        // wire parameter is a button id the PC resolves in its own store.
        var parameters = typeof(IStreamDeckActionExecutor)
            .GetMethod(nameof(IStreamDeckActionExecutor.ExecuteAsync))!
            .GetParameters();

        Assert.AreEqual(2, parameters.Length);
        Assert.AreEqual("buttonId", parameters[0].Name);
        Assert.AreEqual(typeof(string), parameters[0].ParameterType);
        Assert.AreEqual(typeof(CancellationToken), parameters[1].ParameterType);
    }

    [TestMethod]
    public void AButtonIsResolvedFromTheConfigurationByItsId()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();

        var button = configuration.FindButton("btn-play-pause");

        Assert.IsNotNull(button);
        Assert.AreEqual("Play/Pause", button.Label);
        Assert.IsInstanceOfType<MediaCommandAction>(button.Action);
        Assert.IsNull(configuration.FindButton("btn-inexistente"));
    }

    private static IEnumerable<StreamDeckButton> EveryActionKind()
    {
        yield return Button(new LaunchApplicationAction
        {
            ExecutablePath = @"C:\Apps\Editor\editor.exe",
        });
        yield return Button(new OpenUrlAction
        {
            Url = "https://panelos.invalid",
        });
        yield return Button(new KeyboardShortcutAction
        {
            Modifiers = PanelKeyModifiers.Windows,
            Key = PanelKey.D,
        });
        yield return Button(new MediaCommandAction());
        yield return Button(new VolumeCommandAction());
        yield return Button(new PanelModeAction());
    }

    private WindowsStreamDeckActionExecutor Executor()
    {
        _fileSystem.Files.Add(@"C:\Apps\Editor\editor.exe");
        _fileSystem.Directories.Add(@"C:\Apps\Editor");
        return new WindowsStreamDeckActionExecutor(
            new InMemoryStreamDeckConfigurationStore(),
            _keyboard,
            _launcher,
            _fileSystem);
    }

    private static StreamDeckButton Button(PanelAction action) => new()
    {
        Id = "btn-security",
        Label = "Teste",
        Action = action,
    };
}
