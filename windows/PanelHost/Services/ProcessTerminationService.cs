using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PanelHost.Services;

internal sealed record ProcessTerminationResult(
    [property: JsonPropertyName("requestId")] string? RequestId,
    [property: JsonPropertyName("pid")] int? Pid,
    [property: JsonPropertyName("result")] string Result)
{
    [JsonPropertyName("type")] public string Type => "processes.terminate_result";
}

internal sealed class ProcessTerminationService(IProcessTerminationTargets targets, ILogger<ProcessTerminationService>? logger = null,
    bool reviewed = ProcessProtectionPolicy.Reviewed)
{
    private readonly ILogger _log = logger ?? NullLogger<ProcessTerminationService>.Instance;
    private int _enabled;
    private readonly object _sync = new();
    internal bool Reviewed => reviewed;
    internal bool Enabled => Volatile.Read(ref _enabled) != 0;
    internal bool SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (enabled && !reviewed) return false;
            Volatile.Write(ref _enabled, enabled ? 1 : 0); return true;
        }
    }

    internal static bool Allowed(ProcessSafetyFacts facts) => facts.SameUser && facts.SameSession && facts.IntegrityAllowed &&
        !facts.Critical && facts.OutsideWindows && !facts.ParentOfHost && !facts.OpenBotOwned && facts.Pid != Environment.ProcessId && !ProcessProtectionPolicy.IsProtectedName(facts.Name);

    internal ProcessTerminationResult Terminate(string? requestId, int? pid, long? startTicks, bool offered, bool listedProtected = false)
    {
        lock (_sync) return TerminateCore(requestId, pid, startTicks, offered, listedProtected);
    }

    private ProcessTerminationResult TerminateCore(string? requestId, int? pid, long? startTicks, bool offered, bool listedProtected)
    {
        var result = "disabled"; var name = "unknown";
        try
        {
            if (!Enabled || !reviewed) return new(requestId, pid, result);
            if (requestId is not { Length: 32 } || pid is not > 4 || startTicks is not > 0) { result = "invalid_request"; return new(requestId, pid, result); }
            if (!offered) { result = listedProtected ? "protected" : "gone"; return new(requestId, pid, result); }
            using var target = targets.Open(pid.Value);
            if (target is null) { result = "gone"; return new(requestId, pid, result); }
            var facts = target.Facts;
            name = LocalPayloadParser.Clean(facts.Name);
            if (facts.Pid != pid || facts.StartTimeUtcTicks != startTicks) result = "gone";
            else if (!Allowed(facts)) result = "protected";
            else result = target.Terminate() ? "terminated" : "denied";
            return new(requestId, pid, result);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        { result = "denied"; return new(requestId, pid, result); }
        finally { _log.LogInformation("Process termination pid={Pid} name={Name} result={Result}", pid, name, result); }
    }
}
