using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanelOS.Windows;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// Control Center preferences. "Start with Windows" is deliberately absent:
/// that state lives in the registry, and duplicating it here would let the two
/// disagree.
/// </summary>
public sealed class ControlCenterSettings
{
    public const int CurrentSchemaVersion = 1;

    public int Version { get; set; } = CurrentSchemaVersion;

    /// <summary>Closing the window hides it to the tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>
    /// Start the agent when the Control Center opens, unless one is already
    /// running.
    /// </summary>
    public bool StartPanelHostWithControlCenter { get; set; }

    /// <summary>
    /// Stop the agent the Control Center started when it exits. On by default:
    /// what this window starts, it ends, and an agent nobody can see left running
    /// after the last window closed is the harder state to explain. An agent
    /// started outside the Control Center is still never touched, and turning this
    /// off keeps the panel alive after the window is gone.
    /// </summary>
    public bool StopPanelHostOnExit { get; set; } = true;

    /// <summary>Restart an agent that crashed, with a ceiling on attempts.</summary>
    public bool RestartPanelHostAfterCrash { get; set; }

    /// <summary>
    /// Shows the ADB transport tools. ADB is how the tablet reaches PanelHost
    /// during development only, so nothing about it is visible by default.
    /// </summary>
    public bool DevelopmentMode { get; set; }

    /// <summary>
    /// Carrier the agent is launched with: "usb" for the tablet's own USB
    /// accessory link, "adb" for the development transport. The product path
    /// is the default; ADB additionally needs a session token and an
    /// <c>adb reverse</c> pair, which the development flow sets up.
    /// </summary>
    public string PanelHostTransport { get; set; } = "usb";

    /// <summary>Mirror the agent's output to the rotating log files.</summary>
    public bool WritePanelHostLogsToDisk { get; set; } = true;

    /// <summary>Overrides where PanelHost.exe is looked for.</summary>
    public string? PanelHostExecutablePath { get; set; }

    /// <summary>Overrides where adb.exe is looked for.</summary>
    public string? AdbExecutablePath { get; set; }
}

public interface IControlCenterSettingsStore
{
    string Location { get; }

    Task<ControlCenterSettings> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(
        ControlCenterSettings settings,
        CancellationToken cancellationToken);
}

public sealed class JsonControlCenterSettingsStore : IControlCenterSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public JsonControlCenterSettingsStore()
        : this(PanelOSPaths.ControlCenterSettingsFile)
    {
    }

    public JsonControlCenterSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public string Location => _filePath;

    /// <summary>
    /// Falls back to defaults for a missing, unreadable or corrupt file: losing
    /// preferences is never a reason to fail opening the window.
    /// </summary>
    public async Task<ControlCenterSettings> LoadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new ControlCenterSettings();
            }

            var json = await File.ReadAllTextAsync(
                    _filePath,
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
            var settings = JsonSerializer.Deserialize<ControlCenterSettings>(
                json,
                Options);
            if (settings is null ||
                settings.Version > ControlCenterSettings.CurrentSchemaVersion)
            {
                return new ControlCenterSettings();
            }

            return settings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ControlCenterSettings();
        }
    }

    public async Task SaveAsync(
        ControlCenterSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Version = ControlCenterSettings.CurrentSchemaVersion;

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(settings, Options),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
