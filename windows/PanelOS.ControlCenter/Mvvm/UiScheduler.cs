namespace PanelOS.ControlCenter.Mvvm;

/// <summary>
/// Marshals a callback onto the UI thread. The host manager raises its events
/// from the thread pool - a process can exit at any moment - and a view model
/// must not touch bound state from there.
/// </summary>
public interface IUiScheduler
{
    void Post(Action action);
}

/// <summary>
/// Captures the synchronization context of the thread that created it, which
/// in the application is the WPF dispatcher thread.
/// </summary>
public sealed class SynchronizationContextUiScheduler : IUiScheduler
{
    private readonly SynchronizationContext? _context =
        SynchronizationContext.Current;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(static state => ((Action)state!)(), action);
    }
}

/// <summary>Runs the callback inline; the default outside an application.</summary>
public sealed class ImmediateUiScheduler : IUiScheduler
{
    public static ImmediateUiScheduler Instance { get; } = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
