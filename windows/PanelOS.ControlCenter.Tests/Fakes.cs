using PanelOS.DriverInterop;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.Driver;
using PanelOS.Windows.StreamDeck;

namespace PanelOS.ControlCenter.Tests;

/// <summary>
/// Scriptable stand-in for the linked PanelHost driver client. It never touches
/// a real device interface.
/// </summary>
internal sealed class FakeDriverClient : IPanelDisplayDriverClient
{
    public string? InterfacePath { get; set; } = @"\\?\panel-display-test";

    public PanelDisplayDriverState DriverState { get; set; } =
        PanelDisplayDriverState.Ready;

    public PanelDisplayMonitorState MonitorState { get; set; } =
        PanelDisplayMonitorState.Disconnected;

    public PanelDisplayMode ActiveMode { get; set; }

    public Exception? StatusFailure { get; set; }

    public Exception? CommandFailure { get; set; }

    public PanelDisplayCommandResult? ConnectResult { get; set; }

    public PanelDisplayCommandResult? DisconnectResult { get; set; }

    public int ConnectCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public Task<PanelDisplayDriverStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StatusFailure is not null)
        {
            InterfacePath = null;
            return Task.FromException<PanelDisplayDriverStatus>(StatusFailure);
        }

        return Task.FromResult(new PanelDisplayDriverStatus(
            PanelDisplayDriverContract.ProtocolVersion,
            DriverState,
            MonitorState,
            ActiveMode,
            FramesProcessed: 0,
            ConnectCount: 1,
            Modes: [new PanelDisplayMode(1280, 800, 60)]));
    }

    public Task<PanelDisplayCommandResponse> ConnectAsync(
        CancellationToken cancellationToken)
    {
        ConnectCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (CommandFailure is not null)
        {
            return Task.FromException<PanelDisplayCommandResponse>(CommandFailure);
        }

        var result = ConnectResult ?? PanelDisplayCommandResult.Ok;
        if (result is PanelDisplayCommandResult.Ok or
            PanelDisplayCommandResult.AlreadyConnected)
        {
            MonitorState = PanelDisplayMonitorState.Connected;
        }

        return Task.FromResult(
            new PanelDisplayCommandResponse(result, MonitorState));
    }

    public Task<PanelDisplayCommandResponse> DisconnectAsync(
        CancellationToken cancellationToken)
    {
        DisconnectCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (CommandFailure is not null)
        {
            return Task.FromException<PanelDisplayCommandResponse>(CommandFailure);
        }

        var result = DisconnectResult ?? PanelDisplayCommandResult.Ok;
        if (result is PanelDisplayCommandResult.Ok or
            PanelDisplayCommandResult.AlreadyDisconnected)
        {
            MonitorState = PanelDisplayMonitorState.Disconnected;
        }

        return Task.FromResult(
            new PanelDisplayCommandResponse(result, MonitorState));
    }

    internal static PanelDisplayDriverException Failure(
        PanelDisplayDriverFailureKind kind,
        string operation = "GET_STATUS") =>
        new(kind, operation, $"Scripted {operation} failure.", null, null);
}

internal sealed class FakePanelDriverController : IPanelDriverController
{
    public PanelDriverSnapshot Snapshot { get; set; } = PanelDriverSnapshot.Unknown;

    public PanelDriverCommandResult ConnectResult { get; set; } =
        new(PanelDriverCommandStatus.Succeeded, PanelMonitorConnection.Connected);

    public PanelDriverCommandResult DisconnectResult { get; set; } =
        new(PanelDriverCommandStatus.Succeeded, PanelMonitorConnection.Disconnected);

    public int SnapshotCalls { get; private set; }

    public int ConnectCalls { get; private set; }

    public int DisconnectCalls { get; private set; }

    public Task<PanelDriverSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        SnapshotCalls++;
        return Task.FromResult(Snapshot);
    }

    public Task<PanelDriverCommandResult> ConnectAsync(
        CancellationToken cancellationToken)
    {
        ConnectCalls++;
        Snapshot = Snapshot with { Monitor = ConnectResult.Monitor };
        return Task.FromResult(ConnectResult);
    }

    public Task<PanelDriverCommandResult> DisconnectAsync(
        CancellationToken cancellationToken)
    {
        DisconnectCalls++;
        Snapshot = Snapshot with { Monitor = DisconnectResult.Monitor };
        return Task.FromResult(DisconnectResult);
    }
}

internal sealed class InMemoryStreamDeckConfigurationStore : IStreamDeckConfigurationStore
{
    public InMemoryStreamDeckConfigurationStore(
        StreamDeckConfiguration? configuration = null,
        StreamDeckConfigurationSource source = StreamDeckConfigurationSource.Loaded)
    {
        Configuration = configuration ?? StreamDeckDefaults.CreateConfiguration();
        Source = source;
    }

    public string Location => @"C:\PanelOS.Tests\streamdeck.json";

    public StreamDeckConfiguration Configuration { get; set; }

    public StreamDeckConfigurationSource Source { get; set; }

    public StreamDeckConfiguration? Saved { get; private set; }

    public int SaveCalls { get; private set; }

    public Task<StreamDeckConfigurationLoadResult> LoadAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new StreamDeckConfigurationLoadResult(
            Configuration,
            Source,
            Source == StreamDeckConfigurationSource.UnsupportedSchema
                ? "Schema v2 não é suportado."
                : null));

    public Task SaveAsync(
        StreamDeckConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var validation = StreamDeckValidator.Validate(configuration);
        if (!validation.IsValid)
        {
            throw new StreamDeckConfigurationException(
                "A configuração do Stream Deck é inválida e não foi salva: " +
                string.Join(" ", validation.Errors),
                validation.Errors);
        }

        SaveCalls++;
        Saved = configuration;
        return Task.CompletedTask;
    }
}

internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public FakeStartupRegistration(bool isSupported = true)
    {
        IsSupported = isSupported;
        UnsupportedReason = isSupported ? null : "Executável não encontrado.";
    }

    public bool IsSupported { get; }

    public string? UnsupportedReason { get; }

    public bool Enabled { get; set; }

    public Exception? Failure { get; set; }

    public bool IsEnabled() => Enabled;

    public void SetEnabled(bool enabled)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Enabled = enabled;
    }
}

internal sealed class InMemoryControlCenterSettingsStore : IControlCenterSettingsStore
{
    public string Location => @"C:\PanelOS.Tests\control-center.json";

    public ControlCenterSettings Settings { get; set; } = new();

    public ControlCenterSettings? Saved { get; private set; }

    public Task<ControlCenterSettings> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Settings);

    public Task SaveAsync(
        ControlCenterSettings settings,
        CancellationToken cancellationToken)
    {
        Saved = settings;
        return Task.CompletedTask;
    }
}
