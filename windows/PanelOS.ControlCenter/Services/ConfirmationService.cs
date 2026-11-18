using System.Windows;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// Asks the user to confirm something destructive. Only deletion uses it -
/// every other edit is undone by editing again, or by reloading before saving.
/// </summary>
public interface IConfirmationService
{
    bool Confirm(string title, string message);
}

public sealed class ConfirmationService : IConfirmationService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
