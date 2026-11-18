using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PanelHost.Hosting;
using PanelHost.Input;
using PanelHost.Management;
using PanelHost.Protocol;
using PanelHost.Services;
using PanelHost.Video;
using PanelHost.Transport;
using PanelOS.Windows.StreamDeck.Icons;

// Imported by name rather than by namespace: PanelOS.Windows.StreamDeck also
// defines MediaCommand and VolumeCommand, which are different types from the
// protocol enums of the same name used throughout this file.
using StreamDeckSnapshot = PanelOS.Windows.StreamDeck.StreamDeckSnapshot;
using StreamDeckSnapshotChangedEventArgs =
    PanelOS.Windows.StreamDeck.StreamDeckSnapshotChangedEventArgs;

namespace PanelHost.Server;

internal sealed class PanelClientSession
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DesktopOperationTeardownTimeout =
        TimeSpan.FromSeconds(5);

    private readonly ITelemetryService _telemetryService;
    private readonly IMediaService _mediaService;
    private readonly IVolumeService _volumeService;
    private readonly IDesktopService _desktopService;
    private readonly ITouchInjectionService _touchInjectionService;
    private readonly IStreamDeckService _streamDeckService;
    private readonly LoopMediaService? _loopMediaService;
    private readonly VideoSessionLifetime? _videoSessionLifetime;
    private readonly PanelHostRuntimeState? _runtimeState;
    private readonly ILogger<PanelClientSession> _logger;
    private readonly TextWriter _compatibilityLog;
    private readonly Func<IComputerStatsSource> _computerStatsSource;
    private readonly LocalNoticeBroker? _notices;
    private readonly ClaudePermissionBroker? _permissions;
    private readonly ProcessTerminationService? _termination;
    private int _touchReceiveDebugCount;
    private int _touchMoveDebugCount;

    /// <summary>
    /// Layout the tablet was last told about. Every invocation is checked
    /// against it, so a button pressed while the configuration was changing
    /// cannot run whatever the same id means now.
    /// </summary>
    private StreamDeckSnapshot _streamDeckSnapshot = StreamDeckSnapshot.Empty;

    public PanelClientSession(
        ITelemetryService telemetryService,
        IMediaService mediaService,
        IVolumeService volumeService,
        IDesktopService desktopService,
        VideoSessionLifetime? videoSessionLifetime = null,
        ITouchInjectionService? touchInjectionService = null,
        IStreamDeckService? streamDeckService = null,
        PanelHostRuntimeState? runtimeState = null,
        ILogger<PanelClientSession>? logger = null,
        Func<IComputerStatsSource>? computerStatsSource = null,
        LocalNoticeBroker? notices = null,
        ClaudePermissionBroker? permissions = null,
        ProcessTerminationService? termination = null,
        LoopMediaService? loopMediaService = null)
    {
        _telemetryService = telemetryService;
        _mediaService = mediaService;
        _volumeService = volumeService;
        _desktopService = desktopService;
        _videoSessionLifetime = videoSessionLifetime;
        _touchInjectionService = touchInjectionService ??
            UnavailableTouchInjectionService.Instance;
        _streamDeckService = streamDeckService ??
            UnavailableStreamDeckService.Instance;
        _runtimeState = runtimeState;
        _logger = logger ?? NullLogger<PanelClientSession>.Instance;
        _compatibilityLog = new LoggerTextWriter(_logger);
        _computerStatsSource = computerStatsSource ?? (() => new WindowsComputerStatsSource());
        _notices = notices;
        _permissions = permissions;
        _termination = termination;
        _loopMediaService = loopMediaService;
    }

    public Task HandleAsync(IPanelConnection connection, CancellationToken cancellationToken) =>
        HandleAsync(connection.Stream, cancellationToken);

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var connection = new JsonLineConnection(stream);
        var managementGeneration = 0L;
        var permissionGeneration = 0L;

        try
        {
            var hello = await CompleteHandshakeAsync(connection, cancellationToken);
            if (hello is null)
            {
                return;
            }

            var supportsArtwork = hello.SupportsFeature(ProtocolFeatures.MediaArtwork);
            var supportsVolume = hello.SupportsFeature(ProtocolFeatures.Volume);
            var supportsDesktop = hello.SupportsFeature(ProtocolFeatures.Desktop);
            var supportsTouch = supportsDesktop &&
                hello.SupportsFeature(ProtocolFeatures.Touch) &&
                _touchInjectionService.IsAvailable;
            var supportsStreamDeck = hello.SupportsFeature(ProtocolFeatures.StreamDeck) &&
                _streamDeckService.IsAvailable;
            var supportsLoopMedia = hello.SupportsFeature(ProtocolFeatures.LoopMedia) &&
                _loopMediaService is not null;
            var negotiatedFeatures = new List<string>();
            var supportsForegroundPresents = hello.SupportsFeature(ProtocolFeatures.ForegroundPresents);
            if (supportsForegroundPresents) negotiatedFeatures.Add(ProtocolFeatures.ForegroundPresents);
            var supportsComputerStats = hello.SupportsFeature(ProtocolFeatures.ComputerStats);
            var supportsProcesses = hello.SupportsFeature(ProtocolFeatures.Processes);
            var supportsNotices = _notices is not null && hello.SupportsFeature("local_notices");
            if (supportsNotices) negotiatedFeatures.Add("local_notices");
            if (_permissions is not null && hello.SupportsFeature("claude_permissions"))
            {
                negotiatedFeatures.Add("claude_permissions");
                permissionGeneration = _permissions.Connect();
            }
            var computer = supportsComputerStats || supportsProcesses
                ? new ComputerStatsSession(supportsComputerStats, supportsProcesses, _computerStatsSource()) : null;
            if (supportsComputerStats) negotiatedFeatures.Add(ProtocolFeatures.ComputerStats);
            if (supportsProcesses) negotiatedFeatures.Add(ProtocolFeatures.Processes);
            if (supportsArtwork) negotiatedFeatures.Add(ProtocolFeatures.MediaArtwork);
            if (supportsVolume) negotiatedFeatures.Add(ProtocolFeatures.Volume);
            if (supportsDesktop) negotiatedFeatures.Add(ProtocolFeatures.Desktop);
            if (supportsTouch) negotiatedFeatures.Add(ProtocolFeatures.Touch);
            if (supportsStreamDeck) negotiatedFeatures.Add(ProtocolFeatures.StreamDeck);
            if (supportsLoopMedia) negotiatedFeatures.Add(ProtocolFeatures.LoopMedia);
            managementGeneration = _runtimeState?.ControlConnected(
                hello.ProtocolVersion,
                negotiatedFeatures) ?? 0;
            _logger.LogDebug(
                "Touch negotiated={Negotiated} clientDesktop={ClientDesktop} clientTouch={ClientTouch} nativeAvailable={NativeAvailable}",
                supportsTouch,
                supportsDesktop,
                hello.SupportsFeature(ProtocolFeatures.Touch),
                _touchInjectionService.IsAvailable);
            var mediaUpdates = Channel.CreateBounded<MediaStateChangedEventArgs>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var volumeUpdates = Channel.CreateBounded<VolumeStateMessage>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var desktopUpdates = Channel.CreateBounded<DesktopStateMessage>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var desktopCommands = Channel.CreateBounded<DesktopCommand>(
                new BoundedChannelOptions(DesktopCommandQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                });

            // Only the newest layout matters: an older one describes buttons
            // the tablet must not be drawing any more.
            var streamDeckLayouts = Channel.CreateBounded<StreamDeckLayoutMessage>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });

            void OnStreamDeckSnapshotChanged(
                object? sender,
                StreamDeckSnapshotChangedEventArgs eventArgs)
            {
                var snapshot = eventArgs.Snapshot;
                Volatile.Write(ref _streamDeckSnapshot, snapshot);
                streamDeckLayouts.Writer.TryWrite(
                    StreamDeckProtocolMapper.CreateLayout(
                        snapshot.Revision,
                        snapshot.Layout));
            }

            var loopMediaStates = Channel.CreateBounded<byte>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                });
            void OnLoopMediaChanged(object? sender, EventArgs args) =>
                loopMediaStates.Writer.TryWrite(0);

            void OnMediaStateChanged(object? sender, MediaStateChangedEventArgs eventArgs) =>
                mediaUpdates.Writer.TryWrite(eventArgs);

            void OnVolumeStateChanged(object? sender, VolumeStateChangedEventArgs eventArgs) =>
                volumeUpdates.Writer.TryWrite(eventArgs.State);

            void OnDesktopStateChanged(object? sender, DesktopStateChangedEventArgs eventArgs) =>
                desktopUpdates.Writer.TryWrite(
                    CreateDesktopStateMessage(eventArgs.State, supportsTouch));

            _mediaService.StateChanged += OnMediaStateChanged;
            if (supportsVolume)
            {
                _volumeService.StateChanged += OnVolumeStateChanged;
            }

            if (supportsDesktop)
            {
                _desktopService.StateChanged += OnDesktopStateChanged;
            }

            if (supportsStreamDeck)
            {
                _streamDeckService.SnapshotChanged += OnStreamDeckSnapshotChanged;
            }

            using var sessionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task? receiveTask = null;
            Task? telemetryTask = null;
            Task? mediaTask = null;
            Task? volumeTask = null;
            Task? desktopSenderTask = null;
            Task? desktopOperationTask = null;
            Task? streamDeckSenderTask = null;
            Task? loopMediaSenderTask = null;

            try
            {
                var initialArtwork = _mediaService.CurrentArtwork;
                if (supportsLoopMedia)
                {
                    _loopMediaService!.StateChanged += OnLoopMediaChanged;
                    loopMediaStates.Writer.TryWrite(0);
                    loopMediaSenderTask = SendLoopMediaStatesAsync(
                        connection, loopMediaStates.Reader, sessionCancellation.Token);
                }
                await connection.WriteAsync(_mediaService.CurrentState, cancellationToken);
                if (supportsArtwork)
                {
                    await connection.WriteAsync(initialArtwork, cancellationToken);
                }

                var initialVolume = _volumeService.CurrentState;
                if (supportsVolume)
                {
                    await connection.WriteAsync(initialVolume, cancellationToken);
                }

                if (supportsDesktop)
                {
                    desktopUpdates.Writer.TryWrite(
                        CreateDesktopStateMessage(
                            _desktopService.Snapshot,
                            supportsTouch));
                }

                if (supportsStreamDeck)
                {
                    // The tablet never asks for the first layout: a reconnect
                    // must not depend on state it kept from a previous session.
                    var snapshot = _streamDeckService.Snapshot;
                    Volatile.Write(ref _streamDeckSnapshot, snapshot);
                    await connection.WriteAsync(
                        StreamDeckProtocolMapper.CreateLayout(
                            snapshot.Revision,
                            snapshot.Layout),
                        cancellationToken);
                    _logger.LogInformation(
                        "StreamDeck layout revision={Revision} profile={Profile} pages={Pages}",
                        snapshot.Revision,
                        snapshot.ProfileName,
                        snapshot.PageCount);
                }

                receiveTask = ReceiveMessagesAsync(
                    connection,
                    computer,
                    permissionGeneration,
                    supportsDesktop,
                    supportsVolume,
                    supportsTouch,
                    supportsStreamDeck,
                    supportsLoopMedia,
                    desktopCommands.Writer,
                    sessionCancellation.Token);
                telemetryTask = SendTelemetryAsync(
                    connection,
                    computer,
                    supportsNotices,
                    permissionGeneration != 0,
                    supportsForegroundPresents,
                    sessionCancellation.Token);
                mediaTask = SendMediaUpdatesAsync(
                    connection,
                    mediaUpdates.Reader,
                    supportsArtwork,
                    initialArtwork,
                    sessionCancellation.Token);
                if (supportsVolume)
                {
                    volumeTask = SendVolumeUpdatesAsync(
                        connection,
                        volumeUpdates.Reader,
                        initialVolume,
                        sessionCancellation.Token);
                }

                if (supportsDesktop)
                {
                    desktopSenderTask = SendDesktopUpdatesAsync(
                        connection,
                        desktopUpdates.Reader,
                        sessionCancellation.Token);
                    desktopOperationTask = ProcessDesktopCommandsAsync(
                        desktopCommands.Reader,
                        desktopUpdates.Writer,
                        supportsTouch,
                        sessionCancellation.Token);
                }

                if (supportsStreamDeck)
                {
                    streamDeckSenderTask = SendStreamDeckLayoutsAsync(
                        connection,
                        streamDeckLayouts.Reader,
                        sessionCancellation.Token);
                }

                await Task.WhenAny(
                    new[]
                    {
                        receiveTask,
                        telemetryTask,
                        mediaTask,
                        volumeTask,
                        desktopSenderTask,
                        desktopOperationTask,
                        streamDeckSenderTask,
                        loopMediaSenderTask,
                    }
                        .OfType<Task>());
            }
            finally
            {
                sessionCancellation.Cancel();
                if (permissionGeneration != 0) _permissions!.Disconnect(permissionGeneration);
                if (supportsTouch)
                {
                    _touchInjectionService.CancelActiveContact(
                        "control connection teardown");
                }
                desktopCommands.Writer.TryComplete();
                await ObserveSessionTasksAsync(
                    cancellationToken,
                    receiveTask);
                await ObserveDesktopOperationAsync(
                    cancellationToken,
                    desktopOperationTask);

                _mediaService.StateChanged -= OnMediaStateChanged;
                if (supportsVolume)
                {
                    _volumeService.StateChanged -= OnVolumeStateChanged;
                }

                if (supportsDesktop)
                {
                    _desktopService.StateChanged -= OnDesktopStateChanged;
                }

                if (supportsStreamDeck)
                {
                    _streamDeckService.SnapshotChanged -= OnStreamDeckSnapshotChanged;
                }

                mediaUpdates.Writer.TryComplete();
                volumeUpdates.Writer.TryComplete();
                desktopUpdates.Writer.TryComplete();
                streamDeckLayouts.Writer.TryComplete();
                if (supportsLoopMedia)
                    _loopMediaService!.StateChanged -= OnLoopMediaChanged;
                loopMediaStates.Writer.TryComplete();
                await ObserveSessionTasksAsync(
                    cancellationToken,
                    telemetryTask,
                    mediaTask,
                    volumeTask,
                    desktopSenderTask,
                    streamDeckSenderTask,
                    loopMediaSenderTask);

                _logger.LogInformation(
                    "PanelHome control session disconnected; session={Session}",
                    managementGeneration);
            }
        }
        finally
        {
            if (permissionGeneration != 0) _permissions!.Disconnect(permissionGeneration);
            if (managementGeneration != 0)
            {
                _runtimeState?.ControlDisconnected(managementGeneration);
            }
            _videoSessionLifetime?.NotifyControlDisconnected();
            _touchInjectionService.DeactivateDesktop("control disconnect");
            if (_desktopService.State != DesktopServiceState.Stopped)
            {
                _logger.LogInformation("Desktop client disconnected");
                await DesktopShutdown.StopAsync(
                    _desktopService,
                    _compatibilityLog,
                    "Client disconnected");
            }
        }
    }

    private async Task<HelloMessage?> CompleteHandshakeAsync(
        JsonLineConnection connection,
        CancellationToken cancellationToken)
    {
        using var handshakeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeCancellation.CancelAfter(HandshakeTimeout);

        string? line;
        try
        {
            line = await connection.ReadLineAsync(handshakeCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Panel Protocol handshake timed out");
            return null;
        }

        if (line is null)
        {
            return null;
        }

        HelloMessage hello;
        try
        {
            var envelope = PanelJson.Deserialize<MessageEnvelope>(line);
            if (envelope.Type != MessageTypes.Hello)
            {
                await SendErrorAsync(connection, ErrorCodes.InvalidHandshake, cancellationToken);
                return null;
            }

            hello = PanelJson.Deserialize<HelloMessage>(line);
        }
        catch (JsonException)
        {
            await SendErrorAsync(connection, ErrorCodes.InvalidHandshake, cancellationToken);
            return null;
        }

        var validation = HandshakeValidator.Validate(hello);
        if (!validation.IsAccepted)
        {
            await SendErrorAsync(
                connection,
                validation.ErrorCode ?? ErrorCodes.InvalidHandshake,
                cancellationToken);
            return null;
        }

        var features = new List<string>
        {
            ProtocolFeatures.ComputerStats,
            ProtocolFeatures.Processes,
            ProtocolFeatures.Desktop,
            ProtocolFeatures.Volume,
        };
        if (_touchInjectionService.IsAvailable)
        {
            features.Add(ProtocolFeatures.Touch);
        }

        if (_notices is not null) features.Add("local_notices");
        if (_permissions is not null) features.Add("claude_permissions");

        if (_streamDeckService.IsAvailable)
        {
            features.Add(ProtocolFeatures.StreamDeck);
        }

        if (hello.SupportsFeature(ProtocolFeatures.ForegroundPresents)) features.Add(ProtocolFeatures.ForegroundPresents);

        if (_loopMediaService is not null && hello.SupportsFeature(ProtocolFeatures.LoopMedia))
            features.Add(ProtocolFeatures.LoopMedia);

        await connection.WriteAsync(
            new HelloAckMessage
            {
                HostName = Environment.MachineName,
                Features = features.ToArray(),
                BuildIdentity = PanelBuildIdentity.Identity,
                BuildNumber = PanelBuildIdentity.Number,
            },
            cancellationToken);
        if (hello.BuildNumber is not null && PanelBuildIdentity.Number > hello.BuildNumber)
        {
            _logger.LogWarning(
                "Host build {HostIdentity} ({HostBuild}) is newer than client {ClientIdentity} ({ClientBuild}); protocol {Protocol} remains compatible",
                PanelBuildIdentity.Identity,
                PanelBuildIdentity.Number,
                hello.BuildIdentity,
                hello.BuildNumber,
                hello.ProtocolVersion);
        }
        _logger.LogInformation(
            "PanelHome connected with protocol {Protocol}",
            hello.ProtocolVersion);
        return hello;
    }

    private async Task ReceiveMessagesAsync(
        JsonLineConnection connection,
        ComputerStatsSession? computer,
        long permissionGeneration,
        bool supportsDesktop,
        bool supportsVolume,
        bool supportsTouch,
        bool supportsStreamDeck,
        bool supportsLoopMedia,
        ChannelWriter<DesktopCommand> desktopCommands,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await connection.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return;
            }

            MessageEnvelope envelope;
            try
            {
                envelope = PanelJson.Deserialize<MessageEnvelope>(line);
            }
            catch (JsonException)
            {
                await SendErrorAsync(connection, ErrorCodes.InvalidMessage, cancellationToken);
                continue;
            }

            switch (envelope.Type)
            {
                case MessageTypes.Command:
                    await HandleCommandAsync(
                        connection,
                        computer,
                        permissionGeneration,
                        line,
                        supportsDesktop,
                        supportsVolume,
                        supportsTouch,
                        supportsStreamDeck,
                        supportsLoopMedia,
                        desktopCommands,
                        cancellationToken);
                    break;
                case MessageTypes.Ping:
                    await connection.WriteAsync(new PongMessage(), cancellationToken);
                    break;
                case MessageTypes.Pong:
                    break;
                default:
                    await SendErrorAsync(connection, ErrorCodes.InvalidMessage, cancellationToken);
                    break;
            }
        }
    }

    private async Task HandleCommandAsync(
        JsonLineConnection connection,
        ComputerStatsSession? computer,
        long permissionGeneration,
        string line,
        bool supportsDesktop,
        bool supportsVolume,
        bool supportsTouch,
        bool supportsStreamDeck,
        bool supportsLoopMedia,
        ChannelWriter<DesktopCommand> desktopCommands,
        CancellationToken cancellationToken)
    {
        CommandMessage command;
        try
        {
            command = PanelJson.Deserialize<CommandMessage>(line);
        }
        catch (JsonException)
        {
            await SendErrorAsync(connection, ErrorCodes.InvalidMessage, cancellationToken);
            return;
        }

        if (command.Command == "processes.terminate")
        {
            if (computer?.Processes != true || _termination is null)
                await SendErrorAsync(connection, ErrorCodes.UnknownCommand, cancellationToken);
            else
                await connection.WriteAsync(_termination.Terminate(command.RequestId, command.Pid, command.StartTimeUtcTicks,
                    computer.Offered(command.Pid, command.StartTimeUtcTicks),
                    computer.ListedProtected(command.Pid, command.StartTimeUtcTicks)), cancellationToken);
            return;
        }

        if (command.Command is "claude.shown" or "claude.respond")
        {
            if (permissionGeneration == 0)
                await SendErrorAsync(connection, ErrorCodes.UnknownCommand, cancellationToken);
            else
            {
                var valid = command.RequestId is { Length: 32 } id && (command.Command == "claude.shown"
                    ? _permissions!.Shown(id, permissionGeneration)
                    : _permissions!.Respond(id, command.Decision ?? "", permissionGeneration));
                if (!valid) await SendErrorAsync(connection, ErrorCodes.InvalidMessage, cancellationToken);
            }
            return;
        }

        if (command.Command == "processes.options")
        {
            if (computer?.Processes != true)
                await SendErrorAsync(connection, ErrorCodes.UnknownCommand, cancellationToken);
            else if (!computer.SetOptions(command.Sort, command.Grouped))
                await SendErrorAsync(connection, ErrorCodes.InvalidMessage, cancellationToken);
            return;
        }

        if (!string.Equals(
                command.Command,
                CommandNames.TouchMove,
                StringComparison.Ordinal))
        {
            _logger.LogDebug("Command {Command} received", command.Command);
        }
        if (CommandNames.TryParse(command.Command, out var mediaCommand))
        {
            var succeeded = await _mediaService.ExecuteCommandAsync(
                mediaCommand,
                cancellationToken);
            _logger.LogInformation(
                "Media command {Command} completed with available={Available}",
                GetMediaCommandLabel(mediaCommand),
                succeeded);

            await connection.WriteAsync(_mediaService.CurrentState, cancellationToken);
            return;
        }

        if (supportsVolume &&
            CommandNames.TryParseVolume(command.Command, out var volumeCommand))
        {
            bool succeeded;
            if (volumeCommand == VolumeCommand.Set)
            {
                if (command.Value is not int volumePercent)
                {
                    await SendErrorAsync(
                        connection,
                        ErrorCodes.InvalidMessage,
                        cancellationToken);
                    return;
                }

                succeeded = await _volumeService.SetVolumeAsync(
                    volumePercent,
                    cancellationToken);
            }
            else
            {
                succeeded = await _volumeService.ToggleMuteAsync(cancellationToken);
            }

            _logger.LogInformation(
                "Volume command {Command} completed with available={Available}",
                GetVolumeCommandLabel(volumeCommand),
                succeeded);
            return;
        }

        if (supportsDesktop &&
            CommandNames.TryParseDesktop(command.Command, out var desktopCommand))
        {
            if (!desktopCommands.TryWrite(desktopCommand))
            {
                await SendErrorAsync(
                    connection,
                    ErrorCodes.InvalidMessage,
                    cancellationToken);
            }

            return;
        }

        if (string.Equals(
                command.Command,
                CommandNames.StreamDeckInvoke,
                StringComparison.Ordinal))
        {
            if (!supportsStreamDeck)
            {
                await SendErrorAsync(
                    connection,
                    ErrorCodes.UnknownCommand,
                    cancellationToken);
                return;
            }

            await HandleStreamDeckInvokeAsync(connection, command, cancellationToken);
            return;
        }

        if (string.Equals(
                command.Command,
                CommandNames.StreamDeckIconsRequest,
                StringComparison.Ordinal))
        {
            if (!supportsStreamDeck)
            {
                await SendErrorAsync(
                    connection,
                    ErrorCodes.UnknownCommand,
                    cancellationToken);
                return;
            }

            await HandleStreamDeckIconRequestAsync(
                connection,
                command,
                cancellationToken);
            return;
        }

        if (command.Command == CommandNames.LoopMediaRequest)
        {
            if (!supportsLoopMedia)
            {
                await SendErrorAsync(connection, ErrorCodes.UnknownCommand, cancellationToken);
                return;
            }
            // The receive loop awaits each write: at most one chunk is pending.
            var chunk = await _loopMediaService!.ReadChunkAsync(
                command.MediaId, command.Offset ?? -1, cancellationToken);
            await connection.WriteAsync(chunk, cancellationToken);
            return;
        }

        if (CommandNames.TryParseTouch(command.Command, out var touchCommand))
        {
            if (!supportsTouch)
            {
                _logger.LogWarning(
                    "Touch rejected with errorCode {ErrorCode}; command={Command}",
                    "touch_not_negotiated",
                    command.Command);
                await SendErrorAsync(
                    connection,
                    ErrorCodes.UnknownCommand,
                    cancellationToken);
                return;
            }
            if (!TryCreateTouchCommand(
                    command,
                    touchCommand,
                    out var normalizedTouch,
                    out var rejection))
            {
                _logger.LogWarning(
                    "Touch rejected with errorCode {ErrorCode}",
                    rejection);
                await SendErrorAsync(
                    connection,
                    ErrorCodes.InvalidMessage,
                    cancellationToken);
                return;
            }

            if (ShouldLogTouchReceive(normalizedTouch.Action))
            {
                var coordinates = normalizedTouch.X is double x &&
                    normalizedTouch.Y is double y
                    ? " norm=" + FormattableString.Invariant($"({x:F3},{y:F3})")
                    : string.Empty;
                _logger.LogDebug(
                    "Touch {Action} received for pointer={Pointer} generation={Generation} currentGeneration={CurrentGeneration} coordinates={Coordinates}",
                    normalizedTouch.Action,
                    normalizedTouch.PointerId,
                    normalizedTouch.Generation,
                    _touchInjectionService.ActiveGeneration,
                    coordinates);
            }
            var result = _touchInjectionService.Process(normalizedTouch);
            if (!result.Accepted)
            {
                _logger.LogWarning(
                    "Touch rejected with errorCode {ErrorCode}; eventGeneration={EventGeneration} currentGeneration={CurrentGeneration}",
                    result.ErrorCode ?? "unknown",
                    normalizedTouch.Generation,
                    _touchInjectionService.ActiveGeneration);
            }
            return;
        }

        await SendErrorAsync(connection, ErrorCodes.UnknownCommand, cancellationToken);
    }

    /// <summary>
    /// Runs a button the tablet pressed.
    /// </summary>
    /// <remarks>
    /// The only thing that arrives is an id plus the revision it was drawn
    /// from. A revision that no longer matches runs nothing and answers with
    /// the current layout, because the tablet is showing buttons that may have
    /// been re-pointed since.
    /// </remarks>
    private async Task HandleStreamDeckInvokeAsync(
        JsonLineConnection connection,
        CommandMessage command,
        CancellationToken cancellationToken)
    {
        var buttonId = command.ButtonId?.Trim();
        if (string.IsNullOrEmpty(buttonId) ||
            buttonId.Length > StreamDeckProtocolLimits.MaximumButtonIdLength ||
            command.Revision?.Length > StreamDeckProtocolLimits.MaximumRevisionLength)
        {
            await connection.WriteAsync(
                StreamDeckProtocolMapper.CreateFailure(
                    buttonId ?? string.Empty,
                    StreamDeckProtocolErrorCodes.InvalidRequest),
                cancellationToken);
            return;
        }

        var snapshot = Volatile.Read(ref _streamDeckSnapshot);
        if (!string.Equals(command.Revision, snapshot.Revision, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "StreamDeck command failed with errorCode {ErrorCode}",
                StreamDeckProtocolErrorCodes.StaleLayout);
            await connection.WriteAsync(
                StreamDeckProtocolMapper.CreateFailure(
                    buttonId,
                    StreamDeckProtocolErrorCodes.StaleLayout),
                cancellationToken);

            // Answer the staleness immediately instead of waiting for the next
            // configuration change, which may never come.
            await connection.WriteAsync(
                StreamDeckProtocolMapper.CreateLayout(
                    snapshot.Revision,
                    snapshot.Layout),
                cancellationToken);
            return;
        }

        var result = await _streamDeckService.InvokeAsync(
            snapshot,
            buttonId,
            cancellationToken);
        await connection.WriteAsync(
            StreamDeckProtocolMapper.CreateResult(buttonId, result),
            cancellationToken);
    }

    /// <summary>
    /// Answers with the PNG bytes of icons the tablet does not have yet.
    /// </summary>
    /// <remarks>
    /// An id is a key into the PC icon cache and nothing else: the cache
    /// validates its shape before it is ever combined into a path, so
    /// <c>..\..</c>, an absolute path or a separator resolves to nothing. Only
    /// ids the published layout actually refers to are served, and an oversized
    /// or missing file answers "unavailable" rather than failing the session.
    /// </remarks>
    private async Task HandleStreamDeckIconRequestAsync(
        JsonLineConnection connection,
        CommandMessage command,
        CancellationToken cancellationToken)
    {
        var requested = command.IconIds ?? [];
        var snapshot = Volatile.Read(ref _streamDeckSnapshot);
        var published = snapshot.PublishedIconIds;
        var served = 0;

        foreach (var iconId in requested.Distinct(StringComparer.Ordinal))
        {
            if (served >= StreamDeckProtocolLimits.MaximumIconRequestCount)
            {
                break;
            }

            served++;
            if (!StreamDeckIconCache.IsValidIconId(iconId) ||
                !published.Contains(iconId, StringComparer.Ordinal))
            {
                await connection.WriteAsync(
                    new StreamDeckIconMessage { IconId = iconId ?? string.Empty },
                    cancellationToken);
                continue;
            }

            var bytes = await _streamDeckService.TryReadIconAsync(
                iconId,
                cancellationToken);
            await connection.WriteAsync(
                bytes is null
                    ? new StreamDeckIconMessage { IconId = iconId }
                    : new StreamDeckIconMessage
                    {
                        IconId = iconId,
                        Available = true,
                        Mime = StreamDeckProtocolMapper.IconMimeType,
                        Data = Convert.ToBase64String(bytes),
                    },
                cancellationToken);
        }

        _logger.LogInformation("StreamDeck icons requested={Count}", served);
    }

    private async Task SendLoopMediaStatesAsync(
        JsonLineConnection connection,
        ChannelReader<byte> states,
        CancellationToken cancellationToken)
    {
        await foreach (var signal in states.ReadAllAsync(cancellationToken))
            await connection.WriteAsync(_loopMediaService!.GetState(), cancellationToken);
    }

    private static async Task SendStreamDeckLayoutsAsync(
        JsonLineConnection connection,
        ChannelReader<StreamDeckLayoutMessage> layouts,
        CancellationToken cancellationToken)
    {
        await foreach (var layout in layouts.ReadAllAsync(cancellationToken))
        {
            await connection.WriteAsync(layout, cancellationToken);
        }
    }

    private async Task ProcessDesktopCommandsAsync(
        ChannelReader<DesktopCommand> commands,
        ChannelWriter<DesktopStateMessage> desktopStates,
        bool supportsTouch,
        CancellationToken cancellationToken)
    {
        await foreach (var command in commands.ReadAllAsync(cancellationToken))
        {
            await HandleDesktopCommandAsync(
                command,
                desktopStates,
                supportsTouch,
                cancellationToken);
        }
    }

    private async Task HandleDesktopCommandAsync(
        DesktopCommand command,
        ChannelWriter<DesktopStateMessage> desktopStates,
        bool supportsTouch,
        CancellationToken cancellationToken)
    {
        DesktopServiceSnapshot snapshot;
        bool succeeded;
        bool alreadyInRequestedState;
        _logger.LogInformation("Desktop {Command} requested", command);
        try
        {
            if (command == DesktopCommand.Start)
            {
                var result = await _desktopService.StartAsync(
                    new DesktopRequest(),
                    cancellationToken);
                succeeded = result.Succeeded;
                alreadyInRequestedState = result.AlreadyRunning;
                snapshot = new DesktopServiceSnapshot(
                    result.State,
                    result.ErrorCode);
                if (!succeeded)
                {
                    snapshot = new DesktopServiceSnapshot(
                        DesktopServiceState.Error,
                        result.ErrorCode ?? DesktopErrorCodes.StartFailed);
                }
            }
            else
            {
                var result = await _desktopService.StopAsync(cancellationToken);
                succeeded = result.Succeeded;
                alreadyInRequestedState = result.AlreadyStopped;
                snapshot = new DesktopServiceSnapshot(
                    result.State,
                    result.ErrorCode);
                if (!succeeded)
                {
                    snapshot = new DesktopServiceSnapshot(
                        DesktopServiceState.Error,
                        result.ErrorCode ?? DesktopErrorCodes.StopFailed);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Desktop {Command} failed with errorCode {ErrorCode}",
                command,
                DesktopErrorCodes.InternalError);
            desktopStates.TryWrite(CreateDesktopStateMessage(
                new DesktopServiceSnapshot(
                    DesktopServiceState.Error,
                    DesktopErrorCodes.InternalError),
                supportsTouch));
            return;
        }

        if (alreadyInRequestedState)
        {
            _logger.LogInformation(
                "Desktop already in requested driverState {DriverState}",
                snapshot.State);
        }

        if (!succeeded)
        {
            _logger.LogError(
                "Desktop transition failed with driverState {DriverState} and errorCode {ErrorCode}",
                snapshot.State,
                snapshot.ErrorCode ?? DesktopErrorCodes.InternalError);
        }
        else if (snapshot.State == DesktopServiceState.Running)
        {
            _logger.LogInformation(
                "Desktop transition completed with driverState {DriverState}",
                snapshot.State);
        }
        else if (snapshot.State == DesktopServiceState.Stopped)
        {
            _logger.LogInformation(
                "Desktop transition completed with driverState {DriverState}",
                snapshot.State);
        }

        desktopStates.TryWrite(CreateDesktopStateMessage(
            snapshot,
            supportsTouch));
    }

    private DesktopStateMessage CreateDesktopStateMessage(
        DesktopServiceSnapshot snapshot,
        bool supportsTouch)
    {
        if (supportsTouch)
        {
            _touchInjectionService.SetDesktopState(snapshot);
        }

        return DesktopProtocolMapper.CreateStateMessage(
            snapshot,
            supportsTouch ? _touchInjectionService.ActiveGeneration : null);
    }

    private bool ShouldLogTouchReceive(DesktopTouchAction action)
    {
        if (_touchReceiveDebugCount >= 20)
        {
            return false;
        }
        if (action == DesktopTouchAction.Move && _touchMoveDebugCount++ >= 3)
        {
            return false;
        }

        _touchReceiveDebugCount++;
        return true;
    }

    private static bool TryCreateTouchCommand(
        CommandMessage message,
        DesktopTouchCommand command,
        out NormalizedTouchCommand normalized,
        out string rejection)
    {
        normalized = default;
        rejection = string.Empty;
        if (message.PointerId is not int pointerId)
        {
            rejection = "malformed_message_missing_pointer";
            return false;
        }
        if (message.Generation is not long generation || generation <= 0)
        {
            rejection = "invalid_generation";
            return false;
        }

        var action = command switch
        {
            DesktopTouchCommand.Down => DesktopTouchAction.Down,
            DesktopTouchCommand.Move => DesktopTouchAction.Move,
            DesktopTouchCommand.Up => DesktopTouchAction.Up,
            DesktopTouchCommand.Cancel => DesktopTouchAction.Cancel,
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        if (action != DesktopTouchAction.Cancel &&
            (message.X is not double x || !double.IsFinite(x) ||
                message.Y is not double y || !double.IsFinite(y)))
        {
            rejection = "invalid_coordinates";
            return false;
        }

        normalized = new NormalizedTouchCommand(
            action,
            pointerId,
            message.X,
            message.Y,
            generation);
        rejection = string.Empty;
        return true;
    }

    private async Task SendTelemetryAsync(
        JsonLineConnection connection,
        ComputerStatsSession? computer,
        bool supportsNotices,
        bool supportsPermissions,
        bool supportsForegroundPresents,
        CancellationToken cancellationToken)
    {
        using var fps = supportsForegroundPresents ? new EtwForegroundPresentsSource() : null;
        fps?.Start();
        while (!cancellationToken.IsCancellationRequested)
        {
            await connection.WriteAsync(
                _telemetryService.CurrentSnapshot with { Fps = fps?.Sample() },
                cancellationToken);
            if (computer is not null)
                foreach (var sample in computer.Sample())
                    await connection.WriteAsync(sample is ProcessesMessage processList
                        ? processList with { TerminateEnabled = _termination?.Enabled == true } : sample, cancellationToken);
            if (supportsNotices) await connection.WriteAsync(_notices!.Snapshot(), cancellationToken);
            if (supportsPermissions) await connection.WriteAsync(_permissions!.Snapshot(), cancellationToken);
            await Task.Delay(TelemetryInterval, cancellationToken);
        }
    }

    private static async Task SendMediaUpdatesAsync(
        JsonLineConnection connection,
        ChannelReader<MediaStateChangedEventArgs> updates,
        bool supportsArtwork,
        MediaArtworkMessage lastArtwork,
        CancellationToken cancellationToken)
    {
        await foreach (var update in updates.ReadAllAsync(cancellationToken))
        {
            await connection.WriteAsync(update.State, cancellationToken);
            if (supportsArtwork && update.Artwork != lastArtwork)
            {
                await connection.WriteAsync(update.Artwork, cancellationToken);
                lastArtwork = update.Artwork;
            }
        }
    }

    private static async Task SendDesktopUpdatesAsync(
        JsonLineConnection connection,
        ChannelReader<DesktopStateMessage> updates,
        CancellationToken cancellationToken)
    {
        DesktopStateMessage? lastState = null;
        await foreach (var state in updates.ReadAllAsync(cancellationToken))
        {
            if (state == lastState)
            {
                continue;
            }

            await connection.WriteAsync(state, cancellationToken);
            lastState = state;
        }
    }

    private static async Task SendVolumeUpdatesAsync(
        JsonLineConnection connection,
        ChannelReader<VolumeStateMessage> updates,
        VolumeStateMessage lastState,
        CancellationToken cancellationToken)
    {
        await foreach (var state in updates.ReadAllAsync(cancellationToken))
        {
            if (state == lastState)
            {
                continue;
            }

            await connection.WriteAsync(state, cancellationToken);
            lastState = state;
        }
    }

    private static Task SendErrorAsync(
        JsonLineConnection connection,
        string errorCode,
        CancellationToken cancellationToken) =>
        connection.WriteAsync(new ErrorMessage { Code = errorCode }, cancellationToken);

    private static string GetMediaCommandLabel(MediaCommand command) => command switch
    {
        MediaCommand.Previous => "Previous",
        MediaCommand.PlayPause => "Play/Pause",
        MediaCommand.Next => "Next",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private static string GetVolumeCommandLabel(VolumeCommand command) => command switch
    {
        VolumeCommand.Set => "Set",
        VolumeCommand.ToggleMute => "Toggle mute",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
    };

    private async Task ObserveSessionTasksAsync(
        CancellationToken serverCancellationToken,
        params Task?[] sessionTasks)
    {
        var startedTasks = sessionTasks.OfType<Task>().ToArray();
        if (startedTasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(startedTasks);
        }
        catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PanelHost session task failed");
        }
    }

    private async Task ObserveDesktopOperationAsync(
        CancellationToken serverCancellationToken,
        Task? desktopOperationTask)
    {
        if (desktopOperationTask is null)
        {
            return;
        }

        try
        {
            await desktopOperationTask.WaitAsync(DesktopOperationTeardownTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "In-flight desktop operation did not finish during session teardown; continuing with bounded cleanup");
            ObserveFaultAfterTimeout(desktopOperationTask);
        }
        catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "PanelHost session task failed");
        }
    }

    private static void ObserveFaultAfterTimeout(Task task) =>
        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private const int DesktopCommandQueueCapacity = 8;
}

