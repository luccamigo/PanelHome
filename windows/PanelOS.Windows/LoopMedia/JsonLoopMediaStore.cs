using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelOS.Windows.LoopMedia;

public sealed class JsonLoopMediaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly Action<string>? _log;

    public JsonLoopMediaStore(string? location = null, string? mediaDirectory = null,
        Action<string>? log = null)
    {
        Location = location ?? PanelOSPaths.LoopMediaConfigurationFile;
        MediaDirectory = mediaDirectory ?? PanelOSPaths.LoopMediaDirectory;
        _log = log;
    }

    public string Location { get; }
    public string MediaDirectory { get; }

    public string GetMediaPath(LoopMediaConfiguration configuration)
    {
        if (!LoopMediaValidator.IsValid(configuration) || configuration.MediaId is null)
            throw new InvalidDataException("Invalid media configuration.");
        return Path.Combine(MediaDirectory, configuration.MediaId +
            (configuration.Kind == "gif" ? ".gif" : ".mp4"));
    }

    public async Task<LoopMediaConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(Location)) return new();
            await using var file = new FileStream(Location, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length > 4096) throw new InvalidDataException();
            var configuration = await JsonSerializer.DeserializeAsync<LoopMediaConfiguration>(
                file, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (configuration is null || !LoopMediaValidator.IsValid(configuration))
                throw new InvalidDataException();
            return configuration;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _log?.Invoke("[LoopMedia] configuration unavailable or invalid");
            return new();
        }
    }

    public async Task SaveAsync(LoopMediaConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!LoopMediaValidator.IsValid(configuration)) throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Location))!);
        var temporary = Location + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(configuration, JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, Location, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public async Task<LoopMediaConfiguration> ImportAsync(string source,
        CancellationToken cancellationToken)
    {
        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        // Deny writers while validating, hashing and copying the same handle.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, useAsync: true);
        if (input.Length > LoopMediaValidator.MaximumBytes)
            throw new InvalidDataException(LoopMediaValidator.TooLarge);
        var header = new byte[(int)Math.Min(8, input.Length)];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var format = LoopMediaValidator.Detect(header);
        input.Position = 0;
        var digest = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        var configuration = new LoopMediaConfiguration
        {
            MediaId = Convert.ToHexString(digest).ToLowerInvariant(),
            Kind = format.Kind,
            Mime = format.Mime,
            SizeBytes = input.Length,
            OriginalFileName = Path.GetFileName(source),
        };
        Directory.CreateDirectory(MediaDirectory);
        var destination = GetMediaPath(configuration);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var existed = File.Exists(destination);
        var copied = false;
        try
        {
            input.Position = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: true);
            copied = true;
            await SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (copied && !existed) TryDelete(destination);
            throw;
        }
        finally
        {
            TryDelete(temporary);
        }
        if (previous.MediaId is not null && GetMediaPath(previous) != destination)
            TryDelete(GetMediaPath(previous));
        return configuration;
    }

    public async Task RemoveAsync(CancellationToken cancellationToken)
    {
        var previous = await LoadAsync(cancellationToken).ConfigureAwait(false);
        await SaveAsync(new(), cancellationToken).ConfigureAwait(false);
        if (previous.MediaId is not null) TryDelete(GetMediaPath(previous));
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke("[LoopMedia] cleanup unavailable");
        }
    }
}
