using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PanelOS.DisplayBroker.Protocol;
using PanelOS.DriverInterop;

namespace PanelHost.Broker;

internal sealed class PanelDisplayBrokerClient :
    IPanelDisplayDriverClient,
    IPanelDisplayDriverFrameClient,
    IPanelDisplayDriverCaptureMetricsClient,
    IPanelDisplayDriverCaptureConfigurationClient,
    IPanelDisplayBrokerConnection,
    IAsyncDisposable
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(45);
    internal static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeSpan _requestTimeout;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly TextWriter _log;
    private readonly Func<string> _executableLocator;

    private NamedPipeServerStream? _pipe;
    private BrokerControlMessageReader? _reader;
    private StreamWriter? _writer;
    private SharedFrameMailbox? _mailbox;
    private Process? _process;
    private long _nextRequestId;
    private int _connected;
    private bool _cpuTemperatureAvailable;
    private int _disposed;
    private int _disconnectPublished;

    internal PanelDisplayBrokerClient(
        TextWriter? log = null,
        Func<string>? executableLocator = null,
        TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _log = log ?? TextWriter.Null;
        _executableLocator = executableLocator ?? LocateBrokerExecutable;
    }

    public string? InterfacePath => IsConnected ? "broker://driver" : null;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;

    public event EventHandler<PanelDisplayBrokerDisconnectedEventArgs>? Disconnected;

    public async Task<PanelDisplayDriverStatus> GetStatusAsync(
        CancellationToken cancellationToken) =>
        (await SendAsync(
            PanelDisplayBrokerOperation.GetStatus,
            null,
            cancellationToken).ConfigureAwait(false)).Status ??
        throw ProtocolFailure("GET_STATUS response omitted status.");

    public async Task<PanelDisplayDriverStatus> GetStatusIfConnectedAsync(
        CancellationToken cancellationToken) =>
        (await SendAsync(PanelDisplayBrokerOperation.GetStatus, null,
            cancellationToken, startIfNeeded: false).ConfigureAwait(false)).Status ??
        throw ProtocolFailure("GET_STATUS response omitted status.");

    public async Task<PanelDisplayCommandResponse> ConnectAsync(
        CancellationToken cancellationToken) =>
        (await SendAsync(
            PanelDisplayBrokerOperation.Connect,
            null,
            cancellationToken).ConfigureAwait(false)).Command ??
        throw ProtocolFailure("CONNECT response omitted command result.");

    public async Task<PanelDisplayCommandResponse> DisconnectAsync(
        CancellationToken cancellationToken) =>
        (await SendAsync(
            PanelDisplayBrokerOperation.Disconnect,
            null,
            cancellationToken, startIfNeeded: false).ConfigureAwait(false)).Command ??
        throw ProtocolFailure("DISCONNECT response omitted command result.");

    public Task<PanelDisplayFrameInfo> GetFrameInfoAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureFrameTransportConnected();
        return Task.FromResult(_mailbox!.GetFrameInfo(lastSequence));
    }

    public Task<PanelDisplayDriverFrame> ReadFrameAsync(
        PanelDisplayFrameInfo requestedFrame,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureFrameTransportConnected();
        return Task.FromResult(_mailbox!.ReadFrame(requestedFrame));
    }

    public async Task<PanelDisplayCaptureMetrics> GetCaptureMetricsAsync(
        CancellationToken cancellationToken) =>
        (await SendAsync(
            PanelDisplayBrokerOperation.GetCaptureMetrics,
            null,
            cancellationToken).ConfigureAwait(false)).CaptureMetrics ??
        throw ProtocolFailure("GET_CAPTURE_METRICS response omitted metrics.");

    public async Task<uint> SetCaptureTargetFpsAsync(
        uint targetFramesPerSecond,
        CancellationToken cancellationToken) =>
        (await SendAsync(
            PanelDisplayBrokerOperation.SetCaptureTarget,
            targetFramesPerSecond,
            cancellationToken).ConfigureAwait(false)).ConfiguredFramesPerSecond ??
        throw ProtocolFailure("SET_CAPTURE_TARGET response omitted target.");

    // Telemetry may use an existing lease, but must never start a broker or prompt for UAC.
    internal async Task<double?> ReadCpuTemperatureIfConnectedAsync(CancellationToken cancellationToken) =>
        !_cpuTemperatureAvailable || !IsConnected ? null : (await SendAsync(PanelDisplayBrokerOperation.GetCpuTemperature, null,
            cancellationToken, startIfNeeded: false).ConfigureAwait(false)).CpuTemperatureC;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PublishDisconnect(PanelDisplayBrokerDisconnectReason.HostShutdown, null);
        await DisposeConnectionAsync().ConfigureAwait(false);
        _startLock.Dispose();
        _requestLock.Dispose();
    }

    private async Task<PanelDisplayBrokerResponse> SendAsync(
        PanelDisplayBrokerOperation operation,
        uint? targetFramesPerSecond,
        CancellationToken cancellationToken,
        bool startIfNeeded = true)
    {
        if (startIfNeeded) await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        else EnsureControlTransportConnected();
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        // PAN-3: a broker that is alive but silent (a driver call that never returns) must not
        // hold the request lock forever. The caller's token still wins over the deadline.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            // Recheck after waiting; capture references before concurrent cleanup clears the fields.
            EnsureControlTransportConnected();
            var writer = _writer ?? throw DisconnectedControlPipe();
            var reader = _reader ?? throw DisconnectedControlPipe();
            var requestId = Interlocked.Increment(ref _nextRequestId);
            var request = new PanelDisplayBrokerRequest(
                requestId,
                PanelDisplayBrokerProtocol.Version,
                operation,
                targetFramesPerSecond);
            await writer.WriteLineAsync(
                PanelDisplayBrokerProtocol.Serialize(request).AsMemory(),
                deadline.Token).ConfigureAwait(false);
            var line = await reader.ReadAsync(deadline.Token).ConfigureAwait(false);
            if (line is null)
            {
                throw new EndOfStreamException("Display broker closed its control pipe.");
            }

            var response = PanelDisplayBrokerProtocol.Deserialize<
                PanelDisplayBrokerResponse>(line);
            if (response is null ||
                response.ProtocolVersion != PanelDisplayBrokerProtocol.Version ||
                response.RequestId != requestId)
            {
                throw ProtocolFailure("Display broker returned an invalid response.");
            }
            if (!response.Succeeded)
            {
                throw ToDriverException(operation, response.Error);
            }
            return response;
        }
        catch (OperationCanceledException exception) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            _log.WriteLine(
                $"[DisplayBroker] No answer to {operation} within {_requestTimeout.TotalSeconds:0.#}s; " +
                "closing the control pipe (the broker ends when it sees the lease close).");
            await HandleConnectionFailureAsync(
                PanelDisplayBrokerDisconnectReason.Unresponsive,
                exception).ConfigureAwait(false);
            throw new PanelDisplayBrokerDisconnectedException(
                PanelDisplayBrokerDisconnectReason.Unresponsive,
                "The privileged display broker stopped answering.",
                exception);
        }
        catch (OperationCanceledException exception)
        {
            await HandleConnectionFailureAsync(
                PanelDisplayBrokerDisconnectReason.PipeClosed,
                exception).ConfigureAwait(false);
            throw;
        }
        catch (PanelDisplayDriverException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or InvalidDataException or ObjectDisposedException or
            System.Text.Json.JsonException)
        {
            await HandleConnectionFailureAsync(
                exception is InvalidDataException
                    ? PanelDisplayBrokerDisconnectReason.ProtocolError
                    : PanelDisplayBrokerDisconnectReason.PipeClosed,
                exception).ConfigureAwait(false);
            throw new PanelDisplayBrokerDisconnectedException(
                exception is InvalidDataException
                    ? PanelDisplayBrokerDisconnectReason.ProtocolError
                    : PanelDisplayBrokerDisconnectReason.PipeClosed,
                "The privileged display broker disconnected.",
                exception);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsConnected)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await DisposeConnectionAsync().ConfigureAwait(false);
            Volatile.Write(ref _disconnectPublished, 0);
            var pipeName = $"PanelOS.DisplayBroker.{Guid.NewGuid():N}";
            var frameMappingName = $"PanelOS.DisplayFrames.{Guid.NewGuid():N}";
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var authenticator = new PanelDisplayBrokerAuthenticator(nonce);
            _mailbox = SharedFrameMailbox.CreateNew(frameMappingName);
            _pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough |
                    PipeOptions.CurrentUserOnly,
                4096,
                64 * 1024);

            using var parent = Process.GetCurrentProcess();
            var executable = _executableLocator();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(' ',
                    "--pipe", pipeName,
                    "--frame-map", frameMappingName,
                    "--nonce", nonce,
                    "--parent-pid", Environment.ProcessId,
                    "--parent-start-utc-ticks",
                    parent.StartTime.ToUniversalTime().Ticks),
            };
            _process = Process.Start(startInfo) ??
                throw new InvalidOperationException(
                    "Windows did not start PanelDisplayBroker.");
            _process.EnableRaisingEvents = true;
            _process.Exited += OnBrokerExited;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(ConnectionTimeout);
            await _pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            ValidateConnectedProcess(_pipe, _process.Id);
            _reader = new BrokerControlMessageReader(_pipe);
            _writer = new StreamWriter(
                _pipe,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            var helloLine = await _reader.ReadAsync(timeout.Token).ConfigureAwait(false);
            var hello = helloLine is null
                ? null
                : PanelDisplayBrokerProtocol.Deserialize<
                    PanelDisplayBrokerHello>(helloLine);
            var authenticated = hello is
                {
                    ProtocolVersion: PanelDisplayBrokerProtocol.Version,
                    ParentProcessId: var helloParent,
                    ParentStartTimeUtcTicks: var helloStart,
                } &&
                helloParent == Environment.ProcessId &&
                helloStart == parent.StartTime.ToUniversalTime().Ticks &&
                authenticator.TryAuthenticate(hello.Nonce);
            await _writer.WriteLineAsync(PanelDisplayBrokerProtocol.Serialize(
                new PanelDisplayBrokerHelloResponse(
                    PanelDisplayBrokerProtocol.Version,
                    authenticated,
                    authenticated ? null : "authentication_failed")));
            if (!authenticated)
            {
                PublishDisconnect(
                    PanelDisplayBrokerDisconnectReason.AuthenticationFailed,
                    "Broker authentication failed.");
                throw new PanelDisplayBrokerDisconnectedException(
                    PanelDisplayBrokerDisconnectReason.AuthenticationFailed,
                    "PanelDisplayBroker authentication failed.");
            }

            _cpuTemperatureAvailable = hello?.CpuTemperatureAvailable == true;
            Volatile.Write(ref _connected, 1);
            _log.WriteLine(
                $"[DisplayBroker] Connected protocol=v{PanelDisplayBrokerProtocol.Version} " +
                $"pid={_process.Id}; nonce=<redacted>.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            throw new PanelDisplayBrokerDisconnectedException(
                PanelDisplayBrokerDisconnectReason.ElevationDeclined,
                "Display broker elevation was cancelled.",
                exception);
        }
        catch (Exception exception) when (IsStartupConnectionFailure(exception))
        {
            // The broker connected and then dropped the lease before the handshake finished (for
            // example it exited within a second). Report it as a broker disconnect, not as an
            // unexpected driver failure, and keep the broker's exit code: nothing else records it.
            var exitCode = await TryReadExitCodeAsync(_process).ConfigureAwait(false);
            await DisposeConnectionAsync().ConfigureAwait(false);
            var failure = TranslateStartupFailure(exception, exitCode);
            _log.WriteLine(
                $"[DisplayBroker] The broker closed the connection while starting " +
                $"({exception.GetType().Name}: {exception.Message}); " +
                (exitCode is { } code
                    ? $"process exit code {code} ({PanelDisplayBrokerExitCodes.Describe(code)})."
                    : "exit code unavailable.") +
                $" Broker log: {PanelDisplayBrokerExitCodes.LogFilePath}");
            throw failure;
        }
        catch
        {
            await DisposeConnectionAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// A transport failure while the broker is still being started. A disconnect that was already
    /// reported on purpose (authentication, elevation) keeps its own reason.
    /// </summary>
    internal static bool IsStartupConnectionFailure(Exception exception) =>
        exception is not PanelDisplayBrokerDisconnectedException and
            (IOException or ObjectDisposedException or InvalidDataException or
                System.Text.Json.JsonException);

    internal static PanelDisplayBrokerDisconnectedException TranslateStartupFailure(
        Exception exception,
        int? brokerExitCode) => new(
            exception is InvalidDataException or System.Text.Json.JsonException
                ? PanelDisplayBrokerDisconnectReason.ProtocolError
                : brokerExitCode is null
                    ? PanelDisplayBrokerDisconnectReason.PipeClosed
                    : PanelDisplayBrokerDisconnectReason.BrokerExited,
            "The privileged display broker disconnected while starting.",
            exception);

    private static async Task<int?> TryReadExitCodeAsync(Process? process)
    {
        if (process is null)
        {
            return null;
        }

        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is
            OperationCanceledException or InvalidOperationException or
            Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private void EnsureFrameTransportConnected()
    {
        if (!IsConnected || _mailbox is null)
        {
            throw new PanelDisplayBrokerDisconnectedException(
                PanelDisplayBrokerDisconnectReason.PipeClosed,
                "The privileged display broker is not connected.");
        }
    }

    private static PanelDisplayBrokerDisconnectedException DisconnectedControlPipe() => new(
        PanelDisplayBrokerDisconnectReason.PipeClosed, "The display broker is not connected.");

    private void EnsureControlTransportConnected()
    {
        if (!IsConnected) throw DisconnectedControlPipe();
    }

    private async Task HandleConnectionFailureAsync(
        PanelDisplayBrokerDisconnectReason reason,
        Exception exception)
    {
        PublishDisconnect(reason, exception.Message);
        await DisposeConnectionAsync().ConfigureAwait(false);
    }

    private void OnBrokerExited(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Process { HasExited: true } exited)
            {
                _log.WriteLine(
                    $"[DisplayBroker] PanelDisplayBroker exited with code {exited.ExitCode}.");
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The handle may already be released by the cleanup that follows.
        }

        PublishDisconnect(
            PanelDisplayBrokerDisconnectReason.BrokerExited,
            "PanelDisplayBroker process exited.");
        _ = Task.Run(DisposeConnectionAsync);
    }

    private void PublishDisconnect(
        PanelDisplayBrokerDisconnectReason reason,
        string? detail)
    {
        Volatile.Write(ref _connected, 0);
        if (Interlocked.Exchange(ref _disconnectPublished, 1) != 0 ||
            reason == PanelDisplayBrokerDisconnectReason.HostShutdown)
        {
            return;
        }
        Disconnected?.Invoke(
            this,
            new PanelDisplayBrokerDisconnectedEventArgs(reason, detail));
    }

    private async Task DisposeConnectionAsync()
    {
        Volatile.Write(ref _connected, 0);
        var process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            process.Exited -= OnBrokerExited;
        }

        var writer = Interlocked.Exchange(ref _writer, null);
        if (writer is not null)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // A lost lease may fail the writer's final flush; still release the pipe/mailbox/process.
                _log.WriteLine("[DisplayBroker] Control writer already closed during cleanup.");
            }
        }
        _reader = null;
        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is not null)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
        Interlocked.Exchange(ref _mailbox, null)?.Dispose();

        if (process is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is
                    InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // The broker runs at High IL and this host at Medium, so Windows normally refuses.
                    // The closed pipe and the broker's own watchdog are what end it.
                    _log.WriteLine(
                        "[DisplayBroker] The broker did not exit and could not be terminated from " +
                        $"this host ({exception.GetType().Name}); it ends on the closed lease or its own watchdog.");
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// The peer that connected must be the broker process this client started.
    /// </summary>
    /// <remarks>
    /// Reading the peer's user name instead - <c>GetImpersonationUserName</c> - is not
    /// available at this point in the handshake. That call impersonates the client, and
    /// Windows refuses impersonation with ERROR_CANNOT_IMPERSONATE until the server has
    /// read from the pipe; the first thing to read is the hello, which is exactly what
    /// must not be trusted before the peer is known. The client's process id is
    /// answerable straight after the connection and answers the narrower question
    /// anyway: not "some process of this user" but "the process we launched".
    /// </remarks>
    internal static void ValidateConnectedProcess(
        NamedPipeServerStream pipe,
        int expectedProcessId)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The display broker pipe did not report its client process.");
        }

        if (clientProcessId != (uint)expectedProcessId)
        {
            throw new UnauthorizedAccessException(
                $"Display broker pipe was connected by process {clientProcessId}, " +
                $"not the broker started for it ({expectedProcessId}).");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipeHandle,
        out uint clientProcessId);

    private static PanelDisplayDriverException ToDriverException(
        PanelDisplayBrokerOperation operation,
        PanelDisplayBrokerError? error)
    {
        var kind = error?.Code switch
        {
            "notfound" => PanelDisplayDriverFailureKind.NotFound,
            "accessdenied" => PanelDisplayDriverFailureKind.AccessDenied,
            _ => PanelDisplayDriverFailureKind.IoError,
        };
        return new PanelDisplayDriverException(
            kind,
            operation.ToString(),
            error?.Message ?? "Display broker operation failed.",
            error?.Win32ErrorCode,
            "broker://driver");
    }

    private static InvalidDataException ProtocolFailure(string message) => new(message);

    private static string LocateBrokerExecutable()
    {
        var besideHost = Path.Combine(AppContext.BaseDirectory, "PanelDisplayBroker.exe");
        if (File.Exists(besideHost))
        {
            return besideHost;
        }

        var configuration = AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        var repositoryBuild = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "PanelDisplayBroker", "bin", configuration,
            "net8.0-windows10.0.19041.0", "PanelDisplayBroker.exe"));
        return File.Exists(repositoryBuild)
            ? repositoryBuild
            : throw new FileNotFoundException(
                "PanelDisplayBroker.exe was not found beside PanelHost.",
                besideHost);
    }
}
