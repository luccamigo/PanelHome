using System.Windows;
using System.Windows.Media;

namespace PanelOS.ControlCenter.Controls;

/// <summary>
/// Draws a QR matrix on a white tile with a four-module quiet zone, with every module a WHOLE number of PHYSICAL screen
/// pixels. The size is worked out in physical pixels (device-independent units times the DPI scale), because at 125% or
/// 175% scaling a module of 3 or 4 DIP is a fraction of a pixel and its edges blur, the worst case for a tablet camera.
/// At least 4 physical pixels per module, 5 when there is room (about 585 px), and a larger limit for the full-screen view.
/// </summary>
public sealed class QrCodeControl : FrameworkElement
{
    public const int QuietZoneModules = 4;
    public const int MinModulePixels = 4;
    public const int DefaultMaxModulePixels = 5;

    public static readonly DependencyProperty ModulesProperty = DependencyProperty.Register(
        nameof(Modules), typeof(IReadOnlyList<string>), typeof(QrCodeControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxModulePixelsProperty = DependencyProperty.Register(
        nameof(MaxModulePixels), typeof(int), typeof(QrCodeControl),
        new FrameworkPropertyMetadata(DefaultMaxModulePixels, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<string>? Modules
    {
        get => (IReadOnlyList<string>?)GetValue(ModulesProperty);
        set => SetValue(ModulesProperty, value);
    }

    /// <summary>Largest module size in physical pixels (5 on the page, much larger for "Ampliar").</summary>
    public int MaxModulePixels
    {
        get => (int)GetValue(MaxModulePixelsProperty);
        set => SetValue(MaxModulePixelsProperty, value);
    }

    /// <summary>
    /// Whole physical pixels per module that fit in <paramref name="availableDip"/> at <paramref name="dpiScale"/>, never below
    /// <see cref="MinModulePixels"/> (a QR that cannot be read is worse than one that scrolls) and never above <paramref name="maxPixels"/>.
    /// With no finite room the largest size is used.
    /// </summary>
    public static int ModulePixels(int modules, double availableDip, double dpiScale, int maxPixels)
    {
        var total = modules + 2 * QuietZoneModules;
        var ceiling = Math.Max(MinModulePixels, maxPixels);
        if (double.IsNaN(availableDip) || double.IsInfinity(availableDip) || availableDip <= 0 || dpiScale <= 0) return ceiling;
        var fits = (int)Math.Floor(availableDip * dpiScale / total);
        return Math.Clamp(fits, MinModulePixels, ceiling);
    }

    private double Scale => Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        if (Modules is not { Count: > 0 } rows) return new System.Windows.Size(0, 0);
        var side = Math.Min(availableSize.Width, availableSize.Height);
        var pixels = ModulePixels(rows.Count, side, Scale, MaxModulePixels);
        var dip = (rows.Count + 2 * QuietZoneModules) * pixels / Scale;
        return new System.Windows.Size(dip, dip);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (Modules is not { Count: > 0 } rows) return;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        var scale = Scale;
        // The module in DIP is an exact number of physical pixels, so every edge lands on a pixel boundary.
        var pixels = ModulePixels(rows.Count, Math.Min(ActualWidth, ActualHeight), scale, MaxModulePixels);
        var module = pixels / scale;
        var total = (rows.Count + 2 * QuietZoneModules) * module;
        context.DrawRectangle(Brushes.White, null, new Rect(0, 0, total, total));
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            for (var y = 0; y < rows.Count; y++)
                for (var x = 0; x < rows[y].Length; x++)
                {
                    if (rows[y][x] != '1') continue;
                    var left = (x + QuietZoneModules) * module; var top = (y + QuietZoneModules) * module;
                    sink.BeginFigure(new System.Windows.Point(left, top), true, true);
                    sink.LineTo(new System.Windows.Point(left + module, top), false, false);
                    sink.LineTo(new System.Windows.Point(left + module, top + module), false, false);
                    sink.LineTo(new System.Windows.Point(left, top + module), false, false);
                }
        }
        geometry.Freeze();
        context.DrawGeometry(Brushes.Black, null, geometry);
    }
}