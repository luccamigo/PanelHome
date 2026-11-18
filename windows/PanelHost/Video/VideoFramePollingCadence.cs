namespace PanelHost.Video;

internal static class VideoFramePollingCadence
{
    internal const int PollingMultiplier = 2;
    internal const int PollingOffset = 1;

    internal static int PollsPerSecond(int targetFramesPerSecond)
    {
        if (targetFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        }

        // READ_LATEST_FRAME is a latest-only polling contract. Poll just over
        // twice the publication target so a timer phase collision cannot turn
        // normal scheduler jitter into a recurring missed frame. The +1 keeps
        // the two cadences non-harmonic without creating a busy loop.
        return checked(targetFramesPerSecond * PollingMultiplier + PollingOffset);
    }

    internal static TimeSpan Interval(int targetFramesPerSecond) =>
        TimeSpan.FromSeconds(1d / PollsPerSecond(targetFramesPerSecond));
}
