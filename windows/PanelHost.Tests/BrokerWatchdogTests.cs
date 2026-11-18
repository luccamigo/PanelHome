using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelDisplayBroker;

namespace PanelHost.Tests;

/// <summary>
/// PAN-3: the broker's own deadline. A driver call that never returns leaves the orderly
/// shutdown path unreachable, so a separate thread has to end the process.
/// </summary>
[TestClass]
public sealed class BrokerWatchdogTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    [TestMethod]
    public async Task Pan3_a_driver_operation_that_never_returns_ends_the_broker()
    {
        var expired = new TaskCompletionSource<(int Code, string Reason)>();
        var watchdog = new BrokerWatchdog((code, reason) => expired.TrySetResult((code, reason)), Poll);

        using var stuck = watchdog.Request(TimeSpan.FromMilliseconds(150));
        // Nothing disposes the scope: the operation is blocked in a native wait.

        var result = await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(BrokerWatchdog.RequestTimeoutExitCode, result.Code);
    }

    [TestMethod]
    public async Task Pan3_an_operation_that_returns_in_time_never_trips_the_watchdog()
    {
        var tripped = 0;
        var watchdog = new BrokerWatchdog((_, _) => Interlocked.Increment(ref tripped), Poll);

        for (var i = 0; i < 5; i++)
        {
            using (watchdog.Request(TimeSpan.FromMilliseconds(400)))
            {
                await Task.Delay(30);
            }
        }
        await Task.Delay(600);

        Assert.AreEqual(0, tripped);
    }

    [TestMethod]
    public async Task Pan3_shutdown_that_does_not_finish_ends_the_process_and_cannot_be_disarmed()
    {
        var expired = new TaskCompletionSource<int>();
        var watchdog = new BrokerWatchdog((code, _) => expired.TrySetResult(code), Poll);

        watchdog.ArmShutdown(TimeSpan.FromMilliseconds(200));
        // A late, quick request finishing must not cancel the shutdown deadline.
        using (watchdog.Request(TimeSpan.FromSeconds(30)))
        {
        }

        Assert.AreEqual(
            BrokerWatchdog.ShutdownTimeoutExitCode,
            await expired.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task Pan3_arming_shutdown_twice_does_not_extend_the_first_deadline()
    {
        var expired = new TaskCompletionSource<int>();
        var watchdog = new BrokerWatchdog((code, _) => expired.TrySetResult(code), Poll);

        watchdog.ArmShutdown(TimeSpan.FromMilliseconds(200));
        watchdog.ArmShutdown(TimeSpan.FromMinutes(5));

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
