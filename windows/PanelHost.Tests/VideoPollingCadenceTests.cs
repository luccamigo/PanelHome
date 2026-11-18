using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

/// <summary>
/// The existing cadence coverage asserted only that
/// <c>PollsPerSecond(60) == 121</c> - arithmetic. The runtime loop ran at 64 Hz
/// for the whole life of the project and no test noticed, because none of them
/// ever timed one. These do.
/// </summary>
[TestClass]
public sealed class VideoPollingCadenceTests
{
    /// <summary>
    /// Comfortably above the ~64 Hz ceiling the default ~15.6 ms granularity
    /// imposes, and comfortably below the 121 Hz design target, so the test
    /// fails if the scope is removed without turning flaky on a busy machine.
    /// </summary>
    private const double MinimumAcceptablePollsPerSecond = 85;

    [TestMethod]
    public void Raising_resolution_requests_one_millisecond_and_releases_it_once()
    {
        var api = new FakeTimerResolutionApi();

        var scope = TimerResolutionScope.Acquire(121, TextWriter.Null, api);
        Assert.IsTrue(scope.IsRaised);
        CollectionAssert.AreEqual(
            new[] { TimerResolutionScope.RequestedPeriodMilliseconds },
            api.Began);
        CollectionAssert.AreEqual(Array.Empty<uint>(), api.Ended);

        scope.Dispose();
        CollectionAssert.AreEqual(
            new[] { TimerResolutionScope.RequestedPeriodMilliseconds },
            api.Ended);

        // timeEndPeriod is refcounted process-wide, so a second release would
        // lower the resolution out from under an unrelated holder.
        scope.Dispose();
        CollectionAssert.AreEqual(
            new[] { TimerResolutionScope.RequestedPeriodMilliseconds },
            api.Ended);
    }

    [TestMethod]
    public void A_refused_request_is_survivable_and_never_released()
    {
        var api = new FakeTimerResolutionApi { BeginSucceeds = false };

        var scope = TimerResolutionScope.Acquire(121, TextWriter.Null, api);

        Assert.IsFalse(scope.IsRaised);
        scope.Dispose();

        // Ending a period that was never begun would unbalance the process-wide
        // refcount, so a refusal must leave timeEndPeriod alone.
        CollectionAssert.AreEqual(Array.Empty<uint>(), api.Ended);
    }

    [TestMethod]
    public void A_refusal_is_reported_rather_than_swallowed()
    {
        var log = new StringWriter();

        using var scope = TimerResolutionScope.Acquire(
            121,
            log,
            new FakeTimerResolutionApi { BeginSucceeds = false });

        StringAssert.Contains(log.ToString(), "refused");
        StringAssert.Contains(log.ToString(), "64 Hz");
    }

    [DataTestMethod]
    [DataRow(30)]
    [DataRow(45)]
    [DataRow(60)]
    public async Task Poll_loop_reaches_its_design_cadence_while_resolution_is_raised(
        int targetFps)
    {
        var designPollsPerSecond = VideoFramePollingCadence.PollsPerSecond(targetFps);
        using var scope = TimerResolutionScope.Acquire(
            designPollsPerSecond,
            TextWriter.Null);
        if (!scope.IsRaised)
        {
            Assert.Inconclusive(
                "This machine refused a 1 ms timer period, so the runtime " +
                "cadence cannot be measured here.");
        }

        var interval = VideoFramePollingCadence.Interval(targetFps);
        await MeasurePollsPerSecondAsync(interval, TimeSpan.FromMilliseconds(300));
        var measured = await MeasurePollsPerSecondAsync(
            interval,
            TimeSpan.FromSeconds(2));

        // Only assert the floor that separates a raised resolution from the
        // default one. Asserting the design target exactly would measure the
        // scheduler's mood, not the defect.
        var floor = Math.Min(designPollsPerSecond, MinimumAcceptablePollsPerSecond);
        Assert.IsTrue(
            measured >= floor,
            $"Poll cadence measured {measured:N1} Hz for a {targetFps} FPS " +
            $"target, against a design cadence of {designPollsPerSecond} Hz " +
            $"and a floor of {floor:N1} Hz. The default ~15.6 ms timer " +
            $"granularity caps this loop near 64 Hz.");
    }

    private static async Task<double> MeasurePollsPerSecondAsync(
        TimeSpan interval,
        TimeSpan duration)
    {
        using var timer = new PeriodicTimer(interval);
        var elapsed = Stopwatch.StartNew();
        var ticks = 0;
        while (elapsed.Elapsed < duration)
        {
            await timer.WaitForNextTickAsync();
            ticks++;
        }

        return ticks / elapsed.Elapsed.TotalSeconds;
    }

    private sealed class FakeTimerResolutionApi : ITimerResolutionApi
    {
        internal List<uint> Began { get; } = [];

        internal List<uint> Ended { get; } = [];

        internal bool BeginSucceeds { get; init; } = true;

        public bool TryBegin(uint periodMilliseconds)
        {
            Began.Add(periodMilliseconds);
            return BeginSucceeds;
        }

        public bool TryEnd(uint periodMilliseconds)
        {
            Ended.Add(periodMilliseconds);
            return true;
        }
    }
}
