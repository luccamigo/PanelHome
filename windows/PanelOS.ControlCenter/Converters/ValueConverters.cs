using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Converters;

/// <summary>
/// Maps the view-model status severity onto the theme brushes, so no view model
/// ever mentions a colour.
/// </summary>
public sealed class StatusLevelToBrushConverter : IValueConverter
{
    /// <summary>
    /// Set to true for the tinted fill behind a chip or a banner rather than
    /// the saturated colour used for a mark.
    /// </summary>
    public bool Soft { get; set; }

    /// <summary>
    /// With <see cref="Soft"/>, tints only the levels that ask for attention
    /// and leaves the rest on the plain elevated surface.
    /// </summary>
    /// <remarks>
    /// A header strip where four healthy components are all filled green reads
    /// as decoration and stops carrying information. Tinting only warnings and
    /// failures means the one that needs the operator is the one that stands
    /// out.
    /// </remarks>
    public bool AttentionOnly { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var name = value is PanelStatusLevel level
            ? level switch
            {
                PanelStatusLevel.Good => "Good",
                PanelStatusLevel.Warning => "Warning",
                PanelStatusLevel.Bad => "Bad",
                _ => "Neutral",
            }
            : "Neutral";

        var key = Soft
            ? AttentionOnly && name is "Good" or "Neutral"
                ? "PanelSurfaceElevatedBrush"
                : $"PanelStatus{name}SoftBrush"
            : $"PanelStatus{name}Brush";
        return Application.Current?.TryFindResource(key) as Brush
            ?? Brushes.Gray;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps the status severity onto a distinct shape.
/// </summary>
/// <remarks>
/// A state that is only a colour is a state a colour-blind operator cannot
/// read, so every status mark in the Control Center is a check, a triangle, a
/// cross or a dot before it is green, amber, red or grey. The text beside it
/// says the same thing a third time.
/// </remarks>
public sealed class StatusLevelToGeometryConverter : IValueConverter
{
    public object? Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var key = value is PanelStatusLevel level
            ? level switch
            {
                PanelStatusLevel.Good => "IconStatusGood",
                PanelStatusLevel.Warning => "IconStatusWarning",
                PanelStatusLevel.Bad => "IconStatusBad",
                _ => "IconStatusNeutral",
            }
            : "IconStatusNeutral";

        return Application.Current?.TryFindResource(key) as Geometry;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves an icon named by the view model into the geometry the theme
/// defines, so a view model can say "IconOverview" without ever holding a
/// drawing and no view has to spell out a path.
/// </summary>
public sealed class ResourceKeyToGeometryConverter : IValueConverter
{
    public object? Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is string key
            ? Application.Current?.TryFindResource(key) as Geometry
            : null;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to show when the bound value is false.</summary>
    public bool Invert { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var flag = value is true;
        return flag != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is not true;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is not true;
}

/// <summary>Shows an element while the bound value is null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to show when the bound value is not null.</summary>
    public bool Invert { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is null != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows an element only while a bound collection has something in it. It is
/// what lets a list and its empty state live side by side without the view
/// model publishing a second "has items" flag for every collection it owns.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    /// <summary>Set to true to show when the collection is empty.</summary>
    public bool Invert { get; set; }

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var hasItems = value switch
        {
            null => false,
            int count => count > 0,
            ICollection collection => collection.Count > 0,
            IEnumerable items => items.GetEnumerator().MoveNext(),
            _ => false,
        };

        return hasItems != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Shows an element only when two bound strings are the same. It exists so a
/// profile row can carry the "no tablet" badge without the list items becoming
/// wrappers just to hold one boolean.
/// </summary>
public sealed class EqualStringsToVisibilityConverter : IMultiValueConverter
{
    public object Convert(
        object[] values,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (values is not [string first, string second])
        {
            return Visibility.Collapsed;
        }

        return string.Equals(first, second, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(
        object value,
        Type[] targetTypes,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when its text is null or empty.</summary>
public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
