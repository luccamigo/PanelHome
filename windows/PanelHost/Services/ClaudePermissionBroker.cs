using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelOS.Windows.Management;

namespace PanelHost.Services;

internal sealed record ClaudePermission(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("tail")] string Tail,
    [property: JsonPropertyName("omitted")] int Omitted,
    [property: JsonPropertyName("expiresAtUnixMs")] long ExpiresAtUnixMs,
    [property: JsonPropertyName("contentHidden")] bool ContentHidden = false)
{
    internal bool Incomplete => Omitted > 0 || ContentHidden;
}
internal sealed record ClaudePermissionsMessage(
    [property: JsonPropertyName("sampledAtUnixMs")] long SampledAtUnixMs,
    [property: JsonPropertyName("requests")] IReadOnlyList<ClaudePermission> Requests)
{
    [JsonPropertyName("type")] public string Type => "claude_permissions";
}

internal sealed class ClaudePermissionBroker(TimeProvider? time = null, ILogger<ClaudePermissionBroker>? logger = null)
{
    private readonly object _sync = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = logger ?? NullLogger<ClaudePermissionBroker>.Instance;
    private readonly List<Pending> _pending = [];
    private long _generation;
    private bool _connected;
    internal bool Connected { get { lock (_sync) return _connected; } }
    internal string? LastDecision { get; private set; }
    internal long? LastRequestUnixMs { get; private set; }
    private sealed record Pending(ClaudePermission Request, TaskCompletionSource<string?> Reply)
    { internal long? ShownAt { get; set; } }

    internal long Connect()
    {
        lock (_sync) { Clear("disconnected"); _connected = true; return ++_generation; }
    }
    internal void Disconnect(long generation)
    { lock (_sync) { if (generation != _generation) return; _connected = false; Clear("disconnected"); } }
    private void Clear(string reason) { foreach (var pending in _pending.ToArray()) Complete(pending, null, reason); }

    internal async Task<string?> RequestAsync(ClaudeHookRequest request, int timeoutSeconds, CancellationToken token)
    {
        Pending pending;
        lock (_sync)
        {
            Expire();
            if (!_connected || _pending.Count >= 20 || timeoutSeconds is not (30 or 60 or 90 or 120)) return null;
            var project = Validate(request.Project, 80);
            var tool = Validate(request.Tool, 80);
            var head = Validate(request.Head, 360, allowEmpty: true);
            var tail = Validate(request.Tail, 180, allowEmpty: true);
            if (request.Omitted < 0 || request.Omitted == 0 && tail.Length > 0 || request.Omitted > 0 &&
                (head.EnumerateRunes().Count() > 180 || head.Length == 0 || tail.Length == 0)) return null;
            var permission = new ClaudePermission(Guid.NewGuid().ToString("N"), project, tool, head, tail, request.Omitted,
                _time.GetUtcNow().AddSeconds(timeoutSeconds).ToUnixTimeMilliseconds(), request.ContentHidden);
            pending = new(permission, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending.Add(pending);
            LastRequestUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
            Audit(pending, "requested");
        }
        try { return await pending.Reply.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), _time, token); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return null; }
        finally { lock (_sync) { if (_pending.Contains(pending)) Complete(pending, null, token.IsCancellationRequested ? "cancelled" : "expired"); } }
    }

    private static string Validate(string text, int maximum, bool allowEmpty = false)
    {
        var clean = LocalPayloadParser.Clean(text, true);
        if ((!allowEmpty && string.IsNullOrWhiteSpace(clean)) || clean.EnumerateRunes().Count() > maximum) throw new ArgumentException("invalid_hook_text");
        return clean;
    }

    internal ClaudePermissionsMessage Snapshot()
    {
        lock (_sync) { Expire(); return new(_time.GetUtcNow().ToUnixTimeMilliseconds(), _pending.Select(x => x.Request).ToArray()); }
    }

    internal bool Shown(string id, long generation)
    {
        lock (_sync)
        {
            Expire();
            var first = _pending.FirstOrDefault();
            if (!_connected || generation != _generation || first?.Request.Id != id) return false;
            first.ShownAt ??= _time.GetTimestamp();
            return true;
        }
    }

    internal bool Respond(string id, string decision, long generation)
    {
        lock (_sync)
        {
            Expire();
            var first = _pending.FirstOrDefault();
            if (!_connected || generation != _generation || first?.Request.Id != id || decision is not ("allow" or "deny" or "defer")) return false;
            if (decision != "defer" && (first.ShownAt is not long shown || _time.GetElapsedTime(shown) < TimeSpan.FromMilliseconds(700))) return false;
            Complete(first, decision == "defer" ? null : decision, decision);
            return true;
        }
    }

    private void Expire()
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var pending in _pending.Where(x => x.Request.ExpiresAtUnixMs <= now).ToArray()) Complete(pending, null, "expired");
    }
    private void Complete(Pending pending, string? decision, string audit)
    { _pending.Remove(pending); LastDecision = audit; pending.Reply.TrySetResult(decision); Audit(pending, audit); }
    // D7 (mantenedor): allow stays available on a cut or path-only summary, so the log must say it was decided blind.
    // Only counts and flags are logged, never the command or the file content.
    private void Audit(Pending pending, string result)
    {
        var request = pending.Request;
        var level = result == "allow" && request.Incomplete ? LogLevel.Warning : LogLevel.Information;
        _log.Log(level, "Claude permission project={Project} tool={Tool} result={Result} incompleteSummary={Incomplete} omitted={Omitted} contentHidden={ContentHidden}",
            request.Project, request.Tool, result, request.Incomplete, request.Omitted, request.ContentHidden);
    }
}
