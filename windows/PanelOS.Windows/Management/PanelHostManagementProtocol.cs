using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelOS.Windows.Management;

/// <summary>Local management API. It is not the PanelHome wire protocol.</summary>
public static class PanelHostManagementProtocol
{
    public const string PipeName = "PanelOS.Host.v1";
    public const int Version = 1;
    public const string GetSnapshot = "get_snapshot";
    public const string Subscribe = "subscribe";
    public const string DesktopStart = "desktop_start";
    public const string DesktopStop = "desktop_stop";
    public const string Snapshot = "snapshot";
    public const string DesktopResult = "desktop_result";
    public const string Error = "error";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SerializeRequest(string type) =>
        JsonSerializer.Serialize(
            new PanelHostManagementRequest(type, Version),
            JsonOptions);

    public static string SerializeSnapshot(PanelHostSnapshot snapshot) =>
        JsonSerializer.Serialize(
            new PanelHostManagementResponse(Snapshot, Version, snapshot, null),
            JsonOptions);

    public static string SerializeError(string error) =>
        JsonSerializer.Serialize(
            new PanelHostManagementResponse(Error, Version, null, error),
            JsonOptions);

    public static string SerializeDesktopResult(
        PanelHostManagementDesktopResult result) =>
        JsonSerializer.Serialize(
            new PanelHostManagementResponse(
                DesktopResult,
                Version,
                null,
                null,
                result),
            JsonOptions);

    public static PanelHostManagementRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<PanelHostManagementRequest>(json, JsonOptions);

    public static PanelHostManagementResponse? DeserializeResponse(string json) =>
        JsonSerializer.Deserialize<PanelHostManagementResponse>(json, JsonOptions);
}

public sealed record PanelHostManagementRequest(string Type, int ProtocolVersion);

public sealed record PanelHostManagementResponse(
    string Type,
    int ProtocolVersion,
    PanelHostSnapshot? Snapshot,
    string? Error,
    PanelHostManagementDesktopResult? Desktop = null);

public sealed record PanelHostManagementDesktopResult(
    bool Succeeded,
    bool AlreadyInDesiredState,
    string State,
    string? ErrorCode = null);

public sealed record PanelHostSnapshot
{
    public required long Sequence { get; init; }

    public required int ManagementProtocolVersion { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required HostManagementSnapshot Host { get; init; }

    public required TabletManagementSnapshot Tablet { get; init; }

    public required DisplayManagementSnapshot Display { get; init; }

    public required VideoManagementSnapshot Video { get; init; }

    public required StreamDeckManagementSnapshot StreamDeck { get; init; }

    /// <summary>
    /// Additive Management v1 health data. Older peers ignore this property and
    /// newer peers tolerate it being absent in snapshots from an older host.
    /// </summary>
    public HostServicesManagementSnapshot? Services { get; init; }
}

public sealed record HostServicesManagementSnapshot(
    HostServiceManagementSnapshot Telemetry,
    HostServiceManagementSnapshot Media);

public sealed record HostServiceManagementSnapshot(
    string State,
    string? ErrorCode = null);

public sealed record HostManagementSnapshot(
    int ProcessId,
    string? Version,
    DateTimeOffset StartedAtUtc,
    double UptimeSeconds);

public sealed record TabletManagementSnapshot(
    bool ControlConnected,
    bool VideoConnected,
    int? ProtocolVersion,
    ImmutableArray<string> NegotiatedFeatures,
    long ControlSessionGeneration,
    long VideoSessionGeneration);

public sealed record DisplayManagementSnapshot(
    bool Connected,
    string State,
    int? Width,
    int? Height,
    int? RefreshRate);

public sealed record VideoManagementSnapshot
{
    public bool Connected { get; init; }

    public long SessionGeneration { get; init; }

    public string? Codec { get; init; }

    public string? Encoder { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public int? ConfiguredFps { get; init; }

    public int? NegotiatedFps { get; init; }

    public double? DriverCaptureFps { get; init; }

    public double? HostReceiveFps { get; init; }

    public double? SendFps { get; init; }

    public double? BandwidthMegabytesPerSecond { get; init; }

    public double? EncodeLatencyMilliseconds { get; init; }

    public double? CaptureToSendMilliseconds { get; init; }

    public long? SequenceGap { get; init; }
}

public sealed record StreamDeckManagementSnapshot(
    string? ActiveProfileId,
    string? ActiveProfileName,
    string? PublishedRevision,
    int PageCount,
    int ConfiguredButtonCount);
