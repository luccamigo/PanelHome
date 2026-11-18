using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelOS.Windows.Management;

namespace PanelHost.Services;

internal sealed class LocalIntegrationService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly LocalIntegrationSettingsStore _store;
    private readonly LocalNoticeBroker _broker;
    private readonly ILogger _log;
    private readonly Func<bool> _isElevated;
    private readonly ClaudePermissionBroker? _permissions;
    private readonly ProcessTerminationService? _termination;
    private readonly Func<string, bool> _pageExists;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots = new(LocalIntegrationLimits.Connections);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly List<Task> _acceptors = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LocalIntegrationSettings _settings = new();
    private LocalRateLimits _limits = new();
    private TcpListener? _listener;
    private string _state = "disabled";
    private long _clientId;
    private int _disposed;
    private long? _lastRequest;
    private DateOnly _authDay = DateOnly.FromDateTime(DateTime.Now);
    private int _authToday, _authWindow, _rateWindow;
    private Timer? _aggregateTimer;

    internal LocalIntegrationService(LocalIntegrationSettingsStore store, LocalNoticeBroker broker,
        ILogger<LocalIntegrationService>? log = null, Func<bool>? isElevated = null, ClaudePermissionBroker? permissions = null,
        ProcessTerminationService? termination = null, Func<string, bool>? pageExists = null)
    {
        _store = store; _broker = broker; _log = log ?? NullLogger<LocalIntegrationService>.Instance;
        _permissions = permissions;
        _termination = termination;
        _pageExists = pageExists ?? (id => id is "computer" or "processes" or "notices");
        _isElevated = isElevated ?? (() => { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); });
    }

    internal void Initialize()
    {
        lock (_sync)
        {
            try { _settings = _store.Load(); StartListener(); }
            catch (Exception error) when (error is IOException or System.Security.Cryptography.CryptographicException or JsonException or FormatException or UnauthorizedAccessException)
            { _state = "settings_unavailable"; }
        }
    }

    internal LocalIntegrationAdminResult Admin(LocalIntegrationAdminRequest request)
    {
        lock (_sync)
        {
            string? key = null;
            try
            {
                switch (request.Action)
                {
                    case "status": break;
                    case "processes.configure":
                        if (_termination is null || request.Enabled is not bool enabled) return Result(error: "invalid_request");
                        if (!_termination.SetEnabled(enabled)) return Result(error: "review_required");
                        break;
                    case "configure":
                        if (request.Port is int port && !LocalIntegrationSettingsStore.ValidPort(port)) return Result(error: "invalid_port");
                        if (request.PermissionTimeoutSeconds is int seconds && seconds is not (30 or 60 or 90 or 120)) return Result(error: "invalid_timeout");
                        var next = _settings with { Enabled = request.Enabled ?? _settings.Enabled, Port = request.Port ?? _settings.Port,
                            PermissionTimeoutSeconds = request.PermissionTimeoutSeconds ?? _settings.PermissionTimeoutSeconds };
                        if (!next.Enabled) next = next with { KeyHash = null, KeySuffix = null };
                        _store.Save(next); _settings = next;
                        _listener?.Stop(); _listener = null;
                        if (!next.Enabled) { _broker.Clear(); _limits = new(); }
                        StartListener();
                        _log.LogInformation("Local integrations configured enabled={Enabled} generation={Generation}", next.Enabled, next.Generation);
                        break;
                    case "rotate_key":
                        key = LocalIntegrationKey.Create();
                        var rotated = _settings with { KeyHash = Convert.ToBase64String(LocalIntegrationKey.Hash(key)), KeySuffix = key[^4..], Generation = _settings.Generation + 1 };
                        _store.Save(rotated); _settings = rotated;
                        if (_listener is null) StartListener();
                        _log.LogInformation("Local integrations rotated generation={Generation}", rotated.Generation);
                        break;
                    case "test_notice":
                        if (!_settings.Enabled || _settings.KeyHash is null) return Result(error: "disabled");
                        _broker.Publish(new LocalNoticePayload("PanelOS", "Este é um aviso de teste.", "info", null, null, 5, 300));
                        break;
                    default: return Result(error: "unknown_action");
                }
                return Result(key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { return Result(error: "settings_unavailable"); }
        }
    }

    internal async Task<LocalIntegrationAdminResult> AdminAsync(LocalIntegrationAdminRequest request, CancellationToken token)
    {
        if (request.Action != "test_permission") return Admin(request);
        int timeout;
        lock (_sync)
        {
            if (_permissions?.Connected != true) return Result(error: "tablet_disconnected");
            timeout = _settings.PermissionTimeoutSeconds;
        }
        await _permissions.RequestAsync(new("PanelOS", "Teste", "Este pedido não executa nada no PC.", "", 0), timeout, token);
        lock (_sync) return Result();
    }

    private LocalIntegrationAdminResult Result(string? key = null, string? error = null)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_authDay != today) { _authToday = 0; _authDay = today; }
        return new(
        _settings.Enabled, _settings.Port, _state, _settings.KeySuffix, _settings.Generation, _lastRequest,
        _authToday, _settings.PermissionTimeoutSeconds, key, error)
        {
            TabletConnected = _permissions?.Connected == true,
            LastPermissionDecision = _permissions?.LastDecision,
            LastPermissionUnixMs = _permissions?.LastRequestUnixMs,
            ProcessTerminationEnabled = _termination?.Enabled == true,
            ProcessTerminationReviewed = _termination?.Reviewed == true,
        };
    }

    private void StartListener()
    {
        _broker.SetEnabled(false);
        _aggregateTimer?.Dispose(); _aggregateTimer = null;
        if (!_settings.Enabled) { _state = "disabled"; return; }
        if (_settings.KeyHash is null) { _state = "key_required"; return; }
        if (_isElevated()) { _state = "requires_unelevated"; return; }
        var listener = new TcpListener(IPAddress.Loopback, _settings.Port);
        try { listener.Start(LocalIntegrationLimits.Connections); }
        catch (SocketException) { listener.Stop(); _state = "port_in_use"; return; }
        _listener = listener; _state = "listening";
        _broker.SetEnabled(true);
        _aggregateTimer = new Timer(_ => { lock (_sync) FlushRefusals(); }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _acceptors.RemoveAll(x => x.IsCompleted);
        _acceptors.Add(AcceptAsync(listener));
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_shutdown.Token);
                if (!_slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref _clientId);
                var task = HandleAsync(client);
                _clients[id] = task;
                _ = task.ContinueWith(_ => _clients.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(LocalIntegrationLimits.ReadSeconds));
            var stream = client.GetStream();
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(IPAddress.Loopback)) return;
                int port; lock (_sync) port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                var request = await LocalHttpRequest.ReadAsync(stream, port, timeout.Token);
                var status = 200; string reason = "accepted";
                lock (_sync)
                {
                    var now = _clock.Elapsed.TotalSeconds;
                    if (!_settings.Enabled || _state != "listening" || port != _settings.Port) { status = 503; reason = "disabled"; }
                    else if (_limits.IsBlocked(now)) { status = 429; reason = "temporarily_blocked"; _rateWindow++; }
                    else if (!LocalIntegrationKey.Matches(request.Key, _settings.KeyHash))
                    {
                        status = 401; reason = "unauthorized"; _limits.AuthenticationFailed(now); _authWindow++;
                        var day = DateOnly.FromDateTime(DateTime.Now);
                        if (_authDay != day) { _authToday = 0; _authDay = day; }
                        _authToday++;
                    }
                    else if (!_limits.Take(now, request.Path == "/v1/notices")) { status = 429; reason = "rate_limited"; _rateWindow++; }
                    else
                    {
                        try
                        {
                            switch (request.Path)
                            {
                                case "/v1/notices":
                                    var notice = LocalPayloadParser.Notice(request.Body);
                                    _broker.Publish(notice.Page is not null && !_pageExists(notice.Page) ? notice with { Page = null } : notice);
                                    break;
                                case "/v1/values":
                                    if (!_broker.Publish(LocalPayloadParser.Value(request.Body))) { status = 409; reason = "value_limit"; }
                                    break;
                                default: status = 404; reason = "unknown_path"; break;
                            }
                        }
                        catch (ArgumentException error) when (error.GetType() == typeof(ArgumentException)) { status = 400; reason = error.Message; }
                        catch (Exception error) when (error is JsonException or InvalidOperationException or DecoderFallbackException) { status = 400; reason = "invalid_json"; }
                        _lastRequest = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _log.LogInformation("Local request kind={Kind} status={Status} reason={Reason} bytes={Bytes} queued={Queued}",
                            request.Path == "/v1/notices" ? "notice" : "value", status, reason, request.Body.Length, _broker.Snapshot().Notices.Count);
                    }
                }
                await ReplyAsync(stream, status, reason, timeout.Token);
            }
            catch (LocalHttpError error)
            {
                _log.LogInformation("Local request status={Status} reason={Reason} bytes={Bytes}", error.Status, error.Message, 0);
                try
                {
                    await ReplyAsync(stream, error.Status, error.Message, timeout.Token);
                    // Finish the response before closing an unread request body (otherwise Windows sends a reset).
                    client.Client.Shutdown(SocketShutdown.Send);
                    var discard = new byte[1024];
                    var remaining = LocalIntegrationLimits.BodyBytes;
                    while (remaining > 0)
                    {
                        var count = await stream.ReadAsync(discard.AsMemory(0, Math.Min(remaining, discard.Length)), timeout.Token);
                        if (count == 0) break;
                        remaining -= count;
                    }
                }
                catch (Exception ignored) when (ignored is IOException or OperationCanceledException or SocketException) { }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
            finally { _slots.Release(); }
        }
    }

    private static async Task ReplyAsync(Stream stream, int status, string reason, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { status, reason });
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token); await stream.WriteAsync(body, token);
    }

    private void FlushRefusals()
    {
        if (_authWindow + _rateWindow > 0) _log.LogInformation("Local refusals auth={Auth} rate={Rate}", _authWindow, _rateWindow);
        _authWindow = _rateWindow = 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel(); lock (_sync) { _listener?.Stop(); _listener = null; _aggregateTimer?.Dispose(); FlushRefusals(); }
        await Task.WhenAll(_acceptors);
        await Task.WhenAll(_clients.Values);
        _shutdown.Dispose(); _slots.Dispose();
    }
}
