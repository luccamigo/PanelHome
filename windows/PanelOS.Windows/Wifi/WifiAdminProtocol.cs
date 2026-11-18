using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelOS.Windows.Wifi;

/// <summary>
/// Local administration of the WiFi pairing, between the Control Center and the process that owns the WiFi
/// identity (today the bench window of PanelHost). It is a user-only named pipe and is not the PanelHome wire
/// protocol: it carries the pairing QR and the operator's approve/deny/revoke decisions, and nothing from the
/// panel session. One request, one response, on a fresh connection.
/// </summary>
public static class WifiAdminProtocol
{
    public const string PipeName = "PanelOS.Wifi.Admin.v1";
    public const int Version = 1;
    public const int MaxRequestBytes = 4096;

    public const string Status = "status";
    public const string OpenPairing = "open_pairing";
    public const string CancelPairing = "cancel_pairing";
    public const string Approve = "approve";
    public const string Deny = "deny";
    public const string Revoke = "revoke";

    public const string ModeQr = "qr";
    public const string ModeCode = "code";

    // Error codes are stable identifiers, never prose with data from the peer.
    public const string ErrorUnavailable = "unavailable";
    public const string ErrorUnsupported = "unsupported_request";
    public const string ErrorInvalid = "invalid_request";
    public const string ErrorPairingOpen = "pairing_already_open";
    public const string ErrorNoRequest = "no_matching_request";
    public const string ErrorUnknownDevice = "unknown_device";
    public const string ErrorWindowClosed = "window_closed";
    public const string ErrorFailed = "failed";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(WifiAdminRequest request) => JsonSerializer.Serialize(request, JsonOptions);
    public static string Serialize(WifiAdminResponse response) => JsonSerializer.Serialize(response, JsonOptions);
    public static WifiAdminRequest? DeserializeRequest(string json) => JsonSerializer.Deserialize<WifiAdminRequest>(json, JsonOptions);
    public static WifiAdminResponse? DeserializeResponse(string json) => JsonSerializer.Deserialize<WifiAdminResponse>(json, JsonOptions);
}

public sealed record WifiAdminRequest(int ProtocolVersion, string Type, string? Mode = null, string? Fingerprint = null);

public sealed record WifiAdminResponse(bool Ok, string? Error, WifiAdminStatus? Status);

public sealed record WifiAdminStatus(
    bool Running,
    int WindowSecondsLeft,
    string BindAddress,
    int SessionPort,
    int PairPort,
    WifiAdminPairing? Pairing,
    IReadOnlyList<WifiAdminDevice> Devices,
    // SHA-256 of this PC's root key (64 hex): the value the tablet shows for "the PC" so the operator can compare both screens.
    string? HostId = null);

/// <summary>
/// The open invitation. Qr / Code hold the secret that lets a device ask to pair: they exist only while the
/// invitation is open, only on this pipe, and the UI must not log, copy or persist them.
/// </summary>
public sealed record WifiAdminPairing(
    string Mode,
    string Outcome,
    int ExpiresInSeconds,
    string? Qr,
    string? Code,
    WifiAdminPending? Pending);

public sealed record WifiAdminPending(string Label, string Fingerprint, int ApproveSecondsLeft);

public sealed record WifiAdminDevice(string Fingerprint, string? Label, string State, DateTimeOffset? CreatedAt, bool Connected);