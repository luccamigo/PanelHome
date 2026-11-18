namespace PanelHost;

internal static class ProcessCrashDiagnostics
{
    private static readonly object Sync = new();
    private static int _installed;

    internal static void Install(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                WriteException(
                    writer,
                    "AppDomain.CurrentDomain.UnhandledException",
                    exception,
                    eventArgs.IsTerminating);
            }
            else
            {
                WriteNonException(
                    writer,
                    "AppDomain.CurrentDomain.UnhandledException",
                    eventArgs.ExceptionObject,
                    eventArgs.IsTerminating);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            WriteException(
                writer,
                "TaskScheduler.UnobservedTaskException",
                eventArgs.Exception,
                terminating: false);
    }

    internal static void WriteException(
        TextWriter writer,
        string boundary,
        Exception exception,
        bool terminating = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentNullException.ThrowIfNull(exception);

        lock (Sync)
        {
            try
            {
                writer.WriteLine(
                    $"[Crash] boundary={boundary} terminating={terminating} " +
                    $"utc={DateTimeOffset.UtcNow:O}");
                WriteExceptionTree(writer, exception, "exception", 0);
                writer.Flush();
            }
            catch
            {
                // A crash logger cannot safely turn a diagnostic failure into a
                // replacement process failure. The original exception continues.
            }
        }
    }

    private static void WriteNonException(
        TextWriter writer,
        string boundary,
        object exceptionObject,
        bool terminating)
    {
        lock (Sync)
        {
            try
            {
                writer.WriteLine(
                    $"[Crash] boundary={boundary} terminating={terminating} " +
                    $"utc={DateTimeOffset.UtcNow:O}");
                writer.WriteLine(
                    $"[Crash] exceptionObject.type=" +
                    $"{exceptionObject?.GetType().FullName ?? "<null>"}");
                writer.WriteLine(
                    $"[Crash] exceptionObject.value={exceptionObject ?? "<null>"}");
                writer.Flush();
            }
            catch
            {
            }
        }
    }

    private static void WriteExceptionTree(
        TextWriter writer,
        Exception exception,
        string label,
        int depth)
    {
        var indent = new string(' ', depth * 2);
        writer.WriteLine(
            $"[Crash] {indent}{label}.type={exception.GetType().FullName}");
        writer.WriteLine(
            $"[Crash] {indent}{label}.message=" +
            PanelOS.Windows.PanelOSPaths.RedactForLog(exception.Message));
        writer.WriteLine(
            $"[Crash] {indent}{label}.hresult=0x{unchecked((uint)exception.HResult):X8}");
        writer.WriteLine($"[Crash] {indent}{label}.stack:");
        writer.WriteLine(PanelOS.Windows.PanelOSPaths.RedactForLog(
            exception.StackTrace ?? "<unavailable>"));

        if (exception is AggregateException aggregate)
        {
            for (var index = 0; index < aggregate.InnerExceptions.Count; index++)
            {
                WriteExceptionTree(
                    writer,
                    aggregate.InnerExceptions[index],
                    $"inner[{index}]",
                    depth + 1);
            }
        }
        else if (exception.InnerException is { } inner)
        {
            WriteExceptionTree(writer, inner, "inner", depth + 1);
        }
    }
}
