using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class FakeDesktopServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Start_and_stop_are_idempotent_and_preserve_request()
    {
        await using var service = new FakeDesktopService(TimeSpan.Zero);
        var states = new List<DesktopServiceState>();
        service.StateChanged += (_, eventArgs) => states.Add(eventArgs.State.State);
        var request = new DesktopRequest(
            Width: 1_920,
            Height: 1_080,
            RefreshRate: 60);

        var firstStart = await service.StartAsync(request, CancellationToken.None);
        var secondStart = await service.StartAsync(request, CancellationToken.None);
        var firstStop = await service.StopAsync(CancellationToken.None);
        var secondStop = await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(firstStart.Succeeded);
        Assert.IsFalse(firstStart.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Running, firstStart.State);
        Assert.IsTrue(secondStart.Succeeded);
        Assert.IsTrue(secondStart.AlreadyRunning);
        Assert.IsTrue(firstStop.Succeeded);
        Assert.IsFalse(firstStop.AlreadyStopped);
        Assert.IsTrue(secondStop.Succeeded);
        Assert.IsTrue(secondStop.AlreadyStopped);
        Assert.AreEqual(request, service.LastStartRequest);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        CollectionAssert.AreEqual(
            new[]
            {
                DesktopServiceState.Starting,
                DesktopServiceState.Running,
                DesktopServiceState.Stopping,
                DesktopServiceState.Stopped,
            },
            states);
    }

    [TestMethod]
    public async Task Concurrent_starts_share_one_transition_and_second_is_already_running()
    {
        await using var service = new FakeDesktopService(
            TimeSpan.FromMilliseconds(500));
        var states = new List<DesktopServiceState>();
        var starting = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, eventArgs) =>
        {
            states.Add(eventArgs.State.State);
            if (eventArgs.State.State == DesktopServiceState.Starting)
            {
                starting.TrySetResult(true);
            }
        };

        var firstStartTask = service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        await starting.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(firstStartTask.IsCompleted);
        var secondStartTask = service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);
        Assert.IsFalse(secondStartTask.IsCompleted);

        var firstStart = await firstStartTask.WaitAsync(TestTimeout);
        var secondStart = await secondStartTask.WaitAsync(TestTimeout);

        Assert.IsTrue(firstStart.Succeeded);
        Assert.IsFalse(firstStart.AlreadyRunning);
        Assert.IsTrue(secondStart.Succeeded);
        Assert.IsTrue(secondStart.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Running, service.State);
        Assert.AreEqual(2, service.StartRequestCount);
        CollectionAssert.AreEqual(
            new[]
            {
                DesktopServiceState.Starting,
                DesktopServiceState.Running,
            },
            states);
    }

    [TestMethod]
    public async Task Stop_during_start_waits_and_finishes_stopped()
    {
        await using var service = new FakeDesktopService(
            TimeSpan.FromMilliseconds(75));
        var states = new List<DesktopServiceState>();
        var starting = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, eventArgs) =>
        {
            states.Add(eventArgs.State.State);
            if (eventArgs.State.State == DesktopServiceState.Starting)
            {
                starting.TrySetResult(true);
            }
        };

        var startTask = service.StartAsync(new DesktopRequest(), CancellationToken.None);
        await starting.Task.WaitAsync(TestTimeout);
        var stopTask = service.StopAsync(CancellationToken.None);

        var start = await startTask.WaitAsync(TestTimeout);
        var stop = await stopTask.WaitAsync(TestTimeout);

        Assert.IsTrue(start.Succeeded);
        Assert.IsTrue(stop.Succeeded);
        Assert.IsFalse(stop.AlreadyStopped);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        CollectionAssert.AreEqual(
            new[]
            {
                DesktopServiceState.Starting,
                DesktopServiceState.Running,
                DesktopServiceState.Stopping,
                DesktopServiceState.Stopped,
            },
            states);
    }

    [TestMethod]
    public async Task Failed_start_exposes_only_stable_error_code()
    {
        await using var service = new FakeDesktopService(TimeSpan.Zero);
        service.FailNextStart("test_start_failed");

        var result = await service.StartAsync(
            new DesktopRequest(),
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.IsFalse(result.AlreadyRunning);
        Assert.AreEqual(DesktopServiceState.Error, result.State);
        Assert.AreEqual("test_start_failed", result.ErrorCode);
        Assert.AreEqual(DesktopServiceState.Error, service.State);
        Assert.AreEqual("test_start_failed", service.ErrorCode);
    }

    [TestMethod]
    public async Task Cancelled_start_rolls_back_to_stopped()
    {
        await using var service = new FakeDesktopService(TimeSpan.FromSeconds(1));
        using var cancellation = new CancellationTokenSource();
        var starting = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.State.State == DesktopServiceState.Starting)
            {
                starting.TrySetResult(true);
            }
        };

        var startTask = service.StartAsync(new DesktopRequest(), cancellation.Token);
        await starting.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
            await startTask);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
    }

    [TestMethod]
    public async Task Shutdown_helper_stops_running_service_and_logs_cleanup()
    {
        await using var service = new FakeDesktopService(TimeSpan.Zero);
        await service.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var writer = new StringWriter();

        var succeeded = await DesktopShutdown.StopAsync(
            service,
            writer,
            "test shutdown",
            TestTimeout);

        Assert.IsTrue(succeeded);
        Assert.AreEqual(DesktopServiceState.Stopped, service.State);
        Assert.AreEqual(1, service.StopRequestCount);
        StringAssert.Contains(writer.ToString(), "Cleanup (test shutdown)");
    }

    [TestMethod]
    public async Task Shutdown_helper_hard_times_out_non_cooperative_service()
    {
        await using var service = new NonCooperativeStopDesktopService();
        using var writer = new StringWriter();

        var cleanupTask = DesktopShutdown.StopAsync(
            service,
            writer,
            "non-cooperative test",
            TimeSpan.FromMilliseconds(50));

        var succeeded = await cleanupTask.WaitAsync(TestTimeout);

        Assert.IsFalse(succeeded);
        Assert.AreEqual(1, service.StopRequestCount);
        StringAssert.Contains(writer.ToString(), "timed out");

        service.CompleteStop();
    }

    private sealed class NonCooperativeStopDesktopService : IDesktopService
    {
        private readonly TaskCompletionSource<DesktopStopResult> _stop = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopRequestCount;

        public DesktopServiceState State => DesktopServiceState.Running;

        public DesktopServiceSnapshot Snapshot { get; } =
            new(DesktopServiceState.Running);

        public string? ErrorCode => null;

        public int StopRequestCount => Volatile.Read(ref _stopRequestCount);

        public event EventHandler<DesktopStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public Task<DesktopStartResult> StartAsync(
            DesktopRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DesktopStartResult(
                Succeeded: true,
                AlreadyRunning: true,
                State: DesktopServiceState.Running));

        public Task<DesktopStopResult> StopAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stopRequestCount);
            return _stop.Task;
        }

        public void CompleteStop() =>
            _stop.TrySetResult(new DesktopStopResult(
                Succeeded: true,
                AlreadyStopped: false,
                State: DesktopServiceState.Stopped));

        public ValueTask DisposeAsync()
        {
            CompleteStop();
            return ValueTask.CompletedTask;
        }
    }
}
