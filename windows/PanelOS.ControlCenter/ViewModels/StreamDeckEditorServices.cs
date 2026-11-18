using PanelOS.ControlCenter.Services;
using PanelOS.Windows.StreamDeck;

namespace PanelOS.ControlCenter.ViewModels;

/// <summary>
/// Everything the Stream Deck editor needs from the outside world. Grouping it
/// keeps the view-model constructors short and lets a test supply only the
/// pieces the case under test actually exercises.
/// </summary>
public sealed record StreamDeckEditorServices
{
    public IApplicationPicker? ApplicationPicker { get; init; }

    public IIconFilePicker? IconPicker { get; init; }

    public IIconImporter? IconImporter { get; init; }

    public IConfirmationService? Confirmation { get; init; }

    public IStreamDeckActionExecutor? ActionExecutor { get; init; }

    public static StreamDeckEditorServices None { get; } = new();
}
