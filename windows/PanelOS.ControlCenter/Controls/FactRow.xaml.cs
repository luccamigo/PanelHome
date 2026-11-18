using System.Windows;
using System.Windows.Automation;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Controls;

/// <summary>
/// One measured fact: a label, its value, and optionally a status mark and a
/// line of guidance under it.
/// </summary>
/// <remarks>
/// Three pages show lists of these - the device, the diagnostics and the file
/// locations in settings - and each of them used to lay out its own two-column
/// grid. Doing it once means the label column, the wrapping and the mark are
/// the same everywhere, and a row can gain a hint without three views changing.
/// </remarks>
public partial class FactRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(FactRow),
            new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(FactRow),
            new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(
            nameof(Level),
            typeof(PanelStatusLevel),
            typeof(FactRow),
            new PropertyMetadata(PanelStatusLevel.Neutral));

    /// <summary>Draws the status mark. Off for a fact that has no state.</summary>
    public static readonly DependencyProperty ShowMarkProperty =
        DependencyProperty.Register(
            nameof(ShowMark),
            typeof(bool),
            typeof(FactRow),
            new PropertyMetadata(false));

    /// <summary>What to do about it, or what the value means.</summary>
    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(
            nameof(Hint),
            typeof(string),
            typeof(FactRow),
            new PropertyMetadata(null));

    /// <summary>Monospaced value, for a path, a GUID or an identifier.</summary>
    public static readonly DependencyProperty IsMonoProperty =
        DependencyProperty.Register(
            nameof(IsMono),
            typeof(bool),
            typeof(FactRow),
            new PropertyMetadata(false));

    /// <summary>
    /// Width of the label column. It is a property rather than a constant so a
    /// narrow column - the Stream Deck editor's side panel - can shrink it
    /// without the rows reflowing into two lines.
    /// </summary>
    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(
            nameof(LabelWidth),
            typeof(GridLength),
            typeof(FactRow),
            new PropertyMetadata(new GridLength(170)));

    public FactRow() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public PanelStatusLevel Level
    {
        get => (PanelStatusLevel)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public bool ShowMark
    {
        get => (bool)GetValue(ShowMarkProperty);
        set => SetValue(ShowMarkProperty, value);
    }

    public string? Hint
    {
        get => (string?)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public bool IsMono
    {
        get => (bool)GetValue(IsMonoProperty);
        set => SetValue(IsMonoProperty, value);
    }

    public GridLength LabelWidth
    {
        get => (GridLength)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    /// <summary>
    /// Announces the row as "label: value". Without it a screen reader reads
    /// the two text blocks as unrelated fragments.
    /// </summary>
    private static void OnContentChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is FactRow row)
        {
            AutomationProperties.SetName(row, $"{row.Label}: {row.Value}");
        }
    }
}
