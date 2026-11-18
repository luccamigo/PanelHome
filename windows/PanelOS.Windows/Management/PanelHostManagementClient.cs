using System.IO.Pipes;
using System.Text;
using PanelOS.Windows.Protocol;

namespace PanelOS.Windows.Management;

public enum PanelHostManagementConnectionState
{
    Disconnected,
    Connecting,
    Connected,
}

public sealed class PanelHostManagementSnapshotChangedEventArgs(
    PanelHostSnapshot? snapshot) : EventArgs
{
    public PanelHostSnapshot? Snapshot { get; } = snapshot;
}

public sealed class PanelHostManagementConnectionChangedEventArgs(
    PanelHostManagementConnectionState state) : EventArgs
{
    public PanelHostManagementConnectionState State { get; } = state;
}

public interface IPanelHostManagementClient : IDisposable
{
    PanelHostManagementConnectionState ConnectionState { get; }

    PanelHostSnapshot? Snapshot { get; }

    event EventHandler<PanelHostManagementSnapshotChangedEventArgs>? SnapshotChanged;

    event EventHandler<PanelHostManagementConnectionChangedEventArgs>? ConnectionChanged;

    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Maintains a read-only subscription to PanelHost. A missing pipe is a normal disconnected
/// state; the client retries with bounded backoff and clears stale state on every disconnect.
/// </summary>
public interface IPanelHostManagementDesktopCommands
{
    Task<PanelHostManagementDesktopResult> StartDesktopAsync(
        CancellationToken cancellationToken);

    Task<PanelHostManagementDesktopResult> StopDesktopAsync(
        CancellationToken cancellationToken);
}

public sealed class PanelHostManagementClient :
    IPanelHostManagementClient,
    IPanelHostManagementDesktopCommands
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly TimeSpan _initialReconnectDelay;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private PanelHostSnapshot? _snapshot;
    private int _connectionState;
    private int _runStarted;
    private int _disposed;

    public PanelHostManagementClient(
        string pipeName = PanelHostManagementProtocol.PipeName,
        TimeSpan? initialReconnectDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _initialReconnectDelay = initialReconnectDelay ?? TimeSpan.FromMilliseconds(250);
        ArgumentOutOfRangeException.ThrowIfLessThan(_initialReconnectDelay, TimeSpan.Zero);
    }

    public PanelHostManagementConnectionState ConnectionState =>
        (PanelHostManagementConnectionState)Volatile.Read(ref _connectionState);

    public PanelHostSnapshot? Snapshot => Volatile.Read(ref _snapshot);

    public event EventHandler<PanelHostManagementSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<PanelHostManagementConnectionChangedEventArgs>? ConnectionChanged;

    public Task<PanelHostManagementDesktopResult> StartDesktopAsync(
        CancellationToken cancellationToken) =>
        RunDesktopCommandAsync(
            PanelHostManagementProtocol.DesktopStart,
            cancellationToken);

    public Task<PanelHostManagementDesktopResult> StopDesktopAsync(
        CancellationToken cancellationToken) =>
        RunDesktopCommandAsync(
            PanelHostManagementProtocol.DesktopStop,
            cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The management client is already running.");
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var reconnectDelay = _initialReconnectDelay;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                PublishConnectionState(PanelHostManagementConnectionState.Connecting);
                try
                {
                    await RunConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    reconnectDelay = _initialReconnectDelay;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    // The per-attempt connection timeout elapsed. Treat it like an
                    // unavailable host and keep the reconnect loop alive.
                }
                catch (Exception exception) when (exception is
                    IOException or TimeoutException or UnauthorizedAccessException)
                {
                }
                finally
                {
                    PublishDisconnected();
                }

                if (reconnectDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(reconnectDelay, lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                        break;
                    }
                }

                reconnectDelay = TimeSpan.FromMilliseconds(Math.Min(
                    MaximumReconnectDelay.TotalMilliseconds,
                    Math.Max(1, reconnectDelay.TotalMilliseconds * 2)));
            }
        }
        finally
        {
            PublishDisconnected();
            Volatile.Write(ref _runStarted, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Cancel();
        _disposeCancellation.Dispose();
    }

    private async Task RunConnectionAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken))
        {
            connectCancellation.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
        }

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
        await writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeRequest(
                PanelHostManagementProtocol.Subscribe)).ConfigureAwait(false);
        PublishConnectionState(PanelHostManagementConnectionState.Connected);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            PanelHostManagementResponse? response;
            try
            {
                response = PanelHostManagementProtocol.DeserializeResponse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (response is
                {
                    Type: PanelHostManagementProtocol.Snapshot,
                    ProtocolVersion: PanelHostManagementProtocol.Version,
                    Snapshot: { } snapshot,
                })
            {
                Volatile.Write(ref _snapshot, snapshot);
                SnapshotChanged?.Invoke(
                    this,
                    new PanelHostManagementSnapshotChangedEventArgs(snapshot));
            }
        }
    }

    private async Task<PanelHostManagementDesktopResult> RunDesktopCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using (var connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken))
        {
            connectCancellation.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(connectCancellation.Token).ConfigureAwait(false);
        }

        var reader = new BoundedUtf8LineReader(pipe);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        await writer.WriteLineAsync(
            PanelHostManagementProtocol.SerializeRequest(command).AsMemory(),
            cancellationToken).ConfigureAwait(false);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var response = line is null
            ? null
            : PanelHostManagementProtocol.DeserializeResponse(line);
        if (response is
            {
                Type: PanelHostManagementProtocol.DesktopResult,
                ProtocolVersion: PanelHostManagementProtocol.Version,
                Desktop: { } result,
            })
        {
            return result;
        }

        throw new IOException(
            response?.Error is { Length: > 0 } error
                ? $"PanelHost rejected Desktop command: {error}."
                : "PanelHost returned an invalid Desktop command response.");
    }

    private void PublishDisconnected()
    {
        var previous = Interlocked.Exchange(
            ref _connectionState,
            (int)PanelHostManagementConnectionState.Disconnected);
        var stale = Interlocked.Exchange(ref _snapshot, null);
        if (stale is not null)
        {
            SnapshotChanged?.Invoke(
                this,
                new PanelHostManagementSnapshotChangedEventArgs(null));
        }
        if (previous != (int)PanelHostManagementConnectionState.Disconnected)
        {
            ConnectionChanged?.Invoke(
                this,
                new PanelHostManagementConnectionChangedEventArgs(
                    PanelHostManagementConnectionState.Disconnected));
        }
    }

    private void PublishConnectionState(PanelHostManagementConnectionState state)
    {
        var previous = Interlocked.Exchange(ref _connectionState, (int)state);
        if (previous != (int)state)
        {
            ConnectionChanged?.Invoke(
                this,
                new PanelHostManagementConnectionChangedEventArgs(state));
        }
    }
}
