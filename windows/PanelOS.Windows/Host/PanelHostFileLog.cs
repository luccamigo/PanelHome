using System.Text;

namespace PanelOS.Windows.Host;

/// <summary>
/// Mirrors the PanelHost log to disk so a crash can still be read after the
/// Control Center is closed.
/// </summary>
/// <remarks>
/// Bounded by construction: one active file up to <see cref="MaximumFileBytes"/>,
/// rotated through at most <see cref="MaximumFiles"/> generations. Total usage
/// therefore cannot pass 25 MB, and nothing is ever appended without a limit.
/// </remarks>
public sealed class PanelHostFileLog : IDisposable
{
    public const int MaximumFiles = 5;
    public const long MaximumFileBytes = 5L * 1024 * 1024;

    private const string FileName = "panelhost.log";

    private readonly object _sync = new();
    private readonly string _directory;
    private readonly string _path;
    private StreamWriter? _writer;
    private long _written;
    private bool _disabled;

    public PanelHostFileLog()
        : this(PanelOSPaths.LogDirectory)
    {
    }

    public PanelHostFileLog(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public string Directory => _directory;

    public void Write(PanelHostLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                var line =
                    $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} " +
                    $"{entry.Stream} {entry.Text}";
                EnsureWriter();
                _writer!.WriteLine(line);
                _written += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                if (_written >= MaximumFileBytes)
                {
                    Rotate();
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Losing the disk copy must never take the agent down with it.
                _disabled = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(_directory);
        var info = new FileInfo(_path);
        _written = info.Exists ? info.Length : 0;
        _writer = new StreamWriter(_path, append: true, Encoding.UTF8)
        {
            AutoFlush = true,
        };
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        _written = 0;

        var oldest = FilePath(MaximumFiles - 1);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaximumFiles - 2; index >= 1; index--)
        {
            var source = FilePath(index);
            if (File.Exists(source))
            {
                File.Move(source, FilePath(index + 1), overwrite: true);
            }
        }

        if (File.Exists(_path))
        {
            File.Move(_path, FilePath(1), overwrite: true);
        }
    }

    private string FilePath(int generation) =>
        generation == 0
            ? _path
            : Path.Combine(_directory, $"panelhost.{generation}.log");
}
