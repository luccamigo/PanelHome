using System.Windows;
using System.Windows.Media;

namespace PanelOS.ControlCenter.Controls;

/// <summary>
/// Small pieces of presentation that several templates need but that would
/// otherwise force a one-off control each.
/// </summary>
/// <remarks>
/// These are attached properties rather than new control types because they
/// only add a detail to a control that already exists: a description under a
/// switch, a caption under a card title, an icon beside a navigation item.
/// Keeping them here means the style stays in the theme and no view has to
/// construct the layout by hand.
/// </remarks>
public static class PanelUi
{
    /// <summary>
    /// Secondary line rendered under a setting's label. It carries the
    /// consequence of the switch, which the label alone rarely has room for.
    /// </summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.RegisterAttached(
            "Description",
            typeof(string),
            typeof(PanelUi),
            new PropertyMetadata(null));

    /// <summary>
    /// One line under a card's title, saying what the card is for. A card whose
    /// title is enough simply leaves it unset and the line disappears.
    /// </summary>
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.RegisterAttached(
            "Caption",
            typeof(string),
            typeof(PanelUi),
            new PropertyMetadata(null));

    /// <summary>
    /// Key of a geometry in the theme, resolved by the template. Views name an
    /// icon; they never build a path.
    /// </summary>
    public static readonly DependencyProperty IconKeyProperty =
        DependencyProperty.RegisterAttached(
            "IconKey",
            typeof(string),
            typeof(PanelUi),
            new PropertyMetadata(null));

    /// <summary>Icon drawn beside a sidebar item.</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon",
            typeof(Geometry),
            typeof(PanelUi),
            new PropertyMetadata(null));

    public static string? GetDescription(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(DescriptionProperty);
    }

    public static void SetDescription(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(DescriptionProperty, value);
    }

    public static string? GetCaption(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(CaptionProperty);
    }

    public static void SetCaption(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(CaptionProperty, value);
    }

    public static string? GetIconKey(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(IconKeyProperty);
    }

    public static void SetIconKey(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconKeyProperty, value);
    }

    public static Geometry? GetIcon(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Geometry?)element.GetValue(IconProperty);
    }

    public static void SetIcon(DependencyObject element, Geometry? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconProperty, value);
    }
}
