using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PanelHost.Services;

// Security review, 2026-11-16. Shared by HTTP framing, validation and tests.
internal static class LocalIntegrationLimits
{
    internal const int BodyBytes = 8 * 1024;
    internal const int HeaderBytes = 8 * 1024;
    internal const int Title = 80, Text = 280, Name = 40, Value = 140, Unit = 16, Key = 40, Page = 40;
    internal const int Values = 12, Notices = 20, QueueSeconds = 300, MaxTtlSeconds = 86400;
    internal const int Connections = 4, ReadSeconds = 5;
}

internal sealed record LocalNoticePayload(string Title, string Text, string Level, string? Key, string? Page,
    int DurationSeconds, int TtlSeconds);
internal sealed record LocalValuePayload(string Name, string Value, string? Unit, string Level, int? TtlSeconds);

internal static class LocalPayloadParser
{
    internal static JsonDocument Parse(byte[] body, params string[] allowed)
    {
        if (body.Length > LocalIntegrationLimits.BodyBytes) throw new ArgumentException("body_too_large");
        var text = new UTF8Encoding(false, true).GetString(body);
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 3 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("invalid_json");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                    throw new ArgumentException("unknown_or_duplicate_field");
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    internal static LocalNoticePayload Notice(byte[] body)
    {
        using var json = Parse(body, "title", "text", "level", "key", "page", "durationSeconds", "ttlSeconds");
        var root = json.RootElement;
        var level = Level(root);
        return new(Text(root, "title", LocalIntegrationLimits.Title, required: true)!,
            Text(root, "text", LocalIntegrationLimits.Text, multiline: true) ?? "", level,
            Text(root, "key", LocalIntegrationLimits.Key), Text(root, "page", LocalIntegrationLimits.Page),
            Number(root, "durationSeconds", 3, 30) ?? (level is "warning" ? 8 : level is "error" ? 12 : 5),
            Number(root, "ttlSeconds", 1, LocalIntegrationLimits.QueueSeconds) ?? LocalIntegrationLimits.QueueSeconds);
    }

    internal static LocalValuePayload Value(byte[] body)
    {
        using var json = Parse(body, "name", "value", "unit", "level", "ttlSeconds");
        var root = json.RootElement;
        return new(Text(root, "name", LocalIntegrationLimits.Name, required: true)!,
            Text(root, "value", LocalIntegrationLimits.Value, required: true)!,
            Text(root, "unit", LocalIntegrationLimits.Unit), Level(root),
            Number(root, "ttlSeconds", 1, LocalIntegrationLimits.MaxTtlSeconds));
    }

    internal static string? Text(JsonElement root, string name, int max, bool required = false, bool multiline = false)
    {
        if (!root.TryGetProperty(name, out var value))
            return required ? throw new ArgumentException("missing_" + name) : null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("invalid_" + name);
        var cleaned = Clean(value.GetString()!, multiline);
        if (cleaned.EnumerateRunes().Count() > max) throw new ArgumentException(name + "_too_long");
        if (required && string.IsNullOrWhiteSpace(cleaned)) throw new ArgumentException("missing_" + name);
        if (multiline && cleaned.Count(c => c == '\n') > 4) throw new ArgumentException("too_many_lines");
        return cleaned;
    }

    internal static string Clean(string text, bool multiline = false)
    {
        var valid = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { valid.Append(text[i]); valid.Append(text[++i]); }
            }
            else if (!char.IsLowSurrogate(text[i])) valid.Append(text[i]);
        }
        var builder = new StringBuilder();
        foreach (var rune in valid.ToString().Normalize(NormalizationForm.FormC).EnumerateRunes())
        {
            if (multiline && rune.Value == '\n') builder.Append('\n');
            else if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or
                UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)) builder.Append(rune);
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Level(JsonElement root)
    {
        var level = Text(root, "level", 7) ?? "info";
        return level is "info" or "success" or "warning" or "error" ? level : throw new ArgumentException("invalid_level");
    }

    internal static int? Number(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max) throw new ArgumentException("invalid_" + name);
        return number;
    }
}
