using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PanelOS.Windows.Protocol;

namespace PanelOS.Windows.Management;

public sealed record LocalIntegrationAdminRequest(string Action, bool? Enabled = null, int? Port = null,
    int? PermissionTimeoutSeconds = null)
{
    public string Type => "local_integrations";
    public int ProtocolVersion => 1;
}
public sealed record LocalIntegrationAdminResult(bool Enabled, int Port, string State, string? KeySuffix,
    long KeyGeneration, long? LastRequestUnixMs, int RejectedAuthenticationToday,
    int PermissionTimeoutSeconds, string? OneTimeKey = null, string? Error = null)
{
    public bool ProcessTerminationEnabled { get; init; }
    public bool ProcessTerminationReviewed { get; init; }
    public bool TabletConnected { get; init; }
    public string? LastPermissionDecision { get; init; }
    public long? LastPermissionUnixMs { get; init; }
}

public interface ILocalIntegrationAdmin
{
    Task<LocalIntegrationAdminResult> SendAsync(LocalIntegrationAdminRequest request, CancellationToken token = default);
}
public sealed class LocalIntegrationAdminClient(string pipeName = PanelHostManagementProtocol.PipeName) : ILocalIntegrationAdmin
{
    public async Task<LocalIntegrationAdminResult> SendAsync(LocalIntegrationAdminRequest request, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(request.Action == "test_permission" ? 125 : 2));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(request, PanelHostManagementProtocol.JsonOptions) + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(json), timeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
        var line = await new BoundedUtf8LineReader(pipe).ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LocalIntegrationAdminResult>(line ?? throw new IOException("Host disconnected"),
            PanelHostManagementProtocol.JsonOptions) ?? throw new IOException("Invalid host response");
    }
}
