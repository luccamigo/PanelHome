using System.Windows;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
        // A request waiting for approval from another page or window: flash the taskbar button until the window is in front.
        if (viewModel.Wifi is { } wifi)
        {
            wifi.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(WifiPairingViewModel.HasPending)) return;
                if (wifi.HasPending) Services.WindowFlash.Start(this); else Services.WindowFlash.Stop(this);
            };
        }
    }
}
