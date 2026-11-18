using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace PanelOS.Windows.StreamDeck.Serialization;

/// <summary>
/// The persisted Stream Deck format. The document always starts with its
/// schema <c>version</c>; readers must check it before trusting anything else,
/// because a file that is valid today may have been written by a newer build.
/// </summary>
public static class StreamDeckJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions(
        indented: false);

    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(
        indented: true);

    public static string Serialize(StreamDeckConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, IndentedOptions);
    }

    /// <summary>
    /// Parses a document. Throws <see cref="JsonException"/> for malformed JSON
    /// and <see cref="StreamDeckSchemaException"/> for a version this build
    /// cannot read; callers decide how to recover.
    /// </summary>
    public static StreamDeckConfiguration Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        ValidateDocumentBudget(json);
        var version = ReadVersion(json);
        if (version > StreamDeckConfiguration.CurrentSchemaVersion)
        {
            throw new StreamDeckSchemaException(
                version,
                $"A configuração do Stream Deck usa o schema v{version}, mais " +
                $"recente que o suportado (v{StreamDeckConfiguration.CurrentSchemaVersion}).");
        }

        if (version < 1)
        {
            throw new StreamDeckSchemaException(
                version,
                "A configuração do Stream Deck não declara um schema válido.");
        }

        // Older documents are rewritten to the current shape before binding, so
        // the model never has to know about a retired field.
        if (version < StreamDeckConfiguration.CurrentSchemaVersion)
        {
            json = StreamDeckSchemaMigrations.Upgrade(json, version);
            ValidateDocumentBudget(json);
        }

        var configuration = JsonSerializer.Deserialize<StreamDeckConfiguration>(
                json,
                Options)
            ?? throw new JsonException("A configuração do Stream Deck está vazia.");
        configuration.Version = StreamDeckConfiguration.CurrentSchemaVersion;
        return configuration;
    }

    private static void ValidateDocumentBudget(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > StreamDeckValidator.MaximumConfigurationBytes)
        {
            throw new JsonException("A configuraÃ§Ã£o do Stream Deck excede o limite de bytes.");
        }

        using var document = JsonDocument.Parse(
            json,
            new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A configuraÃ§Ã£o deve ser um objeto JSON.");
        }
        if (!root.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        if (profiles.GetArrayLength() > StreamDeckValidator.MaximumProfiles)
        {
            throw new JsonException("A configuraÃ§Ã£o tem profiles demais.");
        }

        var totalButtons = 0;
        var iconIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles.EnumerateArray())
        {
            ValidateString(profile, "id", StreamDeckValidator.MaximumIdentifierLength);
            ValidateString(profile, "name", StreamDeckValidator.MaximumNameLength);
            if (!profile.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            if (pages.GetArrayLength() > StreamDeckValidator.MaximumPagesPerProfile)
            {
                throw new JsonException("Um profile tem pÃ¡ginas demais.");
            }
            foreach (var page in pages.EnumerateArray())
            {
                ValidateString(page, "id", StreamDeckValidator.MaximumIdentifierLength);
                ValidateString(page, "name", StreamDeckValidator.MaximumNameLength);
                if (!page.TryGetProperty("buttons", out var buttons) ||
                    buttons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                if (buttons.GetArrayLength() >
                    StreamDeckValidator.MaximumRows * StreamDeckValidator.MaximumColumns)
                {
                    throw new JsonException("Uma pÃ¡gina tem botÃµes demais.");
                }
                totalButtons += buttons.GetArrayLength();
                if (totalButtons > StreamDeckValidator.MaximumButtons)
                {
                    throw new JsonException("A configuraÃ§Ã£o tem botÃµes demais.");
                }
                foreach (var button in buttons.EnumerateArray())
                {
                    ValidateString(button, "id", StreamDeckValidator.MaximumIdentifierLength);
                    ValidateString(button, "label", StreamDeckValidator.MaximumLabelLength);
                    if (button.TryGetProperty("icon", out var icon) &&
                        icon.ValueKind == JsonValueKind.Object &&
                        icon.TryGetProperty("iconId", out var iconId) &&
                        iconId.ValueKind == JsonValueKind.String)
                    {
                        iconIds.Add(iconId.GetString()!);
                        if (iconIds.Count > StreamDeckValidator.MaximumIconCount)
                        {
                            throw new JsonException("A configuraÃ§Ã£o tem Ã­cones demais.");
                        }
                    }
                }
            }
        }
    }

    private static void ValidateString(JsonElement owner, string name, int maximumLength)
    {
        if (owner.ValueKind == JsonValueKind.Object &&
            owner.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Length > maximumLength)
        {
            throw new JsonException($"{name} excede {maximumLength} caracteres.");
        }
    }

    /// <summary>
    /// Reads only the schema version, without binding the rest of the document.
    /// </summary>
    public static int ReadVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "A configuração do Stream Deck deve ser um objeto JSON.");
        }

        if (!document.RootElement.TryGetProperty("version", out var element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetInt32(out var version))
        {
            throw new JsonException(
                "A configuração do Stream Deck não declara \"version\".");
        }

        return version;
    }

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = indented,
        MaxDepth = 32,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new PanelActionJsonConverter(),
        },
    };
}

/// <summary>
/// The document parsed, but its schema version is not readable by this build.
/// </summary>
public sealed class StreamDeckSchemaException : Exception
{
    public StreamDeckSchemaException(int version, string message)
        : base(message)
    {
        Version = version;
    }

    public int Version { get; }
}
