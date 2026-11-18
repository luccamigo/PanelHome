using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

internal sealed class FakeVolumeService : IVolumeService
{
    private readonly List<int> _setRequests = [];

    public FakeVolumeService(VolumeStateMessage? state = null)
    {
        CurrentState = state ?? new VolumeStateMessage { Available = false };
    }

    public VolumeStateMessage CurrentState { get; private set; }

    public IReadOnlyList<int> SetRequests => _setRequests;

    public int ToggleMuteRequestCount { get; private set; }

    public event EventHandler<VolumeStateChangedEventArgs>? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> SetVolumeAsync(
        int volumePercent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _setRequests.Add(volumePercent);
        if (!CurrentState.Available)
        {
            return Task.FromResult(false);
        }

        Publish(CurrentState with
        {
            VolumePercent = Math.Clamp(volumePercent, 0, 100),
        });
        return Task.FromResult(true);
    }

    public Task<bool> ToggleMuteAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToggleMuteRequestCount++;
        if (!CurrentState.Available)
        {
            return Task.FromResult(false);
        }

        Publish(CurrentState with { IsMuted = !CurrentState.IsMuted });
        return Task.FromResult(true);
    }

    public void Publish(VolumeStateMessage state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, new VolumeStateChangedEventArgs(state));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
