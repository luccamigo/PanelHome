using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using PanelOS.Windows.Management;
using PanelOS.Windows.Protocol;
using PanelHost.Services;

namespace PanelHost.Management;

internal sealed class PanelHostManagementServer
{
    private readonly PanelHostRuntimeState _runtimeState;
    private readonly IDesktopService? _desktopService;
    private readonly string _pipeName;
    private readonly LocalIntegrationService? _integrations;
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private long _nextClientId;

    internal PanelHostManagementServer(
        PanelHostRuntimeState runtimeState,
        string pipeName = PanelHostManagementProtocol.PipeName)
        : this(runtimeState, null, pipeName, initialize: true)
    {
    }

    internal PanelHostManagementServer(
        PanelHostRuntimeState runtimeState,
        IDesktopService desktopService,
        string pipeName = PanelHostManagementProtocol.PipeName,
        LocalIntegrationService? integrations = null)
        : this(runtimeState, desktopService, pipeName, initialize: true)
    {
        _integrations = integrations;
    }

    private PanelHostManagementServer(
        PanelHostRuntimeState runtimeState,
        IDesktopService? desktopService,
        string pipeName,
        bool initialize)
    {
        _ = initialize;
        _runtimeState = runtimeState ??
            throw new ArgumentNullException(nameof(runtimeState));
        _desktopService = desktopService;
        _pipeName = !string.IsNullOrWhiteSpace(pipeName)
            ? pipeName
            : throw new ArgumentException("Pipe name is required.", nameof(pipeName));
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = CreatePipe();
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var clientId = Interlocked.Increment(ref _nextClientId);
                var task = HandleClientAsync(pipe, cancellationToken);
                _clients[clientId] = task;
                _ = task.ContinueWith(
                    completed => _clients.TryRemove(clientId, out var removed),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // CurrentUserOnly uses the token owner (Administrators when elevated),
        // so use the account SID explicitly for both owner and access rules.
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ??
            throw new InvalidOperationException("Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(
            user,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            inBufferSize: 4096,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using var pipeLifetime = pipe;
        var reader = new BoundedUtf8LineReader(pipe);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            PanelHostManagementRequest? request;
            try
            {
                request = PanelHostManagementProtocol.DeserializeRequest(line);
            }
            catch (System.Text.Json.JsonException)
            {
                await WriteErrorAsync(writer, "invalid_json", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (request is null ||
                request.ProtocolVersion != PanelHostManagementProtocol.Version)
            {
                await WriteErrorAsync(writer, "unsupported_protocol", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            switch (request.Type)
            {
                case "local_integrations" when _integrations is not null:
                    LocalIntegrationAdminRequest? admin;
                    try { admin = System.Text.Json.JsonSerializer.Deserialize<LocalIntegrationAdminRequest>(line, PanelHostManagementProtocol.JsonOptions); }
                    catch (System.Text.Json.JsonException) { await WriteErrorAsync(writer, "invalid_request", cancellationToken); break; }
                    if (admin is null) { await WriteErrorAsync(writer, "invalid_request", cancellationToken); break; }
                    var result = await _integrations.AdminAsync(admin, cancellationToken);
                    await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(result,
                        PanelHostManagementProtocol.JsonOptions).AsMemory(), cancellationToken);
                    break;
                case PanelHostManagementProtocol.GetSnapshot:
                    await WriteSnapshotAsync(
                        writer,
                        _runtimeState.Snapshot,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case PanelHostManagementProtocol.Subscribe:
                    await RunSubscriptionAsync(writer, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PanelHostManagementProtocol.DesktopStart:
                    await RunDesktopCommandAsync(
                        writer,
                        start: true,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case PanelHostManagementProtocol.DesktopStop:
                    await RunDesktopCommandAsync(
                        writer,
                        start: false,
                        cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await WriteErrorAsync(writer, "unsupported_request", cancellationToken)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is
            IOException or OperationCanceledException)
        {
        }
    }

    private async Task RunDesktopCommandAsync(
        StreamWriter writer,
        bool start,
        CancellationToken cancellationToken)
    {
        if (_desktopService is null)
        {
            await WriteErrorAsync(
                writer,
                "desktop_control_unavailable",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        PanelHostManagementDesktopResult result;
        if (start)
        {
            var started = await _desktopService.StartAsync(
                new DesktopRequest(),
                cancellationToken).ConfigureAwait(false);
            result = new PanelHostManagementDesktopResult(
                started.Succeeded,
                started.AlreadyRunning,
                started.State.ToString(),
                started.ErrorCode);
        }
        else
        {
            var stopped = await _desktopService.StopAsync(cancellationToken)
                .ConfigureAwait(false);
            result = new PanelHostManagementDesktopResult(
                stopped.Succeeded,
                stopped.AlreadyStopped,
                stopped.State.ToString(),
                stopped.ErrorCode);
        }

        await writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeDesktopResult(result).AsMemory(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSubscriptionAsync(
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var updates = Channel.CreateBounded<PanelHostSnapshot>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        void OnSnapshotChanged(object? sender, PanelHostSnapshotChangedEventArgs eventArgs) =>
            updates.Writer.TryWrite(eventArgs.Snapshot);

        _runtimeState.SnapshotChanged += OnSnapshotChanged;
        updates.Writer.TryWrite(_runtimeState.Snapshot);
        try
        {
            await foreach (var snapshot in updates.Reader.ReadAllAsync(cancellationToken))
            {
                await WriteSnapshotAsync(writer, snapshot, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _runtimeState.SnapshotChanged -= OnSnapshotChanged;
            updates.Writer.TryComplete();
        }
    }

    private static Task WriteSnapshotAsync(
        StreamWriter writer,
        PanelHostSnapshot snapshot,
        CancellationToken cancellationToken) =>
        writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeSnapshot(snapshot).AsMemory(),
            cancellationToken);

    private static Task WriteErrorAsync(
        StreamWriter writer,
        string error,
        CancellationToken cancellationToken) =>
        writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeError(error).AsMemory(),
            cancellationToken);
}
