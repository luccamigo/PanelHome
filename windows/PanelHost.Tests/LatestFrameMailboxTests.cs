using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class LatestFrameMailboxTests
{
    [TestMethod]
    public async Task Newest_frame_replaces_unsent_frame()
    {
        using var mailbox = new LatestFrameMailbox<EncodedVideoFrame>();
        var oldFrame = Frame(1);
        var newFrame = Frame(2);

        mailbox.Publish(oldFrame);
        mailbox.Publish(newFrame);
        var result = await mailbox.ReadAsync(CancellationToken.None);

        Assert.AreSame(newFrame, result);
        Assert.AreEqual(1L, mailbox.Dropped);
    }

    [TestMethod]
    public async Task Waiting_consumer_is_cancellable()
    {
        using var mailbox = new LatestFrameMailbox<EncodedVideoFrame>();
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            mailbox.ReadAsync(cancellation.Token));
    }

    private static EncodedVideoFrame Frame(ulong sequence) => new(
        sequence,
        sequence,
        1,
        1,
        4,
        [1]);
}
