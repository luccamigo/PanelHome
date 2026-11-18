namespace PanelOS.Windows.StreamDeck;

/// <summary>
/// Watches the directory holding the Stream Deck configuration and reports
/// every event that could be the Control Center saving it.
/// </summary>
/// <remarks>
/// The directory - not the file - is watched, because the store replaces the
/// document by moving a temporary file over it: a watcher bound to the file
/// itself stops seeing the new inode after the first save. Created, Changed,
/// Renamed and Deleted are all forwarded; deciding what actually happened is
/// the provider's job, and it does it by reading the file again.
/// </remarks>
public sealed class StreamDeckConfigurationFileWatcher : IStreamDeckConfigurationChangeSource
{
    private readonly string _directory;
    private readonly string _fileName;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public StreamDeckConfigurationFileWatcher(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        _directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException(
                "O caminho da configuração não tem diretório.",
                nameof(filePath));
        _fileName = Path.GetFileName(fullPath);
    }

    public event EventHandler? Changed;

    public void Start()
    {
        if (_disposed || _watcher is not null)
        {
            return;
        }

        // The directory may not exist before the first save; creating it here
        // costs nothing and lets the watcher attach immediately.
        Directory.CreateDirectory(_directory);

        var watcher = new FileSystemWatcher(_directory, _fileName)
        {
            NotifyFilter = NotifyFilters.FileName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        watcher.Created += OnFileSystemEvent;
        watcher.Changed += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnFileSystemEvent;

        // A dropped buffer is not fatal: the provider re-reads the whole file,
        // so one more notification is all that is needed to recover.
        watcher.Error += OnFileSystemEvent;
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnFileSystemEvent;
        _watcher.Changed -= OnFileSystemEvent;
        _watcher.Deleted -= OnFileSystemEvent;
        _watcher.Renamed -= OnFileSystemEvent;
        _watcher.Error -= OnFileSystemEvent;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnFileSystemEvent(object sender, EventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);
}
