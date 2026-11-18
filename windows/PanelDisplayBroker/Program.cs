using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using PanelDisplayBroker;
using PanelOS.DisplayBroker.Protocol;
using PanelOS.DriverInterop;


// Exit codes are shared with the host (PanelDisplayBrokerExitCodes): every failure has its own code
// and is written to the broker log, instead of ending with 0 as the swallowed exceptions used to.
// Longer than the host's 15 s request deadline, so a slow answer is the host's call to give up on.
var requestLimit = TimeSpan.FromSeconds(20);
// Cancel, bounded waits (2 s each) and the 3 s DISCONNECT must fit; past this the process exits.
var shutdownLimit = TimeSpan.FromSeconds(10);
var log = BrokerLog.Null;
var stage = BrokerStage.Startup;
var watchdog = new BrokerWatchdog((exitCode, reason) =>
{
    Console.Error.WriteLine($"[PanelDisplayBroker] Watchdog: {reason}; exiting.");
    log.Info("watchdog", $"{reason}; exit code {exitCode}");
    Environment.Exit(exitCode);
});

BrokerArguments options;
try
{
    options = BrokerArguments.Parse(args);
}
catch (ArgumentException exception)
{
    // Not elevated yet (or not known to be): no file log, stderr only.
    Console.Error.WriteLine($"[PanelDisplayBroker] {exception.Message}");
    Environment.ExitCode = PanelDisplayBrokerExitCodes.InvalidArguments;
    return;
}

using var identity = WindowsIdentity.GetCurrent();
var principal = new WindowsPrincipal(identity);
if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
{
    // The log directory belongs to the elevated broker; a normal process must not create it.
    Console.Error.WriteLine(
        "[PanelDisplayBroker] Elevation is required for driver access.");
    Environment.ExitCode = PanelDisplayBrokerExitCodes.NotElevated;
    return;
}

log = BrokerLog.Open(refused: message => Console.Error.WriteLine(message));
log.Redact(options.Nonce, options.PipeName, options.FrameMappingName);
log.Info("start", $"elevated parentPid={options.ParentProcessId} logToFile={log.IsWritingToFile}");

Process parent;
try
{
    parent = Process.GetProcessById(options.ParentProcessId);
    if (parent.StartTime.ToUniversalTime().Ticks != options.ParentStartTimeUtcTicks)
    {
        parent.Dispose();
        log.Info("startup", "the parent process start time differs; exit code " +
            PanelDisplayBrokerExitCodes.ParentUnavailable);
        Environment.ExitCode = PanelDisplayBrokerExitCodes.ParentUnavailable;
        return;
    }
}
catch (Exception exception) when (exception is
    ArgumentException or InvalidOperationException or
    System.ComponentModel.Win32Exception)
{
    log.Error("startup", exception, PanelDisplayBrokerExitCodes.ParentUnavailable,
        "the parent process was not found");
    Environment.ExitCode = PanelDisplayBrokerExitCodes.ParentUnavailable;
    return;
}

SharedFrameMailbox mailbox;
try
{
    mailbox = SharedFrameMailbox.OpenExisting(options.FrameMappingName);
}
catch (Exception exception) when (exception is
    IOException or UnauthorizedAccessException or ArgumentException)
{
    log.Error("startup", exception, PanelDisplayBrokerExitCodes.FrameMailboxUnavailable,
        "the frame mailbox could not be opened");
    Environment.ExitCode = PanelDisplayBrokerExitCodes.FrameMailboxUnavailable;
    parent.Dispose();
    return;
}

using (log)
using (parent)
using (var lifetime = new CancellationTokenSource())
using (mailbox)
await using (var pipe = new NamedPipeClientStream(
    ".",
    options.PipeName,
    PipeDirection.InOut,
    // Not CurrentUserOnly: .NET compares the pipe owner with WindowsIdentity.Owner, which is
    // BUILTIN\Administrators for this elevated token, and the host's pipe belongs to the user.
    // BrokerPipeOwner.Verify below keeps the same-user guarantee with the right comparison.
    PipeOptions.Asynchronous))
using (var driver = new PanelDisplayDriverClient(keepHandleOpen: true))
{
    try
    {
        stage = BrokerStage.Connect;
        await pipe.ConnectAsync(TimeSpan.FromSeconds(15), lifetime.Token);
        BrokerPipeOwner.Verify(pipe);
        log.Info("connect", "connected to the host pipe; owner accepted");
        stage = BrokerStage.Handshake;
        var reader = new BrokerControlMessageReader(pipe);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            4096,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };

        await writer.WriteLineAsync(PanelDisplayBrokerProtocol.Serialize(
            new PanelDisplayBrokerHello(
                PanelDisplayBrokerProtocol.Version,
                options.Nonce,
                options.ParentProcessId,
                options.ParentStartTimeUtcTicks, CpuTemperatureAvailable: true)));
        var authLine = await reader.ReadAsync(lifetime.Token);
        var auth = authLine is null
            ? null
            : PanelDisplayBrokerProtocol.Deserialize<
                PanelDisplayBrokerHelloResponse>(authLine);
        if (auth is not
            {
                ProtocolVersion: PanelDisplayBrokerProtocol.Version,
                Authenticated: true,
            })
        {
            log.Info("handshake", authLine is null
                ? "the host closed the pipe before answering the hello; exit code 4"
                : "the host did not authenticate the hello; exit code 4");
            Environment.ExitCode = PanelDisplayBrokerExitCodes.AuthenticationFailed;
            return;
        }

        stage = BrokerStage.Serve;
        log.Info("serve", "handshake accepted");
        var captureState = new BrokerCaptureState(mailbox);
        var parentWatch = parent.WaitForExitAsync(lifetime.Token);
        var framePump = PumpFramesAsync(driver, captureState, lifetime.Token);
        var requests = RunRequestsAsync(
            driver,
            captureState,
            reader,
            writer,
            watchdog,
            requestLimit,
            lifetime.Token);
        _ = await Task.WhenAny(parentWatch, requests, framePump);
        watchdog.ArmShutdown(shutdownLimit);
        lifetime.Cancel();
        // Bounded: a task stuck in a native wait must not keep the cleanup below from running.
        await BoundedAsync(IgnoreCancellationAsync(framePump));
        await BoundedAsync(IgnoreCancellationAsync(requests));
        log.Info("serve", "ended: the host closed the pipe, the parent exited, or the frame pump stopped");
    }
    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
    {
        log.Info(stage.ToString().ToLowerInvariant(), "cancelled");
    }
    catch (Exception exception) when (exception is
        IOException or UnauthorizedAccessException or TimeoutException or
        InvalidDataException or System.Text.Json.JsonException)
    {
        // The authenticated pipe is the broker lease: an I/O error after the handshake is the host
        // closing it (orderly end, exit code 0). Anything earlier is a failure with its own code.
        var (code, reason) = BrokerExit.Classify(exception, stage);
        if (code == PanelDisplayBrokerExitCodes.Ok)
        {
            log.Info("serve", $"{reason}: {exception.GetType().Name}");
        }
        else
        {
            log.Error(stage.ToString().ToLowerInvariant(), exception, code, reason);
        }

        Environment.ExitCode = code;
    }
    catch (Exception exception)
    {
        // Recorded and given its own code instead of an unhandled crash with no trace.
        log.Error(stage.ToString().ToLowerInvariant(), exception,
            PanelDisplayBrokerExitCodes.Unexpected, "unexpected exception");
        Environment.ExitCode = PanelDisplayBrokerExitCodes.Unexpected;
    }
    finally
    {
        watchdog.ArmShutdown(shutdownLimit);
        lifetime.Cancel();
        mailbox.Reset();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var status = await driver.GetStatusAsync(cleanup.Token);
            if (status.MonitorState == PanelDisplayMonitorState.Connected)
            {
                _ = await driver.DisconnectAsync(cleanup.Token);
            }
        }
        catch (Exception exception) when (exception is
            PanelDisplayDriverException or OperationCanceledException or
            ObjectDisposedException)
        {
        }

        log.Info("exit", $"exit code {Environment.ExitCode}");
    }
}

static async Task RunRequestsAsync(
    PanelDisplayDriverClient driver,
    BrokerCaptureState captureState,
    BrokerControlMessageReader reader,
    StreamWriter writer,
    BrokerWatchdog watchdog,
    TimeSpan requestLimit,
    CancellationToken cancellationToken)
{
    using var cpuTemperature = new BrokerCpuTemperature();
    while (!cancellationToken.IsCancellationRequested)
    {
        var line = await reader.ReadAsync(cancellationToken);
        if (line is null)
        {
            return;
        }

        PanelDisplayBrokerRequest? request;
        try
        {
            request = PanelDisplayBrokerProtocol.Deserialize<
                PanelDisplayBrokerRequest>(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        if (request is null ||
            request.ProtocolVersion != PanelDisplayBrokerProtocol.Version ||
            request.RequestId <= 0 ||
            !Enum.IsDefined(request.Operation))
        {
            return;
        }

        PanelDisplayBrokerResponse response;
        try
        {
            using (watchdog.Request(requestLimit))
            {
                response = await ExecuteAsync(
                    driver,
                    captureState,
                    cpuTemperature,
                    request,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PanelDisplayDriverException exception)
        {
            response = Failure(
                request,
                exception.Kind.ToString().ToLowerInvariant(),
                exception.Message,
                exception.Win32ErrorCode);
        }
        catch (Exception exception) when (exception is
            InvalidDataException or ArgumentOutOfRangeException)
        {
            response = Failure(request, "invalid_driver_data", exception.Message);
        }

        await writer.WriteLineAsync(
            PanelDisplayBrokerProtocol.Serialize(response).AsMemory(),
            cancellationToken);
    }
}

static async Task<PanelDisplayBrokerResponse> ExecuteAsync(
    PanelDisplayDriverClient driver,
    BrokerCaptureState captureState,
    BrokerCpuTemperature cpuTemperature,
    PanelDisplayBrokerRequest request,
    CancellationToken cancellationToken)
{
    switch (request.Operation)
    {
        case PanelDisplayBrokerOperation.GetCpuTemperature:
            RejectTarget(request);
            return Success(request) with { CpuTemperatureC = cpuTemperature.Read() };
        case PanelDisplayBrokerOperation.GetStatus:
            RejectTarget(request);
            var status = await driver.GetStatusAsync(cancellationToken);
            captureState.SetEnabled(
                status.MonitorState == PanelDisplayMonitorState.Connected);
            return Success(request) with
            {
                Status = status,
            };
        case PanelDisplayBrokerOperation.Connect:
            RejectTarget(request);
            var connected = await driver.ConnectAsync(cancellationToken);
            captureState.SetEnabled(
                connected.MonitorState == PanelDisplayMonitorState.Connected);
            return Success(request) with
            {
                Command = connected,
            };
        case PanelDisplayBrokerOperation.Disconnect:
            RejectTarget(request);
            captureState.DisableAndReset();
            var command = await driver.DisconnectAsync(cancellationToken);
            return Success(request) with { Command = command };
        case PanelDisplayBrokerOperation.GetCaptureMetrics:
            RejectTarget(request);
            return Success(request) with
            {
                CaptureMetrics = await driver.GetCaptureMetricsAsync(
                    cancellationToken),
            };
        case PanelDisplayBrokerOperation.SetCaptureTarget:
            if (request.TargetFramesPerSecond is not (30 or 45 or 60))
            {
                return Failure(request, "invalid_capture_target",
                    "Capture target must be 30, 45, or 60 FPS.");
            }
            return Success(request) with
            {
                ConfiguredFramesPerSecond =
                    await driver.SetCaptureTargetFpsAsync(
                        request.TargetFramesPerSecond.Value,
                        cancellationToken),
            };
        default:
            throw new InvalidDataException("Unsupported broker operation.");
    }
}

static async Task PumpFramesAsync(
    PanelDisplayDriverClient driver,
    BrokerCaptureState captureState,
    CancellationToken cancellationToken)
{
    ulong lastSequence = 0;
    ulong lastGeneration = 0;
    BrokerTimerResolutionScope? timerResolution = null;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!captureState.IsEnabled)
            {
                timerResolution?.Dispose();
                timerResolution = null;
                lastGeneration = 0;
                lastSequence = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                continue;
            }

            timerResolution ??= BrokerTimerResolutionScope.Acquire();
            try
            {
                var info = await driver.GetFrameInfoAsync(lastSequence, cancellationToken);
                if (info.Result == PanelDisplayFrameResult.Ok)
                {
                    var frame = await driver.ReadFrameAsync(info, cancellationToken);
                    if (frame.Info.Result == PanelDisplayFrameResult.Ok &&
                        captureState.TryPublish(frame))
                    {
                        lastGeneration = frame.Info.Generation;
                        lastSequence = frame.Info.Sequence;
                        continue;
                    }
                }
                else if (info.Result == PanelDisplayFrameResult.NoFrame)
                {
                    if (lastGeneration != 0)
                    {
                        captureState.Reset();
                        lastGeneration = 0;
                        lastSequence = 0;
                    }
                }
            }
            catch (PanelDisplayDriverException exception) when (
                exception.Kind is PanelDisplayDriverFailureKind.NotFound or
                    PanelDisplayDriverFailureKind.IoError)
            {
                captureState.Reset();
                lastGeneration = 0;
                lastSequence = 0;
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
                continue;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
        }
    }
    finally
    {
        timerResolution?.Dispose();
    }
}

static PanelDisplayBrokerResponse Success(PanelDisplayBrokerRequest request) =>
    new(request.RequestId, PanelDisplayBrokerProtocol.Version, true);

static PanelDisplayBrokerResponse Failure(
    PanelDisplayBrokerRequest request,
    string code,
    string message,
    int? win32 = null) =>
    new(
        request.RequestId,
        PanelDisplayBrokerProtocol.Version,
        false,
        Error: new PanelDisplayBrokerError(code, message, win32));

static void RejectTarget(PanelDisplayBrokerRequest request)
{
    if (request.TargetFramesPerSecond is not null)
    {
        throw new InvalidDataException(
            "Unexpected capture target for broker operation.");
    }
}

static async Task BoundedAsync(Task task)
{
    _ = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
}

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
}

internal sealed record BrokerArguments(
    string PipeName,
    string FrameMappingName,
    string Nonce,
    int ParentProcessId,
    long ParentStartTimeUtcTicks)
{
    internal static BrokerArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length ||
                args[index] is not ("--pipe" or "--frame-map" or "--nonce" or
                    "--parent-pid" or "--parent-start-utc-ticks") ||
                !values.TryAdd(args[index], args[index + 1]))
            {
                throw new ArgumentException("Invalid broker arguments.");
            }
        }

        if (values.Count != 5 ||
            !values.TryGetValue("--pipe", out var pipe) ||
            !values.TryGetValue("--frame-map", out var frameMap) ||
            !values.TryGetValue("--nonce", out var nonce) ||
            !values.TryGetValue("--parent-pid", out var pidText) ||
            !values.TryGetValue("--parent-start-utc-ticks", out var ticksText) ||
            !int.TryParse(pidText, out var pid) || pid <= 0 ||
            !long.TryParse(ticksText, out var ticks) || ticks <= 0 ||
            pipe.Length > 240 || frameMap.Length > 240 ||
            pipe.Contains('\\') || frameMap.Contains('\\'))
        {
            throw new ArgumentException("Invalid broker arguments.");
        }

        try
        {
            if (Convert.FromHexString(nonce).Length < 32)
            {
                throw new ArgumentException("Invalid broker nonce.");
            }
        }
        catch (FormatException)
        {
            throw new ArgumentException("Invalid broker nonce.");
        }

        return new BrokerArguments(pipe, frameMap, nonce, pid, ticks);
    }
}
