using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using PanelOS.Windows.Management;
using PanelOS.Windows.Protocol;

namespace PanelHost.Services;

internal sealed class ClaudeHookPipeServer(ClaudePermissionBroker broker, LocalIntegrationSettingsStore settings,
    string? pipeName = null) : BackgroundService
{
    private readonly string _pipeName = pipeName ?? ClaudeHookProtocol.PipeName;
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly LocalTokenBucket _rate = new(10, 20);
    private int _clientId;
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 1024);
                try { await pipe.WaitForConnectionAsync(token); }
                catch { await pipe.DisposeAsync(); throw; }
                var id = Interlocked.Increment(ref _clientId);
                var task = HandleAsync(pipe, token);
                _clients[id] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(id, out var ignored), TaskScheduler.Default);
                if (_clients.Count >= 4) await Task.WhenAny(_clients.Values);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(_clients.Values); }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken token)
    {
        await using var lifetime = pipe;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var host = WindowsIdentity.GetCurrent();
            string? clientSid = null;
            pipe.RunAsClient(() => { using var client = WindowsIdentity.GetCurrent(); clientSid = client.User?.Value; });
            if (clientSid is null || clientSid != host.User?.Value) return;
            lock (_rate) { if (!_rate.Take(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency)) return; }
            var reader = new BoundedUtf8LineReader(pipe, ClaudeHookProtocol.MaximumBytes);
            var line = await reader.ReadLineAsync(wait.Token);
            if (line is null) return;
            using var parsed = LocalPayloadParser.Parse(Encoding.UTF8.GetBytes(line), "project", "tool", "head", "tail", "omitted", "contentHidden");
            var root = parsed.RootElement;
            var request = new ClaudeHookRequest(LocalPayloadParser.Text(root, "project", 80, true)!,
                LocalPayloadParser.Text(root, "tool", 80, true)!, LocalPayloadParser.Text(root, "head", 360, multiline: true) ?? "",
                LocalPayloadParser.Text(root, "tail", 180, multiline: true) ?? "", LocalPayloadParser.Number(root, "omitted", 0, int.MaxValue) ?? 0,
                root.TryGetProperty("contentHidden", out var hidden) && hidden.ValueKind == JsonValueKind.True);
            var timeout = settings.Load().PermissionTimeoutSeconds;
            wait.CancelAfter(TimeSpan.FromSeconds(timeout + 1));
            var decision = broker.RequestAsync(request, timeout, wait.Token);
            // EOF cancels this request if Claude closes the hook; later taps cannot decide another request.
            var disconnected = reader.ReadLineAsync(wait.Token).AsTask();
            if (await Task.WhenAny(decision, disconnected) != decision) { wait.Cancel(); await decision; return; }
            var reply = await decision;
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ClaudeHookReply(reply), PanelHostManagementProtocol.JsonOptions) + "\n"), wait.Token);
            await pipe.FlushAsync(wait.Token);
            wait.Cancel();
            try { await disconnected; } catch (OperationCanceledException) { }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException or
            JsonException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException) { }
    }
}
