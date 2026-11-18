using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Views;

public partial class IntegrationsView : UserControl
{
    private IInputElement? _previousFocus;
    public IntegrationsView()
    {
        InitializeComponent();
        HookDialog.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                _previousFocus = Keyboard.FocusedElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => CancelHook.Focus()));
            }
            else _previousFocus?.Focus();
        };
        Unloaded += (_, _) => { if (DataContext is IntegrationsViewModel model) model.ClearSensitive(); };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && DataContext is IntegrationsViewModel { DialogOpen: true } model)
            { model.CancelHookCommand.Execute(null); args.Handled = true; }
        };
    }
}
