using PanelOS.ControlCenter.Mvvm;
using PanelOS.ControlCenter.Services;
using PanelOS.Windows.StreamDeck;
using Strings = PanelOS.ControlCenter.Resources.ControlCenterStrings;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// One position of the editor grid. A cell always exists; the button in it may
/// not.
/// </summary>
public sealed class StreamDeckCellViewModel : ObservableObject
{
    private readonly IIconImporter? _icons;
    private StreamDeckButton? _button;
    private bool _isSelected;

    public StreamDeckCellViewModel(
        int row,
        int column,
        StreamDeckButton? button,
        IIconImporter? icons = null)
    {
        Row = row;
        Column = column;
        _button = button;
        _icons = icons;
    }

    public int Row { get; }

    public int Column { get; }

    public StreamDeckButton? Button
    {
        get => _button;
        set
        {
            _button = value;
            if (value is not null)
            {
                value.Row = Row;
                value.Column = Column;
            }

            Refresh();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsEmpty => _button is null;

    /// <summary>A configured button the user switched off.</summary>
    public bool IsDisabled => _button is { Enabled: false };

    public string Label => _button?.Label ?? "+";

    public string ActionSummary => _button is null
        ? Strings.Get("StreamDeckEmptyCell")
        : PanelActionDescriber.Describe(_button.Action);

    /// <summary>
    /// Cached PNG to preview, or null. Resolved from the icon id; the grid
    /// never reads the image the user originally picked.
    /// </summary>
    public string? IconPath => _icons?.ResolveIconPath(_button?.Icon.IconId);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Button));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsDisabled));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ActionSummary));
        OnPropertyChanged(nameof(IconPath));
    }
}
