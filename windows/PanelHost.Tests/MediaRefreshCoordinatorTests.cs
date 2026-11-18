using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class MediaRefreshCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Artwork_failure_does_not_destroy_media_state()
    {
        var coordinator = new MediaRefreshCoordinator();
        var lease = coordinator.BindSession(new object());
        var state = CreateState("track-a", "Track A");
        Assert.IsTrue(coordinator.TryPublishState(lease, state, resetArtwork: true));
        using var log = new StringWriter();

        await MediaArtworkRefresh.RunAsync(
            coordinator,
            lease,
            state.MediaId!,
            _ => Task.FromException<MediaArtworkPayload?>(
                new InvalidOperationException("decoder failed")),
            log,
            CancellationToken.None);

        Assert.AreEqual(state, coordinator.CurrentState);
        Assert.IsTrue(coordinator.CurrentState.Available);
        Assert.IsFalse(coordinator.CurrentArtwork.Available);
        Assert.AreEqual(state.MediaId, coordinator.CurrentArtwork.MediaId);
    }

    [TestMethod]
    public async Task Missing_thumbnail_publishes_unavailable_artwork_for_current_track()
    {
        var coordinator = new MediaRefreshCoordinator();
        var lease = coordinator.BindSession(new object());
        var state = CreateState("track-without-art", "No Artwork");
        coordinator.TryPublishState(lease, state, resetArtwork: true);

        await MediaArtworkRefresh.RunAsync(
            coordinator,
            lease,
            state.MediaId!,
            _ => Task.FromResult<MediaArtworkPayload?>(null),
            TextWriter.Null,
            CancellationToken.None);

        Assert.AreEqual(state, coordinator.CurrentState);
        Assert.AreEqual(state.MediaId, coordinator.CurrentArtwork.MediaId);
        Assert.IsFalse(coordinator.CurrentArtwork.Available);
        Assert.IsNull(coordinator.CurrentArtwork.MimeType);
        Assert.IsNull(coordinator.CurrentArtwork.DataBase64);
    }

    [TestMethod]
    public async Task Thumbnail_com_exception_is_logged_with_hresult_and_is_isolated()
    {
        var coordinator = new MediaRefreshCoordinator();
        var lease = coordinator.BindSession(new object());
        var state = CreateState("track-com", "COM Failure");
        coordinator.TryPublishState(lease, state, resetArtwork: true);
        using var log = new StringWriter();
        var exception = new COMException(
            "Access denied",
            unchecked((int)0x80070005));

        await MediaArtworkRefresh.RunAsync(
            coordinator,
            lease,
            state.MediaId!,
            _ => Task.FromException<MediaArtworkPayload?>(exception),
            log,
            CancellationToken.None);

        var output = log.ToString();
        StringAssert.Contains(output, "[Media] Artwork refresh failed");
        StringAssert.Contains(output, "Stage: thumbnail retrieval");
        StringAssert.Contains(output, "Type: COMException");
        StringAssert.Contains(output, "HRESULT: 0x80070005");
        StringAssert.Contains(output, "Message: Access denied");
#if DEBUG
        StringAssert.Contains(output, "Stack trace:");
#endif
        Assert.AreEqual(state, coordinator.CurrentState);
        Assert.IsFalse(coordinator.CurrentArtwork.Available);
    }

    [TestMethod]
    public async Task Non_requested_thumbnail_cancellation_is_treated_as_artwork_failure()
    {
        var coordinator = new MediaRefreshCoordinator();
        var lease = coordinator.BindSession(new object());
        var state = CreateState("track-oce", "Unexpected Cancellation");
        coordinator.TryPublishState(lease, state, resetArtwork: true);
        using var log = new StringWriter();

        await MediaArtworkRefresh.RunAsync(
            coordinator,
            lease,
            state.MediaId!,
            _ => Task.FromException<MediaArtworkPayload?>(
                new OperationCanceledException("WinRT canceled the read")),
            log,
            CancellationToken.None);

        StringAssert.Contains(log.ToString(), "Type: OperationCanceledException");
        Assert.AreEqual(state, coordinator.CurrentState);
        Assert.IsFalse(coordinator.CurrentArtwork.Available);
    }

    [TestMethod]
    public async Task Older_artwork_refresh_cannot_overwrite_new_track()
    {
        var coordinator = new MediaRefreshCoordinator();
        var session = new object();
        var leaseA = coordinator.BindSession(session);
        var stateA = CreateState("track-a", "Track A");
        coordinator.TryPublishState(leaseA, stateA, resetArtwork: true);
        var readStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldRead = new TaskCompletionSource<MediaArtworkPayload?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var oldRefresh = MediaArtworkRefresh.RunAsync(
            coordinator,
            leaseA,
            stateA.MediaId!,
            _ =>
            {
                readStarted.TrySetResult(true);
                return releaseOldRead.Task;
            },
            TextWriter.Null,
            CancellationToken.None);
        await readStarted.Task.WaitAsync(TestTimeout);

        coordinator.Invalidate();
        var leaseB = coordinator.Capture();
        var stateB = CreateState("track-b", "Track B");
        coordinator.TryPublishState(leaseB, stateB, resetArtwork: true);
        await MediaArtworkRefresh.RunAsync(
            coordinator,
            leaseB,
            stateB.MediaId!,
            _ => Task.FromResult<MediaArtworkPayload?>(PngPayload(0x22)),
            TextWriter.Null,
            CancellationToken.None);
        var currentArtwork = coordinator.CurrentArtwork;

        releaseOldRead.SetResult(PngPayload(0x11));
        await oldRefresh.WaitAsync(TestTimeout);

        Assert.AreEqual(stateB, coordinator.CurrentState);
        Assert.AreEqual(stateB.MediaId, coordinator.CurrentArtwork.MediaId);
        Assert.AreEqual(currentArtwork, coordinator.CurrentArtwork);
        Assert.IsTrue(coordinator.CurrentArtwork.Available);
    }

    [TestMethod]
    public async Task Rapid_session_changes_reject_late_state_from_previous_sessions()
    {
        var coordinator = new MediaRefreshCoordinator();
        var sessionA = new object();
        var sessionB = new object();
        var sessionC = new object();
        var leaseA = coordinator.BindSession(sessionA);
        var releaseA = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateA = PublishAfterAsync(
            coordinator,
            leaseA,
            CreateState("track-a", "Track A"),
            releaseA.Task);

        var leaseB = coordinator.BindSession(sessionB);
        var releaseB = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lateB = PublishAfterAsync(
            coordinator,
            leaseB,
            CreateState("track-b", "Track B"),
            releaseB.Task);

        var leaseC = coordinator.BindSession(sessionC);
        var stateC = CreateState("track-c", "Track C");
        Assert.IsTrue(coordinator.TryPublishState(
            leaseC,
            stateC,
            resetArtwork: true));

        releaseB.SetResult(true);
        releaseA.SetResult(true);
        var staleResults = await Task.WhenAll(lateA, lateB).WaitAsync(TestTimeout);

        CollectionAssert.AreEqual(new[] { false, false }, staleResults);
        Assert.AreEqual(stateC, coordinator.CurrentState);
        Assert.AreEqual(stateC.MediaId, coordinator.CurrentArtwork.MediaId);
    }

    [TestMethod]
    public void Rebind_started_before_new_session_event_cannot_bind_stale_session()
    {
        var coordinator = new MediaRefreshCoordinator();
        var pendingSession = new object();
        var oldSession = new object();
        var staleCandidate = new object();
        var newestSession = new object();
        coordinator.BindSession(oldSession);

        var staleRebind = coordinator.MarkSessionPending(pendingSession);
        var currentRebind = coordinator.MarkSessionPending(pendingSession);

        Assert.IsFalse(coordinator.TryBindSession(
            staleRebind,
            staleCandidate,
            out _));
        Assert.IsTrue(coordinator.TryBindSession(
            currentRebind,
            newestSession,
            out var newestLease));
        Assert.IsTrue(coordinator.IsCurrent(newestLease));
        Assert.AreSame(newestSession, newestLease.Session);
    }

    [TestMethod]
    public async Task State_changed_notifications_remain_in_commit_order()
    {
        var coordinator = new MediaRefreshCoordinator();
        var lease = coordinator.BindSession(new object());
        using var firstHandlerEntered = new ManualResetEventSlim();
        using var releaseFirstHandler = new ManualResetEventSlim();
        var observedTitles = new ConcurrentQueue<string>();
        coordinator.StateChanged += (_, eventArgs) =>
        {
            if (eventArgs.State.Title == "Track A")
            {
                firstHandlerEntered.Set();
                Assert.IsTrue(releaseFirstHandler.Wait(TestTimeout));
            }

            observedTitles.Enqueue(eventArgs.State.Title!);
        };

        var publishA = Task.Run(() => coordinator.TryPublishState(
            lease,
            CreateState("track-a", "Track A"),
            resetArtwork: true));
        Assert.IsTrue(firstHandlerEntered.Wait(TestTimeout));
        var publishB = Task.Run(() => coordinator.TryPublishState(
            lease,
            CreateState("track-b", "Track B"),
            resetArtwork: true));

        await WaitUntilAsync(
            () => coordinator.CurrentState.Title == "Track B",
            TestTimeout);
        releaseFirstHandler.Set();
        await Task.WhenAll(publishA, publishB).WaitAsync(TestTimeout);

        CollectionAssert.AreEqual(
            new[] { "Track A", "Track B" },
            observedTitles.ToArray());
    }

    [TestMethod]
    public void Unknown_content_type_is_inferred_only_from_supported_signature()
    {
        var valid = MediaArtworkRefresh.CreateMessage(
            "track-png",
            PngPayload(0x33) with { ContentType = "application/octet-stream" },
            out var validRejection);
        var invalid = MediaArtworkRefresh.CreateMessage(
            "track-unknown",
            new MediaArtworkPayload(null, [1, 2, 3, 4]),
            out var invalidRejection);

        Assert.IsNull(validRejection);
        Assert.IsTrue(valid.Available);
        Assert.AreEqual("image/png", valid.MimeType);
        Assert.IsNotNull(valid.DataBase64);
        Assert.IsNotNull(invalidRejection);
        Assert.IsFalse(invalid.Available);
        Assert.IsNull(invalid.DataBase64);
    }

    [TestMethod]
    public void Empty_and_oversized_artwork_are_unavailable()
    {
        var empty = MediaArtworkRefresh.CreateMessage(
            "track-empty",
            new MediaArtworkPayload("image/png", []),
            out var emptyReason);
        var oversizedBytes = new byte[MediaArtworkRefresh.MaximumArtworkBytes + 1];
        Array.Copy(PngPayload().Bytes, oversizedBytes, PngPayload().Bytes.Length);
        var oversized = MediaArtworkRefresh.CreateMessage(
            "track-large",
            new MediaArtworkPayload("image/png", oversizedBytes),
            out var oversizedReason);

        Assert.IsFalse(empty.Available);
        Assert.IsNotNull(emptyReason);
        Assert.IsFalse(oversized.Available);
        Assert.IsNotNull(oversizedReason);
    }

    private static async Task<bool> PublishAfterAsync(
        MediaRefreshCoordinator coordinator,
        MediaRefreshLease lease,
        MediaStateMessage state,
        Task release)
    {
        await release;
        return coordinator.TryPublishState(lease, state, resetArtwork: true);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private static MediaStateMessage CreateState(string mediaId, string title) => new()
    {
        Available = true,
        MediaId = mediaId,
        SourceApp = "Test.Player",
        Title = title,
        Artist = "Artist",
        Album = "Album",
        IsPlaying = true,
        PositionMs = 1_000,
        DurationMs = 10_000,
        CanPlayPause = true,
        CanPrevious = true,
        CanNext = true,
    };

    private static MediaArtworkPayload PngPayload(byte marker = 0) => new(
        "image/png",
        [
            0x89, 0x50, 0x4e, 0x47,
            0x0d, 0x0a, 0x1a, 0x0a,
            marker,
        ]);
}
