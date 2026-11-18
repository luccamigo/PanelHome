using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

internal sealed class FakeMediaService : IMediaService
{
    private readonly List<(MediaCommand Command, bool Succeeded)> _commandResults = [];

    public FakeMediaService(
        MediaStateMessage? state = null,
        MediaArtworkMessage? artwork = null)
    {
        CurrentState = state ?? new MediaStateMessage { Available = false };
        CurrentArtwork = artwork ?? new MediaArtworkMessage { Available = false };
    }

    public MediaStateMessage CurrentState { get; private set; }

    public MediaArtworkMessage CurrentArtwork { get; private set; }

    public IReadOnlyList<(MediaCommand Command, bool Succeeded)> CommandResults =>
        _commandResults;

    public event EventHandler<MediaStateChangedEventArgs>? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> ExecuteCommandAsync(
        MediaCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var succeeded = CurrentState.Available && command switch
        {
            MediaCommand.PlayPause => CurrentState.CanPlayPause,
            MediaCommand.Previous => CurrentState.CanPrevious,
            MediaCommand.Next => CurrentState.CanNext,
            _ => false,
        };

        _commandResults.Add((command, succeeded));
        return Task.FromResult(succeeded);
    }

    public void Publish(
        MediaStateMessage state,
        MediaArtworkMessage? artwork = null,
        bool artworkChanged = false)
    {
        CurrentState = state;
        if (artwork is not null)
        {
            CurrentArtwork = artwork;
        }

        StateChanged?.Invoke(
            this,
            new MediaStateChangedEventArgs(
                CurrentState,
                CurrentArtwork,
                artworkChanged));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
