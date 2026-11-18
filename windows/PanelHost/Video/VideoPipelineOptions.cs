namespace PanelHost.Video;

internal enum VideoCodecPreference
{
    Auto,
    H264,
    Zlib,
}

internal sealed record VideoPipelineOptions(
    VideoCodecPreference CodecPreference,
    int H264MaximumWidth,
    int H264MaximumHeight,
    int PreferredSessionFps,
    int H264Bitrate,
    int ZlibMaximumWidth,
    int ZlibMaximumHeight,
    int ZlibFps)
{
    internal static VideoPipelineOptions Default { get; } = new(
        VideoCodecPreference.Auto,
        H264MaximumWidth: 1280,
        H264MaximumHeight: 800,
        PreferredSessionFps: VideoFrameRatePolicy.DefaultPreferredSessionFps,
        H264Bitrate: 10_000_000,
        ZlibMaximumWidth: VideoFrameProcessor.PreviewMaximumWidth,
        ZlibMaximumHeight: VideoFrameProcessor.PreviewMaximumHeight,
        ZlibFps: 10);

    internal H264EncoderSettings CreateH264Settings(
        int width,
        int height,
        int? effectiveSessionFps = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        // NV12 stores chroma for 2x2 blocks. Keep both dimensions even without
        // ever upscaling above the negotiated/source frame.
        width &= ~1;
        height &= ~1;
        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("H.264 dimensions must be at least 2x2.");
        }

        var fps = effectiveSessionFps ?? PreferredSessionFps;
        if (fps <= 0 || fps > PreferredSessionFps)
        {
            throw new ArgumentOutOfRangeException(nameof(effectiveSessionFps));
        }

        return new H264EncoderSettings(
            width,
            height,
            fps,
            H264Bitrate,
            KeyFrameInterval: fps);
    }
}

internal static class VideoFrameRatePolicy
{
    internal const int DefaultPreferredSessionFps = 60;

    internal static bool IsSupportedHostPreference(int fps) =>
        fps is 30 or 45 or 60;

    internal static VideoFrameRateNegotiation Negotiate(
        int hostPreferredFps,
        uint clientMaxFps)
    {
        if (!IsSupportedHostPreference(hostPreferredFps))
        {
            throw new ArgumentOutOfRangeException(nameof(hostPreferredFps));
        }

        if (clientMaxFps == 0 || clientMaxFps > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(clientMaxFps));
        }

        var clientMaximum = checked((int)clientMaxFps);
        return new VideoFrameRateNegotiation(
            hostPreferredFps,
            clientMaximum,
            Math.Min(hostPreferredFps, clientMaximum));
    }
}

internal readonly record struct VideoFrameRateNegotiation(
    int HostPreferredFps,
    int ClientMaxFps,
    int EffectiveSessionFps)
{
    internal bool IsClientLimited => EffectiveSessionFps < HostPreferredFps;
}
