using PanelOS.DriverInterop;

namespace PanelHost.Video;

/// <summary>
/// Development source that exercises the normal video path without reading
/// pixels from the display driver. Static mode publishes one deterministic
/// frame per session. Dynamic mode publishes a moving checkerboard on every
/// poll so Media Foundation sees continuously changing input.
/// </summary>
internal sealed class VideoTestPatternFrameSource : IFrameSource
{
    internal const int PatternWidth = 640;
    internal const int PatternHeight = 400;
    internal const ulong PatternSequence = 1;

    private readonly VideoTestPatternMode _mode;
    private readonly FrameSnapshot _pattern = CreatePattern();

    internal VideoTestPatternFrameSource(
        VideoTestPatternMode mode = VideoTestPatternMode.Static)
    {
        _mode = mode;
    }

    public Task<FrameSnapshot?> GetLatestFrameAsync(
        ulong lastSequence,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_mode == VideoTestPatternMode.Dynamic)
        {
            var nextSequence = checked(lastSequence + 1);
            return Task.FromResult<FrameSnapshot?>(
                CreateDynamicPattern(nextSequence));
        }

        return Task.FromResult<FrameSnapshot?>(
            lastSequence < PatternSequence ? _pattern : null);
    }

    private static FrameSnapshot CreatePattern()
    {
        var stride = checked(PatternWidth * 4);
        var pixels = new byte[checked(stride * PatternHeight)];
        for (var y = 0; y < PatternHeight; y++)
        {
            var top = y < PatternHeight / 2;
            for (var x = 0; x < PatternWidth; x++)
            {
                var left = x < PatternWidth / 2;
                var offset = checked(y * stride + x * 4);
                var (blue, green, red) = (top, left) switch
                {
                    (true, true) => ((byte)0, (byte)0, (byte)255),
                    (true, false) => ((byte)0, (byte)255, (byte)0),
                    (false, true) => ((byte)255, (byte)0, (byte)0),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 0xFF;
            }
        }

        return new FrameSnapshot(
            Generation: 1,
            Sequence: PatternSequence,
            CaptureTimestampMicros: 1,
            Width: PatternWidth,
            Height: PatternHeight,
            Stride: stride,
            Format: PanelDisplayFrameFormat.Bgra8888,
            Pixels: pixels);
    }

    private static FrameSnapshot CreateDynamicPattern(ulong sequence)
    {
        const int checkerSize = 32;
        var stride = checked(PatternWidth * 4);
        var pixels = new byte[checked(stride * PatternHeight)];
        var phase = checked((int)(sequence % (checkerSize * 2UL)));
        var barX = checked((int)((sequence * 7UL) % (ulong)PatternWidth));
        for (var y = 0; y < PatternHeight; y++)
        {
            for (var x = 0; x < PatternWidth; x++)
            {
                var checker = (((x + phase) / checkerSize) +
                    ((y + phase / 2) / checkerSize)) % 2 == 0;
                var inMovingBar = Math.Abs(x - barX) < 6;
                var offset = checked(y * stride + x * 4);
                var (blue, green, red) = inMovingBar
                    ? ((byte)255, (byte)255, (byte)255)
                    : checker
                        ? ((byte)24, (byte)48, (byte)224)
                        : ((byte)224, (byte)176, (byte)24);
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 0xFF;
            }
        }

        return new FrameSnapshot(
            Generation: 1,
            Sequence: sequence,
            CaptureTimestampMicros: checked(sequence * 1_000_000UL / 30UL),
            Width: PatternWidth,
            Height: PatternHeight,
            Stride: stride,
            Format: PanelDisplayFrameFormat.Bgra8888,
            Pixels: pixels);
    }
}

internal enum VideoTestPatternMode
{
    Static,
    Dynamic,
}
