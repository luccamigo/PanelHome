using System.Diagnostics;

namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Runs a configured button on this PC.
/// </summary>
/// <remarks>
/// <para>
/// Every path into this class starts from a button that already exists in the
/// local configuration. Nothing here accepts a command line, a script or a
/// shell string, so the future <c>streamdeck.invoke(buttonId)</c> from the
/// tablet cannot express anything the user has not configured on the PC.
/// </para>
/// <para>
/// Two rules keep LaunchApplication from becoming a shell: only <c>.exe</c>
/// targets are accepted - a .bat, .cmd, .ps1 or .vbs would be script execution
/// wearing a different extension - and a known interpreter may be launched
/// bare but never with arguments.
/// </para>
/// </remarks>
public sealed class WindowsStreamDeckActionExecutor : IStreamDeckActionExecutor
{
    private static readonly string[] ShellInterpreters =
    [
        "cmd",
        "powershell",
        "pwsh",
        "wscript",
        "cscript",
        "mshta",
        "rundll32",
        "regsvr32",
    ];

    private static readonly string[] AllowedUrlSchemes =
    [
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeMailto,
    ];

    private readonly IStreamDeckConfigurationStore _store;
    private readonly IKeyboardInputService _keyboard;
    private readonly IProcessLauncher _processLauncher;
    private readonly IFileSystemProbe _fileSystem;
    private readonly IStreamDeckLog _log;

    public WindowsStreamDeckActionExecutor(
        IStreamDeckConfigurationStore store,
        IKeyboardInputService keyboard,
        IProcessLauncher processLauncher,
        IFileSystemProbe? fileSystem = null,
        IStreamDeckLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(processLauncher);
        _store = store;
        _keyboard = keyboard;
        _processLauncher = processLauncher;
        _fileSystem = fileSystem ?? new WindowsFileSystemProbe();
        _log = log ?? NullStreamDeckLog.Instance;
    }

    public async Task<StreamDeckActionResult> ExecuteAsync(
        string buttonId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(buttonId);
        _log.Write($"[StreamDeck] Invoke button={buttonId}");

        StreamDeckConfigurationLoadResult loaded;
        try
        {
            loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                StreamDeckErrorCodes.ConfigurationUnavailable,
                $"Não foi possível ler a configuração: {exception.Message}");
        }

        if (loaded.Source == StreamDeckConfigurationSource.Unreadable)
        {
            return Failed(
                StreamDeckErrorCodes.ConfigurationUnavailable,
                loaded.Detail ?? "A configuração do Stream Deck não pôde ser lida.");
        }

        var button = loaded.Configuration.FindButton(buttonId);
        if (button is null)
        {
            return Failed(
                StreamDeckErrorCodes.ButtonNotFound,
                "O botão não existe na configuração deste PC.");
        }

        return await ExecuteButtonAsync(button, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<StreamDeckActionResult> ExecuteButtonAsync(
        StreamDeckButton button,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(button);
        cancellationToken.ThrowIfCancellationRequested();

        // Starting a process and synthesizing input both block briefly; keep
        // them off the caller's thread so the UI stays responsive.
        return Task.Run(() => Execute(button), cancellationToken);
    }

    private StreamDeckActionResult Execute(StreamDeckButton button)
    {
        if (!button.Enabled)
        {
            return Failed(
                StreamDeckErrorCodes.ButtonDisabled,
                "O botão está desativado.");
        }

        if (button.Action is not { } action)
        {
            return Failed(
                StreamDeckErrorCodes.ActionNotConfigured,
                "Ação ainda não configurada.");
        }

        _log.Write($"[StreamDeck] Action={action.Kind}");
        try
        {
            var result = action switch
            {
                LaunchApplicationAction launch => LaunchApplication(launch),
                OpenUrlAction openUrl => OpenUrl(openUrl),
                KeyboardShortcutAction shortcut => SendShortcut(shortcut),
                MediaCommandAction media => SendMediaCommand(media),
                VolumeCommandAction volume => SendVolumeCommand(volume),
                PanelModeAction => Failed(
                    StreamDeckErrorCodes.UnsupportedAction,
                    "Mudar o modo do painel exige a integração com o PanelHost, " +
                    "que ainda não existe."),
                UnknownPanelAction unknown => Failed(
                    StreamDeckErrorCodes.UnsupportedAction,
                    $"Ação \"{unknown.RawKind}\" foi criada por uma versão mais " +
                    "nova e não pode ser executada por esta."),
                _ => Failed(
                    StreamDeckErrorCodes.UnsupportedAction,
                    "Tipo de ação não suportado."),
            };

            if (result.Success)
            {
                // Failures are already logged where they are produced.
                _log.Write("[StreamDeck] Success");
            }

            return result;
        }
        catch (Exception exception)
        {
            _log.Write(
                $"[StreamDeck] Failed code={StreamDeckErrorCodes.ExecutionFailed} " +
                $"({exception.GetType().Name})");
            return StreamDeckActionResult.Error(
                StreamDeckErrorCodes.ExecutionFailed,
                $"A ação falhou: {exception.Message}");
        }
    }

    private StreamDeckActionResult LaunchApplication(LaunchApplicationAction action)
    {
        var executablePath = action.ExecutablePath?.Trim();
        if (string.IsNullOrEmpty(executablePath))
        {
            return Failed(
                StreamDeckErrorCodes.ActionNotConfigured,
                "Nenhum executável foi configurado.");
        }

        if (!IsExecutableFile(executablePath))
        {
            return Failed(
                StreamDeckErrorCodes.ShellNotAllowed,
                "Somente arquivos .exe podem ser iniciados. Scripts e atalhos " +
                "não são executados pelo PanelOS.");
        }

        var arguments = action.Arguments?.Trim();
        if (!string.IsNullOrEmpty(arguments) && IsShellInterpreter(executablePath))
        {
            return Failed(
                StreamDeckErrorCodes.ShellNotAllowed,
                "Um interpretador de comandos não pode receber argumentos: isso " +
                "seria execução de comando arbitrário.");
        }

        if (Path.IsPathRooted(executablePath) &&
            !_fileSystem.FileExists(executablePath))
        {
            return Failed(
                StreamDeckErrorCodes.ApplicationNotFound,
                "Aplicativo não encontrado no caminho configurado.");
        }

        var workingDirectory = action.WorkingDirectory?.Trim();
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            if (!_fileSystem.DirectoryExists(workingDirectory))
            {
                return Failed(
                    StreamDeckErrorCodes.ExecutionFailed,
                    "O diretório de trabalho configurado não existe.");
            }
        }
        else if (Path.IsPathRooted(executablePath))
        {
            // Default to the application's own folder instead of inheriting the
            // Control Center's current directory.
            workingDirectory = Path.GetDirectoryName(executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,

            // Never a shell: CreateProcess starts the executable directly, so
            // no interpreter parses the arguments.
            UseShellExecute = false,
        };
        if (!string.IsNullOrEmpty(arguments))
        {
            // The configured string is handed over as-is. Nothing is built by
            // concatenating the file name with user text.
            startInfo.Arguments = arguments;
        }

        if (!string.IsNullOrEmpty(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        _processLauncher.Start(startInfo);
        return StreamDeckActionResult.Ok("Aplicativo iniciado.");
    }

    private StreamDeckActionResult OpenUrl(OpenUrlAction action)
    {
        var url = action.Url?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return Failed(
                StreamDeckErrorCodes.ActionNotConfigured,
                "Nenhuma URL foi configurada.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !AllowedUrlSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return Failed(
                StreamDeckErrorCodes.InvalidUrl,
                "URL inválida. Apenas http, https e mailto são aceitos.");
        }

        // The shell picks the user's default browser or mail client; PanelOS
        // never chooses or launches one itself.
        _processLauncher.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
        return StreamDeckActionResult.Ok("URL aberta.");
    }

    private StreamDeckActionResult SendShortcut(KeyboardShortcutAction action)
    {
        if (!KeyboardChordBuilder.TryMap(action.Key, out var virtualKey))
        {
            return action.Key == PanelKey.None
                ? Failed(
                    StreamDeckErrorCodes.ActionNotConfigured,
                    "Nenhuma tecla foi configurada.")
                : Failed(
                    StreamDeckErrorCodes.UnsupportedAction,
                    $"A tecla {action.Key} ainda não é suportada.");
        }

        _keyboard.Send(KeyboardChordBuilder.Build(action.Modifiers, virtualKey));
        return StreamDeckActionResult.Ok("Atalho enviado.");
    }

    private StreamDeckActionResult SendMediaCommand(MediaCommandAction action)
    {
        _keyboard.Send(KeyboardChordBuilder.BuildTap(
            KeyboardChordBuilder.ForMediaCommand(action.Command)));
        return StreamDeckActionResult.Ok("Comando de mídia enviado.");
    }

    private StreamDeckActionResult SendVolumeCommand(VolumeCommandAction action)
    {
        var key = KeyboardChordBuilder.ForVolumeCommand(action.Command);
        if (key == PanelVirtualKey.None)
        {
            return Failed(
                StreamDeckErrorCodes.UnsupportedAction,
                "Definir um volume absoluto ainda não é suportado.");
        }

        _keyboard.Send(KeyboardChordBuilder.BuildTap(key));
        return StreamDeckActionResult.Ok("Comando de volume enviado.");
    }

    private static bool IsExecutableFile(string path) =>
        string.Equals(
            Path.GetExtension(path),
            ".exe",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsShellInterpreter(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return ShellInterpreters.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private StreamDeckActionResult Failed(string errorCode, string message)
    {
        _log.Write($"[StreamDeck] Failed code={errorCode}");
        return StreamDeckActionResult.Error(errorCode, message);
    }
}
