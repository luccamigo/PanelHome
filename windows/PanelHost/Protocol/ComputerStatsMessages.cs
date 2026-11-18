using System.Text.Json.Serialization;

namespace PanelHost.Protocol;

public sealed record NetworkAdapterRate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("downloadBytesPerSecond")] double? DownloadBytesPerSecond,
    [property: JsonPropertyName("uploadBytesPerSecond")] double? UploadBytesPerSecond);

public sealed record NetworkRatesMessage(
    [property: JsonPropertyName("sampledAtUnixMs")] long SampledAtUnixMs,
    [property: JsonPropertyName("downloadBytesPerSecond")] double? DownloadBytesPerSecond,
    [property: JsonPropertyName("uploadBytesPerSecond")] double? UploadBytesPerSecond,
    [property: JsonPropertyName("adapters")] IReadOnlyList<NetworkAdapterRate> Adapters)
{
    [JsonPropertyName("type")] public string Type => "network_rates";
}

public sealed record ProcessListItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("pid")] int? Pid,
    [property: JsonPropertyName("startTimeUtcTicks")] long? StartTimeUtcTicks,
    [property: JsonPropertyName("cpuPercent")] double? CpuPercent,
    [property: JsonPropertyName("memoryBytes")] long? MemoryBytes,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("protected")] bool Protected,
    [property: JsonPropertyName("count")] int Count = 1);

public sealed record ProcessesMessage(
    [property: JsonPropertyName("sampledAtUnixMs")] long SampledAtUnixMs,
    [property: JsonPropertyName("sort")] string Sort,
    [property: JsonPropertyName("grouped")] bool Grouped,
    [property: JsonPropertyName("items")] IReadOnlyList<ProcessListItem> Items)
{
    [JsonPropertyName("type")] public string Type => "processes";
    [JsonPropertyName("terminateEnabled")] public bool TerminateEnabled { get; init; }
}
