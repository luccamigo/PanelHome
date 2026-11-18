using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Views;

public partial class WifiView : UserControl
{
    public WifiView()
    {
        InitializeComponent();
        // The approval card enters with a short fade; with "reduce animations" on in Windows it simply appears.
        ApprovalCard.IsVisibleChanged += OnApprovalVisibleChanged;
        EnlargedOverlay.IsVisibleChanged += (_, e) => { if (e.NewValue is true) EnlargedOverlay.Focus(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is WifiPairingViewModel { IsEnlarged: true } viewModel)
            {
                viewModel.CloseEnlargedCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void OnApprovalVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        if (SystemParameters.ClientAreaAnimation)
        {
            ApprovalCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        // Focus goes to the card's title (read aloud with the live region), never to "Aprovar".
        ApprovalTitle.Focus();
    }
}