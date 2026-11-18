using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using PanelOS.Windows.Protocol;

namespace PanelOS.Windows.Management;

// ContentHidden (additive): the tool changes a file but the summary shows only its path, never the edit itself.
public sealed record ClaudeHookRequest(string Project, string Tool, string Head, string Tail, int Omitted, bool ContentHidden = false);
public sealed record ClaudeHookReply(string? Decision);

public static class ClaudeHookProtocol
{
    public const int MaximumBytes = 8192;
    public static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? throw new UnauthorizedAccessException();
            return "PanelOS.Claude.v1." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid))).ToLowerInvariant()[..16];
        }
    }

    public static bool HidesEditContent(string tool, bool summaryIsFilePath) => summaryIsFilePath && tool is "Edit" or "Write" or "MultiEdit";

    public static ClaudeHookRequest Summarize(string project, string tool, string summary, bool contentHidden = false)
    {
        var runes = summary.EnumerateRunes().ToArray();
        return runes.Length <= 360 ? new(project, tool, summary, "", 0, contentHidden) :
            new(project, tool, string.Concat(runes.Take(180)), string.Concat(runes.TakeLast(180)), runes.Length - 360, contentHidden);
    }

    public static async Task<string?> RequestAsync(ClaudeHookRequest request, string? pipeName = null, CancellationToken token = default)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(TimeSpan.FromSeconds(125));
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        connect.CancelAfter(TimeSpan.FromMilliseconds(700));
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, PanelHostManagementProtocol.JsonOptions);
        if (bytes.Length > MaximumBytes) return null;
        await pipe.WriteAsync(bytes, total.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new byte[] { 10 }, total.Token).ConfigureAwait(false);
        await pipe.FlushAsync(total.Token).ConfigureAwait(false);
        var line = await new BoundedUtf8LineReader(pipe, MaximumBytes).ReadLineAsync(total.Token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<ClaudeHookReply>(line ?? "{}", PanelHostManagementProtocol.JsonOptions);
        return reply?.Decision is "allow" or "deny" ? reply.Decision : null;
    }

    public static async Task RunAsync(TextReader input, TextWriter output, string? pipeName = null, CancellationToken token = default)
    {
        try
        {
            var buffer = new char[1024];
            var text = new StringBuilder();
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) break;
                if (text.Length + count > 65536) return;
                text.Append(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(text.ToString(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.GetProperty("hook_event_name").GetString() != "PermissionRequest") return;
            var tool = root.GetProperty("tool_name").GetString() ?? "";
            // These tools require structured updatedInput; a binary allow cannot answer them safely.
            if (tool is "AskUserQuestion" or "ExitPlanMode") return;
            var cwd = root.GetProperty("cwd").GetString() ?? "";
            var project = cwd.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? "";
            var payload = root.GetProperty("tool_input");
            var fromPath = false;
            string summary;
            if (payload.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String) summary = command.GetString()!;
            else if (payload.TryGetProperty("file_path", out var path) && path.ValueKind == JsonValueKind.String) { summary = path.GetString()!; fromPath = true; }
            else summary = payload.GetRawText();
            var decision = await RequestAsync(Summarize(project, tool, summary, HidesEditContent(tool, fromPath)), pipeName, token).ConfigureAwait(false);
            if (decision is null) return;
            await output.WriteAsync(JsonSerializer.Serialize(new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = decision } } })).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException or UnauthorizedAccessException or ArgumentException)
        { /* No output and exit zero preserves Claude Code's permission flow. */ }
    }
}
