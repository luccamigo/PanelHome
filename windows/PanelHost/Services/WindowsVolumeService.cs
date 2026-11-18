using System.Text;
using System.Threading.Channels;
using PanelHost.Protocol;

namespace PanelHost.Services;

public sealed class WindowsVolumeService : IVolumeService
{
    private static readonly Guid EventContext =
        new("0D474C4B-E047-4F4A-9B40-7969224DADEF");

    private readonly Func<ICoreAudioDeviceProvider> _providerFactory;
    private readonly TextWriter _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Channel<bool> _refreshRequests =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly object _stateLock = new();

    private ICoreAudioDeviceProvider? _provider;
    private ICoreAudioEndpoint? _endpoint;
    private VolumeStateMessage _currentState = CreateUnavailableState();
    private Task? _refreshWorker;
    private int _pendingRefreshReasons;
    private bool _initialized;
    private volatile bool _disposed;

    public WindowsVolumeService()
        : this(() => new CoreAudioDeviceProvider(), Console.Out)
    {
    }

    internal WindowsVolumeService(
        ICoreAudioDeviceProvider provider,
        TextWriter log)
        : this(() => provider, log)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    private WindowsVolumeService(
        Func<ICoreAudioDeviceProvider> providerFactory,
        TextWriter log)
    {
        _providerFactory = providerFactory
            ?? throw new ArgumentNullException(nameof(providerFactory));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public VolumeStateMessage CurrentState
    {
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    public event EventHandler<VolumeStateChangedEventArgs>? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            _provider = _providerFactory();
            _provider.DefaultRenderEndpointChanged += OnDefaultRenderEndpointChanged;
            _refreshWorker = ProcessRefreshRequestsAsync(
                _lifetimeCancellation.Token);
            RebindDefaultEndpoint();
            _initialized = true;
        }
        catch
        {
            DetachEndpoint();
            if (_provider is not null)
            {
                _provider.DefaultRenderEndpointChanged -=
                    OnDefaultRenderEndpointChanged;
                _provider.Dispose();
                _provider = null;
            }

            throw;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> SetVolumeAsync(
        int volumePercent,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = _endpoint;
            if (!_initialized || endpoint is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var clampedPercent = Math.Clamp(volumePercent, 0, 100);
            endpoint.SetVolumeScalar(clampedPercent / 100.0f, EventContext);
            Publish(endpoint.ReadState());
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleEndpointFailure("setting master volume", exception);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<bool> ToggleMuteAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var endpoint = _endpoint;
            if (!_initialized || endpoint is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = endpoint.ReadState();
            endpoint.SetMute(!state.IsMuted, EventContext);
            Publish(endpoint.ReadState());
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            HandleEndpointFailure("toggling endpoint mute", exception);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshRequests.Writer.TryComplete();
        await _lifetimeCancellation.CancelAsync();

        if (_refreshWorker is not null)
        {
            try
            {
                await _refreshWorker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _operationGate.WaitAsync(CancellationToken.None);
        try
        {
            DetachEndpoint();
            if (_provider is not null)
            {
                _provider.DefaultRenderEndpointChanged -=
                    OnDefaultRenderEndpointChanged;
                _provider.Dispose();
                _provider = null;
            }
        }
        finally
        {
            _operationGate.Release();
        }

        _lifetimeCancellation.Dispose();
        _operationGate.Dispose();
    }

    private async Task ProcessRefreshRequestsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (await _refreshRequests.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_refreshRequests.Reader.TryRead(out _))
                {
                }

                var reasons = (RefreshReason)Interlocked.Exchange(
                    ref _pendingRefreshReasons,
                    0);
                if (reasons == 0)
                {
                    continue;
                }

                await _operationGate.WaitAsync(cancellationToken);
                try
                {
                    if ((reasons & RefreshReason.Rebind) != 0)
                    {
                        RebindDefaultEndpoint();
                    }
                    else
                    {
                        RefreshEndpointState();
                    }
                }
                finally
                {
                    _operationGate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void RebindDefaultEndpoint()
    {
        DetachEndpoint();
        var provider = _provider;
        if (provider is null)
        {
            Publish(CreateUnavailableState());
            return;
        }

        ICoreAudioEndpoint? endpoint = null;
        try
        {
            endpoint = provider.OpenDefaultRenderEndpoint();
            if (endpoint is null)
            {
                Publish(CreateUnavailableState());
                return;
            }

            endpoint.VolumeChanged += OnEndpointVolumeChanged;
            _endpoint = endpoint;
            Publish(endpoint.ReadState());
        }
        catch (Exception exception)
        {
            if (endpoint is not null)
            {
                endpoint.VolumeChanged -= OnEndpointVolumeChanged;
                endpoint.Dispose();
            }

            _endpoint = null;
            WriteFailure("binding the default render endpoint", exception);
            Publish(CreateUnavailableState());
        }
    }

    private void RefreshEndpointState()
    {
        var endpoint = _endpoint;
        if (endpoint is null)
        {
            Publish(CreateUnavailableState());
            return;
        }

        try
        {
            Publish(endpoint.ReadState());
        }
        catch (Exception exception)
        {
            HandleEndpointFailure("refreshing endpoint volume", exception);
        }
    }

    private void HandleEndpointFailure(string operation, Exception exception)
    {
        WriteFailure(operation, exception);
        Publish(CreateUnavailableState());
        RequestRefresh(RefreshReason.Rebind);
    }

    private void DetachEndpoint()
    {
        var endpoint = _endpoint;
        _endpoint = null;
        if (endpoint is null)
        {
            return;
        }

        endpoint.VolumeChanged -= OnEndpointVolumeChanged;
        endpoint.Dispose();
    }

    private void OnEndpointVolumeChanged(object? sender, EventArgs eventArgs)
    {
        RequestRefresh(RefreshReason.State);
    }

    private void OnDefaultRenderEndpointChanged(object? sender, EventArgs eventArgs)
    {
        RequestRefresh(RefreshReason.Rebind);
    }

    private void RequestRefresh(RefreshReason reason)
    {
        if (_disposed)
        {
            return;
        }

        _ = Interlocked.Or(ref _pendingRefreshReasons, (int)reason);
        _refreshRequests.Writer.TryWrite(true);
    }

    private void Publish(CoreAudioVolumeSnapshot snapshot)
    {
        var volumePercent = Math.Clamp(
            (int)Math.Round(
                snapshot.VolumeScalar * 100.0f,
                MidpointRounding.AwayFromZero),
            0,
            100);
        Publish(new VolumeStateMessage
        {
            Available = true,
            VolumePercent = volumePercent,
            IsMuted = snapshot.IsMuted,
        });
    }

    private void Publish(VolumeStateMessage state)
    {
        lock (_stateLock)
        {
            if (_currentState == state)
            {
                return;
            }

            _currentState = state;
        }

        StateChanged?.Invoke(this, new VolumeStateChangedEventArgs(state));
    }

    private void WriteFailure(string operation, Exception exception)
    {
        var output = new StringBuilder()
            .Append("[Volume] ").Append(operation).AppendLine(" failed")
            .Append("Type: ").AppendLine(exception.GetType().Name)
            .Append("HRESULT: 0x")
            .Append(unchecked((uint)exception.HResult).ToString("X8"))
            .AppendLine()
            .Append("Message: ")
            .AppendLine(string.IsNullOrEmpty(exception.Message)
                ? "<empty>"
                : exception.Message);
#if DEBUG
        output.AppendLine("Stack trace:")
            .Append(exception.StackTrace ?? "<unavailable>");
#endif
        _log.WriteLine(output.ToString());
    }

    private static VolumeStateMessage CreateUnavailableState() => new()
    {
        Available = false,
        VolumePercent = 0,
        IsMuted = false,
    };

    [Flags]
    private enum RefreshReason
    {
        State = 1,
        Rebind = 2,
    }
}
