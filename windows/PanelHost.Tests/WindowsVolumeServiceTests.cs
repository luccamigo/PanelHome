using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class WindowsVolumeServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Available_endpoint_is_published_during_initialization()
    {
        var endpoint = new FakeCoreAudioEndpoint(0.72f, isMuted: false);
        var provider = new FakeCoreAudioDeviceProvider(endpoint);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);

        await service.InitializeAsync(CancellationToken.None);

        Assert.AreEqual(
            new VolumeStateMessage
            {
                Available = true,
                VolumePercent = 72,
                IsMuted = false,
            },
            service.CurrentState);
    }

    [TestMethod]
    public async Task Missing_default_endpoint_is_published_as_unavailable()
    {
        var provider = new FakeCoreAudioDeviceProvider(endpoint: null);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);

        await service.InitializeAsync(CancellationToken.None);

        Assert.IsFalse(service.CurrentState.Available);
        Assert.AreEqual(0, service.CurrentState.VolumePercent);
        Assert.IsFalse(service.CurrentState.IsMuted);
    }

    [TestMethod]
    public async Task Commands_report_unavailable_without_default_endpoint()
    {
        var provider = new FakeCoreAudioDeviceProvider(endpoint: null);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);

        var setSucceeded = await service.SetVolumeAsync(72, CancellationToken.None);
        var muteSucceeded = await service.ToggleMuteAsync(CancellationToken.None);

        Assert.IsFalse(setSucceeded);
        Assert.IsFalse(muteSucceeded);
        Assert.IsFalse(service.CurrentState.Available);
    }

    [DataTestMethod]
    [DataRow(0, 0.0f)]
    [DataRow(100, 1.0f)]
    [DataRow(-25, 0.0f)]
    [DataRow(125, 1.0f)]
    public async Task Set_volume_uses_normalized_clamped_scalar(
        int requestedPercent,
        float expectedScalar)
    {
        var endpoint = new FakeCoreAudioEndpoint(0.5f, isMuted: false);
        var provider = new FakeCoreAudioDeviceProvider(endpoint);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);

        var succeeded = await service.SetVolumeAsync(
            requestedPercent,
            CancellationToken.None);

        Assert.IsTrue(succeeded);
        Assert.AreEqual(1, endpoint.SetVolumeRequests.Count);
        Assert.AreEqual(expectedScalar, endpoint.SetVolumeRequests[0], 0.0001f);
        Assert.AreEqual((int)(expectedScalar * 100), service.CurrentState.VolumePercent);
    }

    [DataTestMethod]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task Toggle_mute_inverts_actual_endpoint_state(
        bool initiallyMuted,
        bool expectedMuted)
    {
        var endpoint = new FakeCoreAudioEndpoint(0.45f, initiallyMuted);
        var provider = new FakeCoreAudioDeviceProvider(endpoint);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);

        var succeeded = await service.ToggleMuteAsync(CancellationToken.None);

        Assert.IsTrue(succeeded);
        CollectionAssert.AreEqual(
            new[] { expectedMuted },
            endpoint.SetMuteRequests.ToArray());
        Assert.AreEqual(expectedMuted, service.CurrentState.IsMuted);
        Assert.AreEqual(45, service.CurrentState.VolumePercent);
    }

    [TestMethod]
    public async Task External_endpoint_callback_publishes_latest_windows_state()
    {
        var endpoint = new FakeCoreAudioEndpoint(0.40f, isMuted: false);
        var provider = new FakeCoreAudioDeviceProvider(endpoint);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);
        var updateTask = WaitForStateAsync(
            service,
            state => state.VolumePercent == 33 && state.IsMuted);

        endpoint.PublishExternal(0.33f, isMuted: true);
        var update = await updateTask;

        Assert.IsTrue(update.Available);
        Assert.AreEqual(33, update.VolumePercent);
        Assert.IsTrue(update.IsMuted);
    }

    [TestMethod]
    public async Task Default_endpoint_change_rebinds_and_releases_previous_endpoint()
    {
        var speakers = new FakeCoreAudioEndpoint(
            0.25f,
            isMuted: false,
            id: "speakers");
        var headset = new FakeCoreAudioEndpoint(
            0.81f,
            isMuted: true,
            id: "headset");
        var provider = new FakeCoreAudioDeviceProvider(speakers);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);
        var updateTask = WaitForStateAsync(
            service,
            state => state.VolumePercent == 81 && state.IsMuted);

        provider.Endpoint = headset;
        provider.RaiseDefaultRenderEndpointChanged();
        var update = await updateTask;

        Assert.AreEqual(2, provider.OpenCount);
        Assert.IsTrue(speakers.IsDisposed);
        Assert.IsFalse(headset.IsDisposed);
        Assert.AreEqual(81, update.VolumePercent);
        Assert.IsTrue(update.IsMuted);
    }

    [TestMethod]
    public async Task Endpoint_appearing_after_unavailable_state_is_rebound()
    {
        var provider = new FakeCoreAudioDeviceProvider(endpoint: null);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);
        var endpoint = new FakeCoreAudioEndpoint(0.64f, isMuted: false);
        var updateTask = WaitForStateAsync(
            service,
            state => state.Available && state.VolumePercent == 64);

        provider.Endpoint = endpoint;
        provider.RaiseDefaultRenderEndpointChanged();
        var update = await updateTask;

        Assert.IsTrue(update.Available);
        Assert.AreEqual(64, update.VolumePercent);
    }

    [TestMethod]
    public async Task Default_endpoint_removal_publishes_unavailable_state()
    {
        var endpoint = new FakeCoreAudioEndpoint(0.64f, isMuted: false);
        var provider = new FakeCoreAudioDeviceProvider(endpoint);
        await using var service = new WindowsVolumeService(provider, TextWriter.Null);
        await service.InitializeAsync(CancellationToken.None);
        var updateTask = WaitForStateAsync(service, state => !state.Available);

        provider.Endpoint = null;
        provider.RaiseDefaultRenderEndpointChanged();
        var update = await updateTask;

        Assert.IsFalse(update.Available);
        Assert.IsTrue(endpoint.IsDisposed);
    }

    private static async Task<VolumeStateMessage> WaitForStateAsync(
        WindowsVolumeService service,
        Func<VolumeStateMessage, bool> predicate)
    {
        if (predicate(service.CurrentState))
        {
            return service.CurrentState;
        }

        var completion = new TaskCompletionSource<VolumeStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnStateChanged(object? sender, VolumeStateChangedEventArgs eventArgs)
        {
            if (predicate(eventArgs.State))
            {
                completion.TrySetResult(eventArgs.State);
            }
        }

        service.StateChanged += OnStateChanged;
        try
        {
            return await completion.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            service.StateChanged -= OnStateChanged;
        }
    }

    private sealed class FakeCoreAudioDeviceProvider : ICoreAudioDeviceProvider
    {
        public FakeCoreAudioDeviceProvider(ICoreAudioEndpoint? endpoint)
        {
            Endpoint = endpoint;
        }

        public ICoreAudioEndpoint? Endpoint { get; set; }

        public int OpenCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler? DefaultRenderEndpointChanged;

        public ICoreAudioEndpoint? OpenDefaultRenderEndpoint()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            OpenCount++;
            return Endpoint;
        }

        public void RaiseDefaultRenderEndpointChanged()
        {
            DefaultRenderEndpointChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class FakeCoreAudioEndpoint : ICoreAudioEndpoint
    {
        private CoreAudioVolumeSnapshot _state;

        public FakeCoreAudioEndpoint(
            float volumeScalar,
            bool isMuted,
            string id = "default")
        {
            Id = id;
            _state = new CoreAudioVolumeSnapshot(volumeScalar, isMuted);
        }

        public string Id { get; }

        public List<float> SetVolumeRequests { get; } = [];

        public List<bool> SetMuteRequests { get; } = [];

        public bool IsDisposed { get; private set; }

        public event EventHandler? VolumeChanged;

        public CoreAudioVolumeSnapshot ReadState()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _state;
        }

        public void SetVolumeScalar(float value, Guid eventContext)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            SetVolumeRequests.Add(value);
            _state = _state with { VolumeScalar = value };
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetMute(bool isMuted, Guid eventContext)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            SetMuteRequests.Add(isMuted);
            _state = _state with { IsMuted = isMuted };
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void PublishExternal(float volumeScalar, bool isMuted)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _state = new CoreAudioVolumeSnapshot(volumeScalar, isMuted);
            VolumeChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
