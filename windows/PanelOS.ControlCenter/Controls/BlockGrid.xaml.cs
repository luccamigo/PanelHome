using System.Windows;
using System.Windows.Controls;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Controls;

public partial class BlockGrid : UserControl
{
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows), typeof(IEnumerable<WifiBlockRow>), typeof(BlockGrid), new PropertyMetadata(null));
    public static readonly DependencyProperty BlockFontSizeProperty = DependencyProperty.Register(
        nameof(BlockFontSize), typeof(double), typeof(BlockGrid), new PropertyMetadata(16d));
    public static readonly DependencyProperty BlockFontWeightProperty = DependencyProperty.Register(
        nameof(BlockFontWeight), typeof(FontWeight), typeof(BlockGrid), new PropertyMetadata(FontWeights.Normal));
    public static readonly DependencyProperty BlockMarginProperty = DependencyProperty.Register(
        nameof(BlockMargin), typeof(Thickness), typeof(BlockGrid), new PropertyMetadata(new Thickness(0, 0, 18, 0)));
    public static readonly DependencyProperty RowMarginProperty = DependencyProperty.Register(
        nameof(RowMargin), typeof(Thickness), typeof(BlockGrid), new PropertyMetadata(new Thickness(0, 0, 0, 8)));

    public BlockGrid() => InitializeComponent();

    public IEnumerable<WifiBlockRow>? Rows { get => (IEnumerable<WifiBlockRow>?)GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
    public double BlockFontSize { get => (double)GetValue(BlockFontSizeProperty); set => SetValue(BlockFontSizeProperty, value); }
    public FontWeight BlockFontWeight { get => (FontWeight)GetValue(BlockFontWeightProperty); set => SetValue(BlockFontWeightProperty, value); }
    public Thickness BlockMargin { get => (Thickness)GetValue(BlockMarginProperty); set => SetValue(BlockMarginProperty, value); }
    public Thickness RowMargin { get => (Thickness)GetValue(RowMarginProperty); set => SetValue(RowMarginProperty, value); }
}