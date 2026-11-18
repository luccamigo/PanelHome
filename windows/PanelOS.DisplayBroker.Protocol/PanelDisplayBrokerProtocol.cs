using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PanelOS.DriverInterop;

namespace PanelOS.DisplayBroker.Protocol;

/// <summary>
/// Small, local-only control protocol between PanelHost and PanelDisplayBroker.
/// It is deliberately unrelated to Panel Protocol and management v1.
/// </summary>
public static class PanelDisplayBrokerProtocol
{
    public const int Version = 1;
    public const int MaximumControlMessageBytes = 64 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    public static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }
}

public enum PanelDisplayBrokerOperation
{
    GetStatus,
    Connect,
    Disconnect,
    GetCaptureMetrics,
    SetCaptureTarget,
    GetCpuTemperature,
}

public sealed record PanelDisplayBrokerHello(
    int ProtocolVersion,
    string Nonce,
    int ParentProcessId,
    long ParentStartTimeUtcTicks,
    bool CpuTemperatureAvailable = false);

public sealed record PanelDisplayBrokerHelloResponse(
    int ProtocolVersion,
    bool Authenticated,
    string? ErrorCode = null);

public sealed record PanelDisplayBrokerRequest(
    long RequestId,
    int ProtocolVersion,
    PanelDisplayBrokerOperation Operation,
    uint? TargetFramesPerSecond = null);

public sealed record PanelDisplayBrokerResponse(
    long RequestId,
    int ProtocolVersion,
    bool Succeeded,
    PanelDisplayDriverStatus? Status = null,
    PanelDisplayCommandResponse? Command = null,
    PanelDisplayCaptureMetrics? CaptureMetrics = null,
    uint? ConfiguredFramesPerSecond = null,
    PanelDisplayBrokerError? Error = null,
    double? CpuTemperatureC = null);

public sealed record PanelDisplayBrokerError(
    string Code,
    string Message,
    int? Win32ErrorCode = null);

public enum PanelDisplayBrokerDisconnectReason
{
    PipeClosed,
    BrokerExited,
    AuthenticationFailed,
    ProtocolError,
    HostShutdown,
    Unresponsive,
    /// <summary>The operator did not approve (or closed) the Windows elevation prompt.</summary>
    ElevationDeclined,
}

public sealed class PanelDisplayBrokerDisconnectedEventArgs(
    PanelDisplayBrokerDisconnectReason reason,
    string? detail = null) : EventArgs
{
    public PanelDisplayBrokerDisconnectReason Reason { get; } = reason;

    public string? Detail { get; } = detail;
}

public interface IPanelDisplayBrokerConnection
{
    bool IsConnected { get; }

    // Ending a lease must not launch a new privileged process just to query it.
    Task<PanelDisplayDriverStatus> GetStatusIfConnectedAsync(CancellationToken cancellationToken);

    event EventHandler<PanelDisplayBrokerDisconnectedEventArgs>? Disconnected;
}

public sealed class PanelDisplayBrokerDisconnectedException : IOException
{
    public PanelDisplayBrokerDisconnectedException(
        PanelDisplayBrokerDisconnectReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public PanelDisplayBrokerDisconnectReason Reason { get; }
}

/// <summary>Single-use constant-time authenticator for one broker launch.</summary>
public sealed class PanelDisplayBrokerAuthenticator
{
    private readonly byte[] _expectedNonce;
    private int _consumed;

    public PanelDisplayBrokerAuthenticator(string nonce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        _expectedNonce = Convert.FromHexString(nonce);
        if (_expectedNonce.Length < 32)
        {
            throw new ArgumentException(
                "Broker nonce must contain at least 256 bits.",
                nameof(nonce));
        }
    }

    public bool TryAuthenticate(string? candidate)
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0 ||
            string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(candidate);
        }
        catch (FormatException)
        {
            return false;
        }

        return supplied.Length == _expectedNonce.Length &&
            CryptographicOperations.FixedTimeEquals(supplied, _expectedNonce);
    }
}

/// <summary>Reads one bounded UTF-8 control message terminated by LF.</summary>
public sealed class BrokerControlMessageReader(Stream stream)
{
    private readonly Stream _stream =
        stream ?? throw new ArgumentNullException(nameof(stream));

    public async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await _stream.ReadAsync(one, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.Length == 0
                    ? null
                    : throw new EndOfStreamException(
                        "Broker control message ended before LF.");
            }

            if (one[0] == (byte)'\n')
            {
                var bytes = buffer.ToArray();
                if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                {
                    Array.Resize(ref bytes, bytes.Length - 1);
                }
                return new UTF8Encoding(false, true).GetString(bytes);
            }

            if (buffer.Length >= PanelDisplayBrokerProtocol.MaximumControlMessageBytes)
            {
                throw new InvalidDataException("Broker control message is too large.");
            }
            buffer.WriteByte(one[0]);
        }
    }
}
