using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;

namespace PanelHost.Tests;

[TestClass]
public sealed class ServiceSupervisorTests
{
    [TestMethod]
    public async Task OptionalFailureDoesNotStopFatalServices()
    {
        var supervisor = CreateSupervisor();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var optionalAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fatalCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var run = supervisor.RunAsync(
        [
            new SupervisedService(
                "control",
                ServiceFailurePolicy.Fatal,
                async token =>
                {
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        fatalCancelled.TrySetResult();
                    }
                }),
            new SupervisedService(
                "telemetry",
                ServiceFailurePolicy.Optional,
                _ =>
                {
                    optionalAttempted.TrySetResult();
                    throw new InvalidOperationException("sensor unavailable");
                }),
        ], shutdown.Token);

        await optionalAttempted.Task.WaitAsync(shutdown.Token);
        Assert.IsFalse(run.IsCompleted);
        shutdown.Cancel();
        await run;
        await fatalCancelled.Task;
    }

    [TestMethod]
    public async Task FatalFailureCancelsPeersAndPropagates()
    {
        var supervisor = CreateSupervisor();
        var peerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            supervisor.RunAsync(
            [
                new SupervisedService(
                    "control",
                    ServiceFailurePolicy.Fatal,
                    _ => Task.FromException(
                        new InvalidOperationException("control listener failed"))),
                new SupervisedService(
                    "video",
                    ServiceFailurePolicy.Fatal,
                    async token =>
                    {
                        try
                        {
                            await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        }
                        finally
                        {
                            peerCancelled.TrySetResult();
                        }
                    }),
            ], timeout.Token));

        StringAssert.Contains(exception.Message, "control listener failed");
        await peerCancelled.Task.WaitAsync(timeout.Token);
    }

    [TestMethod]
    public async Task GracefulShutdownCancelsAndAwaitsEveryService()
    {
        var supervisor = CreateSupervisor();
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var started = new CountdownEvent(2);
        var stopped = 0;
        Task Service(CancellationToken token) => RunUntilCancelledAsync(token);
        async Task RunUntilCancelledAsync(CancellationToken token)
        {
            started.Signal();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }

        var run = supervisor.RunAsync(
        [
            new("control", ServiceFailurePolicy.Fatal, Service),
            new("video", ServiceFailurePolicy.Fatal, Service),
        ], shutdown.Token);
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(2)));

        shutdown.Cancel();
        await run;
        Assert.AreEqual(2, stopped);
    }

    private static ServiceSupervisor CreateSupervisor() =>
        new(NullLogger<ServiceSupervisor>.Instance);
}
