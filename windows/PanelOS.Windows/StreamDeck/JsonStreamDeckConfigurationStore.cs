using System.Text;
using System.Text.Json;
using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Stores the configuration as JSON under %LOCALAPPDATA%\PanelOS. Nothing is
/// written inside the repository, and a file this build cannot read is moved
/// aside rather than overwritten.
/// </summary>
public sealed class JsonStreamDeckConfigurationStore : IStreamDeckConfigurationStore
{
    private readonly string _filePath;
    private readonly bool _quarantineInvalidFile;

    public JsonStreamDeckConfigurationStore()
        : this(PanelOSPaths.StreamDeckConfigurationFile)
    {
    }

    /// <param name="quarantineInvalidFile">
    /// Whether a document this build cannot read is moved aside. The Control
    /// Center does that so the user gets a working editor back; PanelHost must
    /// not, because it only reads - a background reload is no reason to rename
    /// the file the user is editing on the other side of the machine.
    /// </param>
    public JsonStreamDeckConfigurationStore(
        string filePath,
        bool quarantineInvalidFile = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _quarantineInvalidFile = quarantineInvalidFile;
    }

    public string Location => _filePath;

    public async Task<StreamDeckConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken)
    {
        string json;
        try
        {
            if (!File.Exists(_filePath))
            {
                return new StreamDeckConfigurationLoadResult(
                    StreamDeckDefaults.CreateConfiguration(),
                    StreamDeckConfigurationSource.Defaults);
            }

            if (new FileInfo(_filePath).Length >
                StreamDeckValidator.MaximumConfigurationBytes)
            {
                return Recover("A configuraÃ§Ã£o excede o limite de bytes.");
            }

            json = await File.ReadAllTextAsync(
                    _filePath,
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new StreamDeckConfigurationLoadResult(
                StreamDeckDefaults.CreateConfiguration(),
                StreamDeckConfigurationSource.Unreadable,
                $"Não foi possível ler {_filePath}: {exception.Message}");
        }

        StreamDeckConfiguration configuration;
        try
        {
            configuration = StreamDeckJson.Deserialize(json);
        }
        catch (StreamDeckSchemaException exception)
            when (exception.Version > StreamDeckConfiguration.CurrentSchemaVersion)
        {
            return new StreamDeckConfigurationLoadResult(
                StreamDeckDefaults.CreateConfiguration(),
                StreamDeckConfigurationSource.UnsupportedSchema,
                exception.Message);
        }
        catch (Exception exception) when (
            exception is JsonException or StreamDeckSchemaException)
        {
            return Recover(exception.Message);
        }

        var validation = StreamDeckValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            return Recover(string.Join(" ", validation.Errors));
        }

        return new StreamDeckConfigurationLoadResult(
            configuration,
            StreamDeckConfigurationSource.Loaded);
    }

    public async Task SaveAsync(
        StreamDeckConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var validation = StreamDeckValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new StreamDeckConfigurationException(
                "A configuração do Stream Deck é inválida e não foi salva: " +
                string.Join(" ", validation.Errors),
                validation.Errors);
        }

        var json = StreamDeckJson.Serialize(configuration);
        if (Encoding.UTF8.GetByteCount(json) >
            StreamDeckValidator.MaximumConfigurationBytes)
        {
            throw new StreamDeckConfigurationException(
                "A configuraÃ§Ã£o excede o limite de bytes.",
                ["A configuraÃ§Ã£o excede o limite de bytes."]);
        }
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write beside the target and swap, so an interrupted save can never
        // leave a half-written configuration behind.
        var temporaryPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(
                temporaryPath,
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private StreamDeckConfigurationLoadResult Recover(string detail)
    {
        var quarantinedPath = _quarantineInvalidFile ? TryQuarantine() : null;
        var suffix = quarantinedPath is null
            ? " O arquivo original foi mantido."
            : $" O arquivo original foi preservado em {quarantinedPath}.";
        return new StreamDeckConfigurationLoadResult(
            StreamDeckDefaults.CreateConfiguration(),
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            detail + suffix,
            quarantinedPath);
    }

    private string? TryQuarantine()
    {
        var directory = Path.GetDirectoryName(_filePath);
        var name = Path.GetFileNameWithoutExtension(_filePath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var quarantinedPath = Path.Combine(
            directory ?? string.Empty,
            $"{name}.invalid-{stamp}.json");
        try
        {
            File.Move(_filePath, quarantinedPath, overwrite: true);
            return quarantinedPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
