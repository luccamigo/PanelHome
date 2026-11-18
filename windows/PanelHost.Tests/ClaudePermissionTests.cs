using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;
using PanelOS.Windows.Management;

namespace PanelHost.Tests;

[TestClass]
public sealed class ClaudePermissionTests
{
    [TestMethod]
    public async Task Broker_enforces_shown_delay_fifo_exactly_once_and_generation()
    {
        var clock = new ManualClock();
        var broker = new ClaudePermissionBroker(clock);
        var generation = broker.Connect();
        var first = broker.RequestAsync(new("project", "Bash", "first", "", 0), 60, default);
        var second = broker.RequestAsync(new("other", "Read", "second", "", 0), 30, default);
        var requests = broker.Snapshot().Requests;
        Assert.AreEqual(2, requests.Count);
        var id = requests[0].Id;
        Assert.IsFalse(broker.Respond(id, "allow", generation));
        Assert.IsFalse(broker.Shown(requests[1].Id, generation));
        Assert.IsTrue(broker.Shown(id, generation));
        clock.Advance(.5);
        for (var i = 0; i < 10; i++) Assert.IsFalse(broker.Respond(id, "allow", generation));
        clock.Advance(.2);
        Assert.IsTrue(broker.Respond(id, "allow", generation));
        Assert.AreEqual("allow", await first);
        Assert.IsFalse(broker.Respond(id, "deny", generation));
        Assert.IsFalse(broker.Respond(requests[1].Id, "allow", generation));
        Assert.IsTrue(broker.Respond(requests[1].Id, "defer", generation));
        Assert.IsNull(await second);
        broker.Disconnect(generation);
        Assert.IsNull(await broker.RequestAsync(new("project", "Bash", "offline", "", 0), 60, default));
    }

    [TestMethod]
    public async Task Broker_expiration_and_disconnect_never_move_a_decision_to_another_request()
    {
        var clock = new ManualClock();
        var broker = new ClaudePermissionBroker(clock);
        var generation = broker.Connect();
        var first = broker.RequestAsync(new("project", "Bash", "command", "", 0), 30, default);
        var id = broker.Snapshot().Requests.Single().Id;
        clock.Advance(30);
        Assert.AreEqual(0, broker.Snapshot().Requests.Count);
        Assert.IsNull(await first);
        Assert.IsFalse(broker.Respond(id, "allow", generation));
        using var cancel = new CancellationTokenSource();
        var cancelled = broker.RequestAsync(new("project", "Bash", "command", "", 0), 60, cancel.Token);
        cancel.Cancel();
        Assert.IsNull(await cancelled);
        Assert.AreEqual(0, broker.Snapshot().Requests.Count);
        var last = broker.RequestAsync(new("project", "Bash", "command", "", 0), 60, default);
        var replacement = broker.Connect();
        broker.Disconnect(generation);
        Assert.IsNull(await last);
        Assert.IsTrue(replacement > generation);
    }

    [TestMethod]
    public async Task Real_user_only_pipe_returns_the_hook_schema_and_missing_tablet_returns_fast()
    {
        var broker = new ClaudePermissionBroker();
        var pipeName = "PanelOS.Claude.Test." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "PanelOS-HookTest-" + Guid.NewGuid().ToString("N"));
        using var server = new ClaudeHookPipeServer(broker, new LocalIntegrationSettingsStore(directory), pipeName);
        await server.StartAsync(default);
        try
        {
            var clock = Stopwatch.StartNew();
            Assert.IsNull(await ClaudeHookProtocol.RequestAsync(new("project", "Bash", "echo harmless", "", 0), pipeName));
            Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(2));
            var generation = broker.Connect();
            using var output = new StringWriter();
            var input = JsonSerializer.Serialize(new { hook_event_name = "PermissionRequest", cwd = "C:/project", tool_name = "Bash", tool_input = new { command = new string('x', 400) } });
            var hook = ClaudeHookProtocol.RunAsync(new StringReader(input), output, pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (broker.Snapshot().Requests.Count == 0) await Task.Delay(10, timeout.Token);
            var request = broker.Snapshot().Requests.Single();
            Assert.AreEqual("project", request.Project);
            Assert.AreEqual(40, request.Omitted);
            Assert.AreEqual(180, request.Head.Length);
            broker.Shown(request.Id, generation);
            await Task.Delay(720);
            Assert.IsTrue(broker.Respond(request.Id, "allow", generation));
            await hook.WaitAsync(TimeSpan.FromSeconds(2));
            using var response = JsonDocument.Parse(output.ToString());
            var specific = response.RootElement.GetProperty("hookSpecificOutput");
            Assert.AreEqual("PermissionRequest", specific.GetProperty("hookEventName").GetString());
            Assert.AreEqual("allow", specific.GetProperty("decision").GetProperty("behavior").GetString());
            broker.Disconnect(generation);
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally { await server.StopAsync(default); }
    }

    [TestMethod]
    public async Task Hook_failure_prints_nothing_and_structured_tools_stay_in_terminal()
    {
        using var output = new StringWriter();
        var input = JsonSerializer.Serialize(new { hook_event_name = "PermissionRequest", cwd = "C:/project", tool_name = "Bash", tool_input = new { command = "harmless" } });
        var watch = Stopwatch.StartNew();
        await ClaudeHookProtocol.RunAsync(new StringReader(input), output, "PanelOS.Missing." + Guid.NewGuid().ToString("N"));
        Assert.AreEqual("", output.ToString());
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(2));
        await ClaudeHookProtocol.RunAsync(new StringReader(input.Replace("Bash", "AskUserQuestion")), output);
        Assert.AreEqual("", output.ToString());
        var cut = ClaudeHookProtocol.Summarize("p", "t", string.Concat(Enumerable.Repeat("😀", 400)));
        Assert.AreEqual(40, cut.Omitted);
        Assert.AreEqual(180, cut.Head.EnumerateRunes().Count());
    }

    [TestMethod]
    public async Task Allow_stays_available_on_an_incomplete_summary_and_the_log_records_it_without_content()
    {
        var clock = new ManualClock();
        var log = new CapturingLogger();
        var broker = new ClaudePermissionBroker(clock, log);
        var generation = broker.Connect();
        var secretHead = "curl-secret-head";
        var secretTail = "sh-secret-tail";
        var cut = broker.RequestAsync(new("project", "Bash", secretHead, secretTail, 40), 60, default);
        var edit = broker.RequestAsync(new("project", "Edit", "C:/project/file.cs", "", 0, ContentHidden: true), 60, default);
        var complete = broker.RequestAsync(new("project", "Bash", "echo harmless", "", 0), 60, default);
        var requests = broker.Snapshot().Requests;
        Assert.AreEqual(40, requests[0].Omitted);
        Assert.IsFalse(requests[0].ContentHidden);
        Assert.IsTrue(requests[1].ContentHidden);
        Assert.IsFalse(requests[2].ContentHidden);
        foreach (var request in requests)
        {
            Assert.IsTrue(broker.Shown(request.Id, generation));
            clock.Advance(.8);
            Assert.IsTrue(broker.Respond(request.Id, "allow", generation));
        }
        Assert.AreEqual("allow", await cut);
        Assert.AreEqual("allow", await edit);
        Assert.AreEqual("allow", await complete);
        var allowed = log.Entries.Where(x => x.Text.Contains("result=allow")).ToArray();
        Assert.AreEqual(3, allowed.Length);
        Assert.IsTrue(allowed[0].Text.Contains("incompleteSummary=True") && allowed[0].Text.Contains("omitted=40"));
        Assert.IsTrue(allowed[1].Text.Contains("incompleteSummary=True") && allowed[1].Text.Contains("contentHidden=True"));
        Assert.IsTrue(allowed[2].Text.Contains("incompleteSummary=False"));
        Assert.AreEqual(LogLevel.Warning, allowed[0].Level);
        Assert.AreEqual(LogLevel.Warning, allowed[1].Level);
        Assert.AreEqual(LogLevel.Information, allowed[2].Level);
        foreach (var entry in log.Entries)
            Assert.IsFalse(entry.Text.Contains(secretHead) || entry.Text.Contains(secretTail) || entry.Text.Contains("file.cs") || entry.Text.Contains("echo harmless"));
    }

    [TestMethod]
    public async Task Only_edit_tools_that_show_just_a_path_mark_the_content_hidden()
    {
        var pipeName = "PanelOS.Claude.Test." + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "PanelOS-HookTest-" + Guid.NewGuid().ToString("N"));
        var broker = new ClaudePermissionBroker();
        using var server = new ClaudeHookPipeServer(broker, new LocalIntegrationSettingsStore(directory), pipeName);
        await server.StartAsync(default);
        try
        {
            broker.Connect();
            var cases = new (string Tool, object Input, bool Hidden)[]
            {
                ("Edit", new { file_path = "C:/project/a.cs", old_string = "a", new_string = "b" }, true),
                ("Write", new { file_path = "C:/project/a.cs", content = "x" }, true),
                ("MultiEdit", new { file_path = "C:/project/a.cs", edits = new[] { new { old_string = "a", new_string = "b" } } }, true),
                ("Read", new { file_path = "C:/project/a.cs" }, false),
                ("Bash", new { command = "echo harmless" }, false),
            };
            foreach (var (tool, input, hidden) in cases)
            {
                using var output = new StringWriter();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var text = JsonSerializer.Serialize(new { hook_event_name = "PermissionRequest", cwd = "C:/project", tool_name = tool, tool_input = input });
                var hook = ClaudeHookProtocol.RunAsync(new StringReader(text), output, pipeName, cancel.Token);
                while (broker.Snapshot().Requests.Count == 0) await Task.Delay(10, cancel.Token);
                var request = broker.Snapshot().Requests.Single();
                Assert.AreEqual(hidden, request.ContentHidden, tool);
                Assert.AreEqual(0, request.Omitted, tool);
                cancel.Cancel();
                await hook;
                var deadline = Stopwatch.StartNew();
                while (broker.Snapshot().Requests.Count > 0 && deadline.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            }
        }
        finally { await server.StopAsync(default); }
    }

    private sealed class CapturingLogger : ILogger<ClaudePermissionBroker>
    {
        internal readonly List<(LogLevel Level, string Text)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Entries) Entries.Add((logLevel, formatter(state, exception))); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_milliseconds);
        internal void Advance(double seconds) => _milliseconds += (long)(seconds * 1000);
    }
}
