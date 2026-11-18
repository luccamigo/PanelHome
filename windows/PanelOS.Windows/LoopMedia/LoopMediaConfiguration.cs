using System.Text.Json.Serialization;

namespace PanelOS.Windows.LoopMedia;

public sealed record LoopMediaConfiguration
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    public string? MediaId { get; init; }
    public string? Kind { get; init; }
    public string? Mime { get; init; }
    public long? SizeBytes { get; init; }
    public string? OriginalFileName { get; init; }
}

public static class LoopMediaValidator
{
    public const long MaximumBytes = 67108864;
    public const int ChunkBytes = 262144;
    public const string UnsupportedFormat = "Formato não suportado: use GIF ou MP4";
    public const string TooLarge = "Arquivo maior que 64 MB";
    public const string Unreadable = "Não foi possível ler o arquivo";

    public static bool IsValidMediaId(string? id) =>
        id is { Length: 64 } && id.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsValid(LoopMediaConfiguration configuration) =>
        configuration.SchemaVersion == 1 && (configuration.MediaId is null
            ? configuration.Kind is null && configuration.Mime is null &&
                configuration.SizeBytes is null && configuration.OriginalFileName is null
            : IsValidMediaId(configuration.MediaId) &&
                configuration.SizeBytes is >= 1 and <= MaximumBytes &&
                ((configuration.Kind == "gif" && configuration.Mime == "image/gif") ||
                 (configuration.Kind == "video" && configuration.Mime == "video/mp4")));

    public static (string Kind, string Mime) Detect(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
            return ("gif", "image/gif");
        if (header.Length >= 8 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
            return ("video", "video/mp4");
        throw new InvalidDataException(UnsupportedFormat);
    }
}
