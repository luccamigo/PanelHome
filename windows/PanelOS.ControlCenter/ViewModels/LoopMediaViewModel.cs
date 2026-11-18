using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.LoopMedia;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

public sealed class LoopMediaViewModel : ObservableObject
{
    private readonly JsonLoopMediaStore _store;
    private readonly ILoopMediaFilePicker _picker;
    private LoopMediaConfiguration _current = new();
    private string _error = string.Empty;
    private bool _busy;

    public LoopMediaViewModel(JsonLoopMediaStore store, ILoopMediaFilePicker picker)
    {
        _store = store;
        _picker = picker;
        ChooseCommand = new AsyncRelayCommand(ChooseAsync, () => !_busy,
            _ => Error = LoopMediaValidator.Unreadable);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync,
            () => !_busy && _current.MediaId is not null,
            _ => Error = LoopMediaValidator.Unreadable);
    }

    public AsyncRelayCommand ChooseCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public string FileName => _current.OriginalFileName ?? Strings.Get("LoopMediaNone");
    public string Details => _current.MediaId is null ? string.Empty :
        $"{(_current.Kind == "gif" ? "GIF" : "MP4")} · {_current.SizeBytes:N0} bytes";
    public string Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public async Task LoadAsync(CancellationToken cancellationToken) =>
        Apply(await _store.LoadAsync(cancellationToken).ConfigureAwait(true));

    private Task ChooseAsync(CancellationToken cancellationToken)
    {
        var path = _picker.PickFile();
        return path is null ? Task.CompletedTask : ImportAsync(path, cancellationToken);
    }

    public Task ImportAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async () => Apply(await _store.ImportAsync(path, cancellationToken)
            .ConfigureAwait(true)));

    public Task RemoveAsync(CancellationToken cancellationToken) => RunAsync(async () =>
    {
        await _store.RemoveAsync(cancellationToken).ConfigureAwait(true);
        Apply(new());
    });

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        RefreshCommands();
        Error = string.Empty;
        try { await operation().ConfigureAwait(true); }
        catch (InvalidDataException exception)
        {
            Error = exception.Message == LoopMediaValidator.TooLarge
                ? LoopMediaValidator.TooLarge : LoopMediaValidator.UnsupportedFormat;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = LoopMediaValidator.Unreadable;
        }
        finally
        {
            _busy = false;
            RefreshCommands();
        }
    }

    private void Apply(LoopMediaConfiguration configuration)
    {
        _current = configuration;
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(Details));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ChooseCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
    }
}
