using System.Text.Json.Serialization;

namespace PanelHost.Protocol;

public sealed record LoopMediaStateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.LoopMediaState;
    [JsonPropertyName("available")]
    public bool Available { get; init; }
    [JsonPropertyName("mediaId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaId { get; init; }
    [JsonPropertyName("kind"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; init; }
    [JsonPropertyName("mime"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mime { get; init; }
    [JsonPropertyName("sizeBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }
}

public sealed record LoopMediaChunkMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.LoopMediaChunk;
    [JsonPropertyName("mediaId")]
    public string MediaId { get; init; } = string.Empty;
    [JsonPropertyName("offset")]
    public long Offset { get; init; }
    [JsonPropertyName("totalBytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? TotalBytes { get; init; }
    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }
    [JsonPropertyName("last"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Last { get; init; }
    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}
