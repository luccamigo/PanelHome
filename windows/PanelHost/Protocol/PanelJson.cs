using System.Text.Json;

namespace PanelHost.Protocol;

public static class PanelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
    };

    public static string Serialize<T>(T message) where T : class =>
        JsonSerializer.Serialize(message, Options);

    public static string SerializeLine<T>(T message) where T : class =>
        $"{Serialize(message)}\n";

    public static T Deserialize<T>(string json) where T : class
    {
        using var document = JsonDocument.Parse(json);
        RejectNonFiniteNumbers(document.RootElement);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new JsonException("The JSON message was null.");
    }

    private static void RejectNonFiniteNumbers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                if (element.TryGetDouble(out var value) && !double.IsFinite(value))
                {
                    throw new JsonException("Protocol numbers must be finite.");
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    RejectNonFiniteNumbers(child);
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    RejectNonFiniteNumbers(property.Value);
                }
                break;
        }
    }
}
