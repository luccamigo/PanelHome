using System.Collections.Specialized;
using System.Windows.Controls;
using PanelOS.ControlCenter.ViewModels;

namespace PanelOS.ControlCenter.Views;

public partial class DiagnosticsView : UserControl
{
    private INotifyCollectionChanged? _observedLog;

    public DiagnosticsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => DetachLog();
    }

    /// <summary>
    /// Following the newest line is a scroll position, not state worth putting
    /// in a view model, so it is done here - and only while the operator has
    /// left auto-scroll on.
    /// </summary>
    private void OnDataContextChanged(
        object sender,
        System.Windows.DependencyPropertyChangedEventArgs e)
    {
        DetachLog();
        if (DataContext is not DiagnosticsViewModel viewModel)
        {
            return;
        }

        _observedLog = viewModel.VisibleLogEntries;
        _observedLog.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add ||
            DataContext is not DiagnosticsViewModel { AutoScrollLog: true } viewModel ||
            viewModel.VisibleLogEntries.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(
            viewModel.VisibleLogEntries[viewModel.VisibleLogEntries.Count - 1]);
    }

    private void DetachLog()
    {
        if (_observedLog is not null)
        {
            _observedLog.CollectionChanged -= OnLogChanged;
            _observedLog = null;
        }
    }
}
