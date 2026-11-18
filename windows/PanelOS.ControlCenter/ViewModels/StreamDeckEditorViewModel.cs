using System.Collections.ObjectModel;
using PanelOS.ControlCenter.Mvvm;
using PanelOS.Windows.StreamDeck;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// What the Dashboard shows about the Stream Deck. Every number is counted from
/// the configuration that is actually persisted, never from unsaved edits.
/// </summary>
public sealed record StreamDeckSummary(
    string ProfileName,
    int PageCount,
    int ActionCount,
    string Revision)
{
    public static StreamDeckSummary Empty { get; } = new(
        Strings.Get("NotAvailableSymbol"),
        0,
        0,
        StreamDeckLayoutRevision.None);

    public bool HasProfile => PageCount > 0;

    public string Describe() => HasProfile
        ? Strings.Format(
            "StreamDeckSummaryFormat",
            ProfileName,
            Plural(PageCount, "StreamDeckPageSingular", "StreamDeckPagePlural"),
            Plural(ActionCount, "StreamDeckActionSingular", "StreamDeckActionPlural"))
        : Strings.Get("StreamDeckNoProfileConfigured");

    private static string Plural(int count, string singular, string plural) =>
        Strings.Format(count == 1 ? singular : plural, count);
}

/// <summary>
/// What "Testar ação" is doing right now. The result is shown beside the
/// button rather than in a dialog: running a shortcut is not an event that
/// deserves to interrupt the person configuring it.
/// </summary>
public enum StreamDeckTestStatus
{
    Ready,
    Running,
    Success,
    Error,
}

/// <summary>
/// Visual editor for the Stream Deck: profiles, pages, a configurable grid, a
/// button editor and a "test action" that runs the selected button on this PC.
/// Reordering uses Move left/right/up/down rather than drag and drop - it is
/// the same operation on the model with far less surface to get wrong.
/// </summary>
/// <remarks>
/// Selecting a profile and choosing the one the tablet shows are two different
/// decisions. Browsing another profile in the editor must not silently change
/// what the panel on the desk is displaying, so the active profile is moved
/// only by "Usar no tablet", which persists immediately.
/// </remarks>
public sealed class StreamDeckEditorViewModel : ObservableObject
{
    private readonly IStreamDeckConfigurationStore _store;
    private readonly StreamDeckEditorServices _services;

    private StreamDeckConfiguration _configuration =
        StreamDeckDefaults.CreateConfiguration();
    private StreamDeckProfile? _selectedProfile;
    private StreamDeckPage? _selectedPage;
    private StreamDeckCellViewModel? _selectedCell;
    private StreamDeckButtonEditorViewModel? _buttonEditor;
    private bool _isDirty;
    private bool _isReadOnly;
    private string? _statusMessage;
    private string? _testStatusMessage;
    private PanelStatusLevel _testStatusLevel = PanelStatusLevel.Neutral;
    private StreamDeckTestStatus _testStatus = StreamDeckTestStatus.Ready;
    private bool _isApplyingGridSize;
    private bool _isSwitchingSelection;

    public StreamDeckEditorViewModel(
        IStreamDeckConfigurationStore store,
        StreamDeckEditorServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _services = services ?? StreamDeckEditorServices.None;

        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            () => IsDirty && !IsReadOnly,
            ReportFailure);
        ReloadCommand = new AsyncRelayCommand(LoadAsync, onError: ReportFailure);
        TestActionCommand = new AsyncRelayCommand(
            TestActionAsync,
            () => _services.ActionExecutor is not null &&
                SelectedCell is { IsEmpty: false },
            ReportFailure);

        AddButtonCommand = new RelayCommand(
            AddButton,
            () => CanEdit && SelectedCell is { IsEmpty: true });
        RemoveButtonCommand = new RelayCommand(
            RemoveButton,
            () => CanEdit && SelectedCell is { IsEmpty: false });
        MoveLeftCommand = new RelayCommand(() => Move(0, -1), () => CanMove(0, -1));
        MoveRightCommand = new RelayCommand(() => Move(0, 1), () => CanMove(0, 1));
        MoveUpCommand = new RelayCommand(() => Move(-1, 0), () => CanMove(-1, 0));
        MoveDownCommand = new RelayCommand(() => Move(1, 0), () => CanMove(1, 0));

        SetActiveProfileCommand = new AsyncRelayCommand(
            SetActiveProfileAsync,
            () => CanEdit && _selectedProfile is not null && !IsSelectedProfileActive,
            ReportFailure);

        AddProfileCommand = new RelayCommand(AddProfile, () => CanEdit);
        DuplicateProfileCommand = new RelayCommand(
            DuplicateProfile,
            () => CanEdit && _selectedProfile is not null);
        DeleteProfileCommand = new RelayCommand(
            DeleteProfile,
            () => CanEdit && _configuration.Profiles.Count > 1);

        AddPageCommand = new RelayCommand(
            AddPage,
            () => CanEdit && _selectedProfile is not null);
        DuplicatePageCommand = new RelayCommand(
            DuplicatePage,
            () => CanEdit && _selectedPage is not null);
        DeletePageCommand = new RelayCommand(
            DeletePage,
            () => CanEdit && Pages.Count > 1);
    }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand ReloadCommand { get; }

    public AsyncRelayCommand TestActionCommand { get; }

    /// <summary>"Usar no tablet": makes the selected profile the active one.</summary>
    public AsyncRelayCommand SetActiveProfileCommand { get; }

    public RelayCommand AddButtonCommand { get; }

    public RelayCommand RemoveButtonCommand { get; }

    public RelayCommand MoveLeftCommand { get; }

    public RelayCommand MoveRightCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand AddProfileCommand { get; }

    public RelayCommand DuplicateProfileCommand { get; }

    public RelayCommand DeleteProfileCommand { get; }

    public RelayCommand AddPageCommand { get; }

    public RelayCommand DuplicatePageCommand { get; }

    public RelayCommand DeletePageCommand { get; }

    /// <summary>Raised when the persisted configuration changes.</summary>
    public event EventHandler? SummaryChanged;

    /// <summary>Last persisted state, for the Dashboard card.</summary>
    public StreamDeckSummary Summary { get; private set; } = StreamDeckSummary.Empty;

    public ObservableCollection<StreamDeckProfile> Profiles { get; } = [];

    public ObservableCollection<StreamDeckPage> Pages { get; } = [];

    public ObservableCollection<StreamDeckCellViewModel> Cells { get; } = [];

    /// <summary>Grid sizes the validator accepts, for the row/column pickers.</summary>
    public IReadOnlyList<int> GridSizeOptions { get; } =
    [
        .. Enumerable.Range(
            StreamDeckValidator.MinimumRows,
            StreamDeckValidator.MaximumRows - StreamDeckValidator.MinimumRows + 1),
    ];

    public string ConfigurationLocation => _store.Location;

    public StreamDeckConfiguration Configuration => _configuration;

    public bool CanEdit => !IsReadOnly;

    public StreamDeckProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (ReferenceEquals(_selectedProfile, value) || _isSwitchingSelection)
            {
                return;
            }

            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileName));
            OnPropertyChanged(nameof(IsSelectedProfileActive));
            RebuildPages();
            RaiseCommandStates();
        }
    }

    /// <summary>Profile PanelHost publishes to the tablet.</summary>
    public string? ActiveProfileId => _configuration.FindActiveProfile()?.Id;

    public string ActiveProfileName =>
        _configuration.FindActiveProfile()?.Name ?? Strings.Get("NotAvailableSymbol");

    /// <summary>Whether the profile being edited is the one on the tablet.</summary>
    public bool IsSelectedProfileActive =>
        _selectedProfile is not null &&
        string.Equals(_selectedProfile.Id, ActiveProfileId, StringComparison.Ordinal);

    /// <summary>
    /// Identity of the configuration as PanelHost would publish it. Shown so an
    /// operator can tell at a glance whether a save actually changed anything
    /// the tablet can observe.
    /// </summary>
    public string LayoutRevision => StreamDeckLayoutRevision.Compute(_configuration);

    /// <summary>Editable name of the selected profile.</summary>
    public string ProfileName
    {
        get => _selectedProfile?.Name ?? string.Empty;
        set
        {
            if (_selectedProfile is null ||
                string.Equals(_selectedProfile.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedProfile.Name = value ?? string.Empty;
            OnPropertyChanged();
            RefreshEntry(Profiles, _selectedProfile, item => SelectedProfile = item);
            MarkDirty();
        }
    }

    public StreamDeckPage? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (ReferenceEquals(_selectedPage, value) || _isSwitchingSelection)
            {
                return;
            }

            _selectedPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PageName));
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Columns));
            RebuildCells();
            RaiseCommandStates();
        }
    }

    /// <summary>Editable name of the page being edited.</summary>
    public string PageName
    {
        get => _selectedPage?.Name ?? string.Empty;
        set
        {
            if (_selectedPage is null ||
                string.Equals(_selectedPage.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            _selectedPage.Name = value ?? string.Empty;
            OnPropertyChanged();
            RefreshEntry(Pages, _selectedPage, item => SelectedPage = item);
            MarkDirty();
        }
    }

    public StreamDeckCellViewModel? SelectedCell
    {
        get => _selectedCell;
        set
        {
            if (ReferenceEquals(_selectedCell, value))
            {
                return;
            }

            if (_selectedCell is not null)
            {
                _selectedCell.IsSelected = false;
            }

            _selectedCell = value;
            if (_selectedCell is not null)
            {
                _selectedCell.IsSelected = true;
            }

            TestStatusMessage = null;
            TestStatus = StreamDeckTestStatus.Ready;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedCell));
            OnPropertyChanged(nameof(IsSelectedCellEmpty));
            UpdateButtonEditor();
            RaiseCommandStates();
        }
    }

    public StreamDeckButtonEditorViewModel? ButtonEditor
    {
        get => _buttonEditor;
        private set
        {
            if (SetProperty(ref _buttonEditor, value))
            {
                OnPropertyChanged(nameof(HasSelectedButton));
            }
        }
    }

    public int Rows
    {
        get => _selectedPage?.Rows ?? StreamDeckPage.DefaultRows;
        set => ApplyGridSize(value, Columns);
    }

    public int Columns
    {
        get => _selectedPage?.Columns ?? StreamDeckPage.DefaultColumns;
        set => ApplyGridSize(Rows, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(SaveStateText));
                OnPropertyChanged(nameof(SaveStateLevel));
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True when the stored file was written by a newer schema. The editor
    /// still shows the defaults, but saving is refused so the real file is not
    /// destroyed by this build.
    /// </summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set
        {
            if (SetProperty(ref _isReadOnly, value))
            {
                OnPropertyChanged(nameof(CanEdit));
                RaiseCommandStates();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Result of the last "test action", shown next to the button.</summary>
    public string? TestStatusMessage
    {
        get => _testStatusMessage;
        private set => SetProperty(ref _testStatusMessage, value);
    }

    public PanelStatusLevel TestStatusLevel
    {
        get => _testStatusLevel;
        private set => SetProperty(ref _testStatusLevel, value);
    }

    public StreamDeckTestStatus TestStatus
    {
        get => _testStatus;
        private set => SetProperty(ref _testStatus, value);
    }

    /// <summary>Whether the right column has a button to edit.</summary>
    public bool HasSelectedButton => _buttonEditor is not null;

    /// <summary>A slot is selected, whether or not it holds a button.</summary>
    public bool HasSelectedCell => _selectedCell is not null;

    /// <summary>A slot is selected and empty, so a button can be created in it.</summary>
    public bool IsSelectedCellEmpty => _selectedCell is { IsEmpty: true };

    /// <summary>
    /// Whether the file on disk matches the editor. Only a saved configuration
    /// is one PanelHost can publish, so the header says which of the two the
    /// tablet is currently getting.
    /// </summary>
    public string SaveStateText => IsDirty
        ? Strings.Get("StreamDeckUnsavedChanges")
        : Strings.Get("StreamDeckSaved");

    public PanelStatusLevel SaveStateLevel => IsDirty
        ? PanelStatusLevel.Warning
        : PanelStatusLevel.Good;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var result = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _configuration = result.Configuration;
        IsReadOnly = !result.CanSave;
        StatusMessage = result.Source switch
        {
            StreamDeckConfigurationSource.Defaults =>
                Strings.Get("StreamDeckDefaultsLoaded"),
            StreamDeckConfigurationSource.Loaded => null,
            StreamDeckConfigurationSource.RecoveredFromInvalidFile =>
                Strings.Format("StreamDeckRecoveredFormat", result.Detail),
            StreamDeckConfigurationSource.UnsupportedSchema =>
                Strings.Format("StreamDeckUnsupportedFormat", result.Detail),
            _ => result.Detail,
        };

        TestStatusMessage = null;
        RebuildProfiles(_configuration.FindActiveProfile());
        IsDirty = false;
        PublishSummary();
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(_configuration, cancellationToken)
                .ConfigureAwait(true);
            IsDirty = false;
            StatusMessage = Strings.Format("StreamDeckSavedFormat", _store.Location);

            // Only a persisted configuration is one PanelHost can publish, so
            // the summary and the revision follow the save, not the edit.
            PublishSummary();
        }
        catch (StreamDeckConfigurationException exception)
        {
            StatusMessage = exception.Message;
        }
    }

    /// <summary>
    /// Points the tablet at the profile being edited and persists it at once,
    /// so the choice survives even if the editor is closed without saving.
    /// </summary>
    private async Task SetActiveProfileAsync(CancellationToken cancellationToken)
    {
        if (_selectedProfile is not { } profile || IsReadOnly)
        {
            return;
        }

        var previous = _configuration.ActiveProfileId;
        _configuration.ActiveProfileId = profile.Id;
        try
        {
            await _store.SaveAsync(_configuration, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (StreamDeckConfigurationException exception)
        {
            _configuration.ActiveProfileId = previous;
            StatusMessage = exception.Message;
            return;
        }

        IsDirty = false;
        StatusMessage = Strings.Format(
            "StreamDeckActiveProfileSetFormat",
            profile.Name);
        PublishActiveProfile();
        PublishSummary();
    }

    /// <summary>
    /// Runs the selected button here on the PC. It executes the in-memory
    /// button, so a change can be tried before it is saved.
    /// </summary>
    private async Task TestActionAsync(CancellationToken cancellationToken)
    {
        if (SelectedCell?.Button is not { } button ||
            _services.ActionExecutor is not { } executor)
        {
            return;
        }

        TestStatusMessage = Strings.Get("StreamDeckTestRunning");
        TestStatusLevel = PanelStatusLevel.Neutral;
        TestStatus = StreamDeckTestStatus.Running;
        var result = await executor
            .ExecuteButtonAsync(button, cancellationToken)
            .ConfigureAwait(true);
        TestStatusMessage = result.Success
            ? Strings.Get("StreamDeckTestSucceeded")
            : result.Message;
        TestStatusLevel = result.Success
            ? PanelStatusLevel.Good
            : PanelStatusLevel.Bad;
        TestStatus = result.Success
            ? StreamDeckTestStatus.Success
            : StreamDeckTestStatus.Error;
    }

    private void AddProfile()
    {
        var profile = new StreamDeckProfile
        {
            Id = StreamDeckCloner.NewProfileId(),
            Name = Strings.Get("StreamDeckNewProfile"),
            Pages = [CreatePage(Strings.Get("StreamDeckFirstPage"))],
        };
        _configuration.Profiles.Add(profile);
        Profiles.Add(profile);
        SelectProfile(profile);
        MarkDirty();
    }

    private void DuplicateProfile()
    {
        if (_selectedProfile is not { } profile)
        {
            return;
        }

        var copy = StreamDeckCloner.CloneProfile(
            profile,
            Strings.Format("StreamDeckCopySuffixFormat", profile.Name));
        _configuration.Profiles.Add(copy);
        Profiles.Add(copy);
        SelectProfile(copy);
        MarkDirty();
    }

    private void DeleteProfile()
    {
        if (_selectedProfile is not { } profile ||
            _configuration.Profiles.Count <= 1)
        {
            // The configuration must always keep one usable profile.
            return;
        }

        if (!Confirm(
                Strings.Get("StreamDeckDeleteProfileTitle"),
                Strings.Format("StreamDeckDeleteProfileMessage", profile.Name)))
        {
            return;
        }

        _configuration.Profiles.Remove(profile);
        Profiles.Remove(profile);
        if (string.Equals(
                _configuration.ActiveProfileId,
                profile.Id,
                StringComparison.Ordinal))
        {
            // The tablet cannot be left pointing at a profile that is gone.
            _configuration.ActiveProfileId = _configuration.Profiles[0].Id;
        }

        SelectProfile(_configuration.Profiles[0]);
        PublishActiveProfile();
        MarkDirty();
    }

    private void AddPage()
    {
        if (_selectedProfile is not { } profile)
        {
            return;
        }

        var page = CreatePage(Strings.Get("StreamDeckNewPage"));
        profile.Pages.Add(page);
        Pages.Add(page);
        SelectPage(page);
        MarkDirty();
    }

    private void DuplicatePage()
    {
        if (_selectedProfile is not { } profile || _selectedPage is not { } page)
        {
            return;
        }

        var copy = StreamDeckCloner.ClonePage(
            page,
            Strings.Format("StreamDeckCopySuffixFormat", page.Name));
        profile.Pages.Add(copy);
        Pages.Add(copy);
        SelectPage(copy);
        MarkDirty();
    }

    private void DeletePage()
    {
        if (_selectedProfile is not { } profile ||
            _selectedPage is not { } page ||
            profile.Pages.Count <= 1)
        {
            return;
        }

        if (!Confirm(
                Strings.Get("StreamDeckDeletePageTitle"),
                Strings.Format("StreamDeckDeletePageMessage", page.Name)))
        {
            return;
        }

        profile.Pages.Remove(page);
        Pages.Remove(page);
        SelectPage(profile.Pages[0]);
        MarkDirty();
    }

    private static StreamDeckPage CreatePage(string name) => new()
    {
        Id = StreamDeckCloner.NewPageId(),
        Name = name,
        Rows = StreamDeckPage.DefaultRows,
        Columns = StreamDeckPage.DefaultColumns,
    };

    private bool Confirm(string title, string message) =>
        _services.Confirmation?.Confirm(title, message) ?? false;

    private void AddButton()
    {
        if (SelectedCell is not { IsEmpty: true } cell || _selectedPage is null)
        {
            return;
        }

        var button = new StreamDeckButton
        {
            Id = StreamDeckCloner.NewButtonId(),
            Row = cell.Row,
            Column = cell.Column,
            Label = Strings.Get("StreamDeckNewButton"),
        };
        _selectedPage.Buttons.Add(button);
        cell.Button = button;
        OnPropertyChanged(nameof(IsSelectedCellEmpty));
        UpdateButtonEditor();
        MarkDirty();
        RaiseCommandStates();
    }

    private void RemoveButton()
    {
        if (SelectedCell is not { Button: { } button } cell || _selectedPage is null)
        {
            return;
        }

        _selectedPage.Buttons.Remove(button);
        cell.Button = null;
        OnPropertyChanged(nameof(IsSelectedCellEmpty));
        UpdateButtonEditor();
        MarkDirty();
        RaiseCommandStates();
    }

    private bool CanMove(int rowDelta, int columnDelta) =>
        CanEdit &&
        SelectedCell is { IsEmpty: false } cell &&
        FindCell(cell.Row + rowDelta, cell.Column + columnDelta) is not null;

    private void Move(int rowDelta, int columnDelta)
    {
        if (SelectedCell is not { IsEmpty: false } source ||
            FindCell(source.Row + rowDelta, source.Column + columnDelta)
                is not { } target)
        {
            return;
        }

        // Swap: moving onto an occupied cell trades places, moving onto an
        // empty one leaves an empty cell behind.
        (source.Button, target.Button) = (target.Button, source.Button);
        SelectedCell = target;
        MarkDirty();
        RaiseCommandStates();
    }

    private StreamDeckCellViewModel? FindCell(int row, int column) =>
        Cells.FirstOrDefault(cell => cell.Row == row && cell.Column == column);

    private void ApplyGridSize(int rows, int columns)
    {
        if (_selectedPage is null || _isApplyingGridSize)
        {
            return;
        }

        if (_selectedPage.Rows == rows && _selectedPage.Columns == columns)
        {
            return;
        }

        if (!CanEdit)
        {
            RevertGridSize();
            return;
        }

        if (!StreamDeckValidator.IsGridSizeValid(rows, columns))
        {
            StatusMessage = Strings.Format(
                "StreamDeckGridRangeFormat",
                StreamDeckValidator.MinimumRows,
                StreamDeckValidator.MaximumRows,
                StreamDeckValidator.MinimumColumns,
                StreamDeckValidator.MaximumColumns);
            RevertGridSize();
            return;
        }

        // Shrinking never deletes a button: the user is asked to move or remove
        // it first, so no configuration disappears behind their back.
        var displaced = _selectedPage.Buttons
            .Where(button => button.Row >= rows || button.Column >= columns)
            .ToList();
        if (displaced.Count > 0)
        {
            StatusMessage = Strings.Format(
                "StreamDeckDisplacedFormat",
                displaced.Count);
            RevertGridSize();
            return;
        }

        _selectedPage.Rows = rows;
        _selectedPage.Columns = columns;
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
        RebuildCells();
        MarkDirty();
    }

    private void RevertGridSize()
    {
        // The bound control already moved; put it back without recursing.
        _isApplyingGridSize = true;
        try
        {
            OnPropertyChanged(nameof(Rows));
            OnPropertyChanged(nameof(Columns));
        }
        finally
        {
            _isApplyingGridSize = false;
        }
    }

    private void RebuildProfiles(StreamDeckProfile? selected)
    {
        _isSwitchingSelection = true;
        try
        {
            Profiles.Clear();
            foreach (var profile in _configuration.Profiles)
            {
                Profiles.Add(profile);
            }

            _selectedProfile = selected ?? Profiles.FirstOrDefault();
        }
        finally
        {
            _isSwitchingSelection = false;
        }

        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProfileName));
        PublishActiveProfile();
        RebuildPages();
        RaiseCommandStates();
    }

    private void RebuildPages()
    {
        _isSwitchingSelection = true;
        try
        {
            Pages.Clear();
            foreach (var page in _selectedProfile?.Pages ?? [])
            {
                Pages.Add(page);
            }

            _selectedPage = Pages.FirstOrDefault();
        }
        finally
        {
            _isSwitchingSelection = false;
        }

        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(PageName));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
        RebuildCells();
    }

    private void RebuildCells()
    {
        Cells.Clear();
        _selectedCell = null;
        if (_selectedPage is null)
        {
            ButtonEditor = null;
            RaiseCommandStates();
            return;
        }

        for (var row = 0; row < _selectedPage.Rows; row++)
        {
            for (var column = 0; column < _selectedPage.Columns; column++)
            {
                Cells.Add(new StreamDeckCellViewModel(
                    row,
                    column,
                    _selectedPage.FindButtonAt(row, column),
                    _services.IconImporter));
            }
        }

        OnPropertyChanged(nameof(SelectedCell));
        SelectedCell = Cells.FirstOrDefault();
    }

    private void SelectProfile(StreamDeckProfile profile)
    {
        _selectedProfile = profile;
        OnPropertyChanged(nameof(SelectedProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(IsSelectedProfileActive));
        RebuildPages();
        RaiseCommandStates();
    }

    private void SelectPage(StreamDeckPage page)
    {
        _selectedPage = page;
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(PageName));
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(Columns));
        RebuildCells();
        RaiseCommandStates();
    }

    /// <summary>
    /// Re-publishes one entry so a renamed profile or page updates in its combo
    /// box. The domain objects stay free of change notification.
    /// </summary>
    private void RefreshEntry<T>(
        ObservableCollection<T> collection,
        T item,
        Action<T> restoreSelection)
        where T : class
    {
        var index = collection.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        _isSwitchingSelection = true;
        try
        {
            collection[index] = item;
        }
        finally
        {
            _isSwitchingSelection = false;
        }

        restoreSelection(item);
    }

    private void UpdateButtonEditor()
    {
        if (ButtonEditor is not null)
        {
            ButtonEditor.Changed -= OnButtonEdited;
        }

        if (SelectedCell?.Button is not { } button)
        {
            ButtonEditor = null;
            return;
        }

        var editor = new StreamDeckButtonEditorViewModel(button, _services);
        editor.Changed += OnButtonEdited;
        ButtonEditor = editor;
    }

    private void OnButtonEdited(object? sender, EventArgs e)
    {
        SelectedCell?.Refresh();
        MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        if (StatusMessage is not null && !IsReadOnly)
        {
            StatusMessage = null;
        }
    }

    private void PublishActiveProfile()
    {
        OnPropertyChanged(nameof(ActiveProfileId));
        OnPropertyChanged(nameof(ActiveProfileName));
        OnPropertyChanged(nameof(IsSelectedProfileActive));
        SetActiveProfileCommand.RaiseCanExecuteChanged();
    }

    private void PublishSummary()
    {
        var profile = _configuration.FindActiveProfile();
        Summary = profile is null
            ? StreamDeckSummary.Empty
            : new StreamDeckSummary(
                profile.Name,
                profile.Pages.Count,
                profile.Pages
                    .SelectMany(page => page.Buttons)
                    .Count(button => button.IsExecutable),
                LayoutRevision);
        OnPropertyChanged(nameof(LayoutRevision));
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCommandStates()
    {
        AddButtonCommand.RaiseCanExecuteChanged();
        SetActiveProfileCommand.RaiseCanExecuteChanged();
        RemoveButtonCommand.RaiseCanExecuteChanged();
        MoveLeftCommand.RaiseCanExecuteChanged();
        MoveRightCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        TestActionCommand.RaiseCanExecuteChanged();
        AddProfileCommand.RaiseCanExecuteChanged();
        DuplicateProfileCommand.RaiseCanExecuteChanged();
        DeleteProfileCommand.RaiseCanExecuteChanged();
        AddPageCommand.RaiseCanExecuteChanged();
        DuplicatePageCommand.RaiseCanExecuteChanged();
        DeletePageCommand.RaiseCanExecuteChanged();
    }

    private void ReportFailure(Exception exception) =>
        StatusMessage = Strings.Format(
            "UnexpectedFailure",
            exception.GetType().Name,
            exception.Message);
}
