using System.Text.Json;
using System.Text.Json.Nodes;

namespace PanelOS.Windows.StreamDeck.Serialization;

/// <summary>
/// Rewrites an older document into the current schema.
/// </summary>
/// <remarks>
/// Migrations run on the JSON tree rather than on the model: the model only
/// knows the current shape, and a retired field like v1's icon
/// <c>filePath</c> would otherwise have to survive in it forever.
/// </remarks>
internal static class StreamDeckSchemaMigrations
{
    internal static string Upgrade(string json, int fromVersion)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException(
                "A configuração do Stream Deck deve ser um objeto JSON.");

        var version = fromVersion;
        if (version == 1)
        {
            MigrateV1ToV2(root);
            version = 2;
        }

        root["version"] = version;
        return root.ToJsonString();
    }

    /// <summary>
    /// v1 stored a custom icon as its path on disk. v2 stores a cache id and
    /// keeps the origin separately, so the path never reaches the tablet DTO.
    /// The id is filled in later by the icon cache, when the image is imported.
    /// </summary>
    private static void MigrateV1ToV2(JsonObject root)
    {
        foreach (var button in EnumerateButtons(root))
        {
            if (button["icon"] is not JsonObject icon)
            {
                continue;
            }

            if (!string.Equals(
                    icon["kind"]?.GetValue<string>(),
                    "localFile",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var filePath = icon["filePath"]?.GetValue<string>();
            icon.Remove("filePath");
            icon["kind"] = "custom";
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                icon["sourcePath"] = filePath;
            }
        }
    }

    private static IEnumerable<JsonObject> EnumerateButtons(JsonObject root)
    {
        foreach (var profile in Children(root, "profiles"))
        {
            foreach (var page in Children(profile, "pages"))
            {
                foreach (var button in Children(page, "buttons"))
                {
                    yield return button;
                }
            }
        }
    }

    private static IEnumerable<JsonObject> Children(JsonObject parent, string name)
    {
        if (parent[name] is not JsonArray array)
        {
            yield break;
        }

        foreach (var item in array)
        {
            if (item is JsonObject child)
            {
                yield return child;
            }
        }
    }
}
