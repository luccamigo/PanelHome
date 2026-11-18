using PanelHost.Protocol;

namespace PanelHost.Services;

internal static class DesktopShutdown
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> StopAsync(
        IDesktopService desktopService,
        TextWriter writer,
        string reason,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(desktopService);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        writer.WriteLine($"[Desktop] Cleanup ({reason}): requesting stop.");
        var effectiveTimeout = timeout ?? DefaultTimeout;
        using var cancellation = new CancellationTokenSource();
        Task<DesktopStopResult>? stopTask = null;
        try
        {
            stopTask = desktopService.StopAsync(cancellation.Token);
            var result = await stopTask.WaitAsync(effectiveTimeout);
            if (!result.Succeeded)
            {
                writer.WriteLine(
                    $"[Desktop] Cleanup ({reason}) failed: " +
                    (result.ErrorCode ?? DesktopErrorCodes.StopFailed));
            }
            else
            {
                writer.WriteLine("[Desktop] Stopped");
            }

            return result.Succeeded;
        }
        catch (TimeoutException)
        {
            cancellation.Cancel();
            ObserveFaultAfterTimeout(stopTask);
            writer.WriteLine($"[Desktop] Cleanup ({reason}) timed out.");
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            writer.WriteLine(
                $"[Desktop] Cleanup ({reason}) failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
        catch (OperationCanceledException)
        {
            writer.WriteLine($"[Desktop] Cleanup ({reason}) timed out.");
            return false;
        }
    }

    private static void ObserveFaultAfterTimeout(Task? task)
    {
        if (task is null)
        {
            return;
        }

        _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously |
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
