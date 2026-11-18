using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace PanelHost.Hosting;

internal enum ServiceFailurePolicy
{
    Fatal,
    Retryable,
    Optional,
}

internal sealed record SupervisedService(
    string Name,
    ServiceFailurePolicy Policy,
    Func<CancellationToken, Task> RunAsync,
    TimeSpan? RetryDelay = null);

/// <summary>
/// Makes completion policy explicit. An optional failure is isolated, a
/// retryable loop is restarted, and only a fatal loop stops its peers.
/// </summary>
internal sealed class ServiceSupervisor(ILogger<ServiceSupervisor> logger)
{
    private readonly ILogger<ServiceSupervisor> _logger = logger;

    internal async Task RunAsync(
        IReadOnlyCollection<SupervisedService> services,
        CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Count == 0)
        {
            throw new ArgumentException("At least one service is required.", nameof(services));
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var completions = Channel.CreateUnbounded<ServiceCompletion>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var tasks = services.Select(service => ObserveAsync(
            service,
            completions.Writer,
            lifetime.Token)).ToArray();

        Exception? fatalFailure = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var completion = await completions.Reader.ReadAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (completion.Service.Policy == ServiceFailurePolicy.Optional)
                {
                    LogIsolated(completion);
                    continue;
                }

                if (completion.Service.Policy == ServiceFailurePolicy.Fatal)
                {
                    fatalFailure = completion.Exception ?? new InvalidOperationException(
                        $"Fatal service '{completion.Service.Name}' stopped unexpectedly.");
                    _logger.LogCritical(
                        fatalFailure,
                        "Fatal service {Service} stopped; coordinated shutdown requested",
                        completion.Service.Name);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Cancel();
            await AwaitAllAsync(tasks).ConfigureAwait(false);
        }

        if (fatalFailure is not null)
        {
            throw fatalFailure;
        }
    }

    private async Task ObserveAsync(
        SupervisedService service,
        ChannelWriter<ServiceCompletion> completions,
        CancellationToken cancellationToken)
    {
        if (service.Policy == ServiceFailurePolicy.Retryable)
        {
            await RunRetryableAsync(service, cancellationToken).ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
        try
        {
            await service.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        completions.TryWrite(new ServiceCompletion(service, failure));
    }

    private async Task RunRetryableAsync(
        SupervisedService service,
        CancellationToken cancellationToken)
    {
        var delay = service.RetryDelay ?? TimeSpan.FromSeconds(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await service.RunAsync(cancellationToken).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Retryable service {Service} stopped; restarting",
                        service.Name);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Retryable service {Service} failed; restarting",
                    service.Name);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void LogIsolated(ServiceCompletion completion)
    {
        if (completion.Exception is null)
        {
            _logger.LogWarning(
                "Optional service {Service} stopped; core services remain available",
                completion.Service.Name);
        }
        else
        {
            _logger.LogError(
                completion.Exception,
                "Optional service {Service} failed; core services remain available",
                completion.Service.Name);
        }
    }

    private static async Task AwaitAllAsync(IEnumerable<Task> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Every non-cancellation failure was already converted to a completion.
        }
    }

    private sealed record ServiceCompletion(
        SupervisedService Service,
        Exception? Exception);
}
