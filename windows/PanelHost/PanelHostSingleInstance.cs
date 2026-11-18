using System.Security.Principal;

namespace PanelHost;

internal sealed class PanelHostSingleInstance : IDisposable
{
    internal const int AlreadyRunningExitCode = 17;
    private readonly Mutex _mutex;
    private int _disposed;

    private PanelHostSingleInstance(Mutex mutex, bool isOwner)
    {
        _mutex = mutex;
        IsOwner = isOwner;
    }

    internal bool IsOwner { get; }

    internal static PanelHostSingleInstance TryAcquire(string? testName = null)
    {
        var name = testName ?? CreatePerUserName();
        var mutex = new Mutex(initiallyOwned: false, name);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        return new PanelHostSingleInstance(mutex, acquired);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        // Closing the owning handle is intentionally used instead of
        // ReleaseMutex: top-level async continuations are not guaranteed to
        // finish on the thread that acquired this process-lifetime mutex.
        _mutex.Dispose();
    }

    private static string CreatePerUserName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? Environment.UserName;
        return $"Local\\PanelOS.PanelHost.{sid.Replace('-', '_')}";
    }
}
