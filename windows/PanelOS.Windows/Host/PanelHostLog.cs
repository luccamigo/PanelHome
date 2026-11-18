using System.Text;

namespace PanelOS.Windows.Host;

public enum PanelHostLogStream
{
    /// <summary>A line the Control Center itself recorded.</summary>
    Manager,
    StandardOutput,
    StandardError,
}

public sealed record PanelHostLogEntry(
    DateTimeOffset Timestamp,
    PanelHostLogStream Stream,
    string Text)
{
    /// <summary>One line, ready for the log view and for the clipboard.</summary>
    public string Display => Format();

    public string Format() =>
        $"{Timestamp.ToLocalTime():HH:mm:ss} {Prefix()} {Text}";

    private string Prefix() => Stream switch
    {
        PanelHostLogStream.StandardError => "err",
        PanelHostLogStream.StandardOutput => "out",
        _ => "cc ",
    };
}

public sealed class PanelHostLogAppendedEventArgs : EventArgs
{
    public PanelHostLogAppendedEventArgs(PanelHostLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;
    }

    public PanelHostLogEntry Entry { get; }
}

/// <summary>
/// The last <see cref="Capacity"/> lines PanelHost produced, and nothing older.
/// </summary>
/// <remarks>
/// A long-running agent can print for days, so the buffer is a ring: appending
/// past capacity drops the oldest line instead of growing. Only output from a
/// process the Control Center started is captured - an externally started agent
/// owns its own console.
/// </remarks>
public sealed class PanelHostLogBuffer
{
    public const int DefaultCapacity = 1000;

    private readonly object _sync = new();
    private readonly Queue<PanelHostLogEntry> _entries;

    public PanelHostLogBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _entries = new Queue<PanelHostLogEntry>(capacity);
    }

    public event EventHandler<PanelHostLogAppendedEventArgs>? Appended;

    public int Capacity { get; }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public void Append(PanelHostLogStream stream, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Append(new PanelHostLogEntry(
            DateTimeOffset.Now,
            stream,
            PanelOSPaths.RedactForLog(text)));
    }

    public void Append(PanelHostLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry with { Text = PanelOSPaths.RedactForLog(entry.Text) };
        lock (_sync)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }
        }

        Appended?.Invoke(this, new PanelHostLogAppendedEventArgs(entry));
    }

    public IReadOnlyList<PanelHostLogEntry> Snapshot()
    {
        lock (_sync)
        {
            return [.. _entries];
        }
    }

    public string ToPlainText()
    {
        var builder = new StringBuilder();
        foreach (var entry in Snapshot())
        {
            builder.AppendLine(entry.Format());
        }

        return builder.ToString();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }
}
