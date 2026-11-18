using System.Windows;
using System.Windows.Automation;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Controls;

/// <summary>
/// The one way PanelOS shows the state of something: a mark whose shape and
/// colour carry the severity, an optional caption naming the component, and a
/// word from a fixed vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// Every screen used to assemble this by hand, which is how "Instalado",
/// "conectado" and "Em execução" ended up in three different casings on three
/// different screens. The badge takes a level and a text, so the shape and the
/// colour are decided once and the view model still never mentions a colour.
/// </para>
/// <para>
/// It is a UserControl rather than a templated Control on purpose: a templated
/// control resolves its default style from Themes/Generic.xaml, which this
/// application does not ship, and a badge that silently renders nothing is
/// worse than one with a fixed layout.
/// </para>
/// </remarks>
public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(PanelStatusLevel),
            typeof(StatusBadge),
            new PropertyMetadata(PanelStatusLevel.Neutral, OnContentChanged));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(string.Empty, OnContentChanged));

    /// <summary>
    /// Name of what the state belongs to, for a badge that stands on its own -
    /// "Tablet · Conectado" in the header strip, where the surrounding card is
    /// not there to say which component this is.
    /// </summary>
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(
            nameof(Caption),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(null, OnContentChanged));

    /// <summary>Draws the badge as a filled chip instead of an inline line.</summary>
    public static readonly DependencyProperty IsChipProperty =
        DependencyProperty.Register(
            nameof(IsChip),
            typeof(bool),
            typeof(StatusBadge),
            new PropertyMetadata(false));

    public StatusBadge() => InitializeComponent();

    public PanelStatusLevel Level
    {
        get => (PanelStatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string? Caption
    {
        get => (string?)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public bool IsChip
    {
        get => (bool)GetValue(IsChipProperty);
        set => SetValue(IsChipProperty, value);
    }

    /// <summary>
    /// Keeps the accessible name in step with what is drawn. A screen reader
    /// otherwise announces an unnamed group of two shapes.
    /// </summary>
    private static void OnContentChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not StatusBadge badge)
        {
            return;
        }

        var caption = badge.Caption;
        AutomationProperties.SetName(
            badge,
            string.IsNullOrWhiteSpace(caption)
                ? badge.Text ?? string.Empty
                : $"{caption}: {badge.Text}");
    }
}
