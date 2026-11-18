using System.Diagnostics;

namespace PanelOS.Windows.StreamDeck.Runtime;

/// <summary>
/// Minimal log for the action runtime.
/// </summary>
/// <remarks>
/// What is logged is deliberately narrow: the button id, the action kind and
/// the outcome. Executable paths, arguments, working directories and URLs are
/// never written - a URL can carry a token in its query string, and an
/// argument list can carry a credential.
/// </remarks>
public interface IStreamDeckLog
{
    void Write(string message);
}

public sealed class TraceStreamDeckLog : IStreamDeckLog
{
    public void Write(string message) => Trace.WriteLine(message);
}

/// <summary>Discards everything; the default when no log is supplied.</summary>
public sealed class NullStreamDeckLog : IStreamDeckLog
{
    public static NullStreamDeckLog Instance { get; } = new();

    public void Write(string message)
    {
    }
}
