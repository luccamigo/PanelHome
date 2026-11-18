namespace PanelHost.Video;

internal enum VideoSessionCloseReason
{
    Unknown,
    DesktopStopped,
    ControlDisconnected,
    ClientFin,
    ProducerCompleted,
    ConsumerCompleted,
    EncoderFailure,
    TransportFailure,
    ServerShutdown,
    Cancellation,
    ProtocolError,
}

internal sealed class VideoSessionCloseReasonTracker
{
    private int _reason;

    internal VideoSessionCloseReason Reason =>
        (VideoSessionCloseReason)Volatile.Read(ref _reason);

    internal bool TrySet(VideoSessionCloseReason reason)
    {
        if (reason == VideoSessionCloseReason.Unknown)
        {
            return false;
        }

        return Interlocked.CompareExchange(
            ref _reason,
            (int)reason,
            (int)VideoSessionCloseReason.Unknown) ==
            (int)VideoSessionCloseReason.Unknown;
    }
}
