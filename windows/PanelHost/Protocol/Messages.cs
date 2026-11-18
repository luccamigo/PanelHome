using System.Text.Json.Serialization;

namespace PanelHost.Protocol;

public sealed record MessageEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public sealed record HelloMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Hello;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = PanelProtocol.PROTOCOL_VERSION;

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; init; } = string.Empty;

    [JsonPropertyName("features")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Features { get; init; }

    [JsonPropertyName("buildIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildIdentity { get; init; }

    [JsonPropertyName("buildNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BuildNumber { get; init; }

    public bool SupportsFeature(string feature) =>
        Features?.Contains(feature, StringComparer.Ordinal) == true;
}

public sealed record HelloAckMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.HelloAck;

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; } = PanelProtocol.PROTOCOL_VERSION;

    [JsonPropertyName("hostName")]
    public string HostName { get; init; } = string.Empty;

    [JsonPropertyName("features")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Features { get; init; }

    [JsonPropertyName("buildIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildIdentity { get; init; }

    [JsonPropertyName("buildNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BuildNumber { get; init; }

    public bool SupportsFeature(string feature) =>
        Features?.Contains(feature, StringComparer.Ordinal) == true;
}

public sealed record ErrorMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Error;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}

public sealed record CpuTelemetry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("temperatureC")]
    public double? TemperatureC { get; init; }

    [JsonPropertyName("usagePercent")]
    public double? UsagePercent { get; init; }

    [JsonPropertyName("clockGhz")]
    public double? ClockGhz { get; init; }

    [JsonPropertyName("powerW")]
    public double? PowerW { get; init; }
}

public sealed record GpuTelemetry
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("temperatureC")]
    public double? TemperatureC { get; init; }

    [JsonPropertyName("usagePercent")]
    public double? UsagePercent { get; init; }

    [JsonPropertyName("clockGhz")]
    public double? ClockGhz { get; init; }

    [JsonPropertyName("memoryUsagePercent")]
    public double? MemoryUsagePercent { get; init; }

    [JsonPropertyName("memoryUsedGb")]
    public double? MemoryUsedGb { get; init; }

    [JsonPropertyName("memoryTotalGb")]
    public double? MemoryTotalGb { get; init; }

    [JsonPropertyName("powerW")]
    public double? PowerW { get; init; }
}

public sealed record MemoryTelemetry
{
    [JsonPropertyName("usedGb")]
    public double? UsedGb { get; init; }

    [JsonPropertyName("totalGb")]
    public double? TotalGb { get; init; }

    [JsonPropertyName("usagePercent")]
    public double? UsagePercent { get; init; }
}

public sealed record StorageTelemetry
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("temperatureC")]
    public double? TemperatureC { get; init; }

    [JsonPropertyName("usagePercent")]
    public double? UsagePercent { get; init; }
}

public sealed record FpsTelemetry
{
    [JsonPropertyName("state")]
    public string State { get; init; } = "unavailable";
    [JsonPropertyName("presentsPerSecond")]
    public double? PresentsPerSecond { get; init; }
    [JsonPropertyName("appName")]
    public string? AppName { get; init; }
}

public sealed record TelemetryMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Telemetry;

    [JsonPropertyName("fps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FpsTelemetry? Fps { get; init; }

    [JsonPropertyName("cpu")]
    public CpuTelemetry Cpu { get; init; } = new();

    [JsonPropertyName("gpu")]
    public GpuTelemetry Gpu { get; init; } = new();

    [JsonPropertyName("memory")]
    public MemoryTelemetry Memory { get; init; } = new();

    [JsonPropertyName("storage")]
    public IReadOnlyList<StorageTelemetry> Storage { get; init; } =
        Array.Empty<StorageTelemetry>();
}

public sealed record MediaStateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.MediaState;

    [JsonPropertyName("available")]
    public bool Available { get; init; } = true;

    [JsonPropertyName("mediaId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaId { get; init; }

    [JsonPropertyName("sourceApp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceApp { get; init; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    [JsonPropertyName("artist")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Artist { get; init; }

    [JsonPropertyName("album")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Album { get; init; }

    [JsonPropertyName("isPlaying")]
    public bool IsPlaying { get; init; }

    [JsonPropertyName("positionMs")]
    public long PositionMs { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("canPlayPause")]
    public bool CanPlayPause { get; init; }

    [JsonPropertyName("canPrevious")]
    public bool CanPrevious { get; init; }

    [JsonPropertyName("canNext")]
    public bool CanNext { get; init; }
}

public sealed record MediaArtworkMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.MediaArtwork;

    [JsonPropertyName("mediaId")]
    public string MediaId { get; init; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("mimeType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; init; }

    [JsonPropertyName("dataBase64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataBase64 { get; init; }
}

public sealed record VolumeStateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.VolumeState;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("volumePercent")]
    public int VolumePercent { get; init; }

    [JsonPropertyName("isMuted")]
    public bool IsMuted { get; init; }
}

public sealed record DesktopStateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.DesktopState;

    [JsonPropertyName("state")]
    public string State { get; init; } = DesktopStates.Stopped;

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Generation { get; init; }
}

public sealed record CommandMessage
{
    [JsonPropertyName("pid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pid { get; init; }
    [JsonPropertyName("startTimeUtcTicks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StartTimeUtcTicks { get; init; }
    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("decision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Decision { get; init; }

    [JsonPropertyName("sort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sort { get; init; }

    [JsonPropertyName("grouped")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Grouped { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Command;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Value { get; init; }

    [JsonPropertyName("pointerId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PointerId { get; init; }

    [JsonPropertyName("x")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Y { get; init; }

    [JsonPropertyName("generation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Generation { get; init; }

    /// <summary>Button the tablet pressed, for <c>streamdeck.invoke</c>.</summary>
    [JsonPropertyName("buttonId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ButtonId { get; init; }

    /// <summary>Layout the tablet believed it was drawing when it pressed.</summary>
    [JsonPropertyName("revision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Revision { get; init; }

    [JsonPropertyName("iconIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? IconIds { get; init; }

    [JsonPropertyName("mediaId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaId { get; init; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Offset { get; init; }
}

/// <summary>
/// Everything the tablet is allowed to know about the Stream Deck: what to
/// draw and the ids needed to ask for a button back.
/// </summary>
/// <remarks>
/// There is deliberately no member for an executable path, arguments, working
/// directory, URL or script anywhere in this message or the records it carries.
/// It is built from <see cref="PanelOS.Windows.StreamDeck.Dto.StreamDeckLayoutDto"/>,
/// which enforces the same boundary on the PC side.
/// </remarks>
public sealed record StreamDeckLayoutMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.StreamDeckLayout;

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = string.Empty;

    /// <summary>Null when nothing is configured yet.</summary>
    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamDeckProfileMessage? Profile { get; init; }

    [JsonPropertyName("pages")]
    public IReadOnlyList<StreamDeckPageMessage> Pages { get; init; } =
        Array.Empty<StreamDeckPageMessage>();
}

public sealed record StreamDeckProfileMessage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed record StreamDeckPageMessage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("columns")]
    public int Columns { get; init; }

    [JsonPropertyName("buttons")]
    public IReadOnlyList<StreamDeckButtonMessage> Buttons { get; init; } =
        Array.Empty<StreamDeckButtonMessage>();
}

public sealed record StreamDeckButtonMessage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("row")]
    public int Row { get; init; }

    [JsonPropertyName("column")]
    public int Column { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>Key into the PC icon cache; never a path.</summary>
    [JsonPropertyName("iconId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconId { get; init; }

    /// <summary>
    /// Name of a built-in shape the tablet draws itself. It is visual metadata
    /// derived from the action kind, not the action.
    /// </summary>
    [JsonPropertyName("glyph")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Glyph { get; init; }

    /// <summary>Whether the PC can run this button right now.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

public sealed record StreamDeckInvokeResultMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.StreamDeckInvokeResult;

    [JsonPropertyName("buttonId")]
    public string ButtonId { get; init; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Short explanation chosen from a fixed table for the error code. The
    /// executor's own message is never forwarded: it can quote a path.
    /// </summary>
    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

public sealed record StreamDeckIconMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.StreamDeckIcon;

    [JsonPropertyName("iconId")]
    public string IconId { get; init; } = string.Empty;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("mime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mime { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }
}

public sealed record PingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Ping;
}

public sealed record PongMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = MessageTypes.Pong;
}
