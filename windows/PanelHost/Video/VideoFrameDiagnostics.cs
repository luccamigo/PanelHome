using System.Globalization;
using PanelOS.DriverInterop;

namespace PanelHost.Video;

/// <summary>
/// Bounded per-session diagnostics for First Pixels. Only the first two frames
/// at each stage are logged, so production sessions do not emit per-frame logs.
/// </summary>
internal sealed class VideoFrameDiagnostics
{
    internal const int MaximumLoggedFramesPerStage = 2;

    private readonly TextWriter _log;
    private int _rawFramesLogged;
    private int _previewFramesLogged;

    internal VideoFrameDiagnostics(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    internal void LogRaw(FrameSnapshot frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_rawFramesLogged >= MaximumLoggedFramesPerStage)
        {
            return;
        }

        _rawFramesLogged++;
        var analysis = Analyze(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.Format);
        Write(
            "Host raw",
            frame.Sequence,
            frame.Width,
            frame.Height,
            frame.Stride,
            frame.Format.ToString(),
            frame.Pixels.Length,
            analysis);
    }

    internal void LogPreview(VideoPreviewFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_previewFramesLogged >= MaximumLoggedFramesPerStage)
        {
            return;
        }

        _previewFramesLogged++;
        var analysis = Analyze(
            frame.Pixels,
            frame.Width,
            frame.Height,
            frame.Stride,
            PanelDisplayFrameFormat.Bgra8888);
        Write(
            "Host preview",
            frame.Sequence,
            frame.Width,
            frame.Height,
            frame.Stride,
            "Bgra8888",
            frame.Pixels.Length,
            analysis);
    }

    internal static VideoFrameAnalysis Analyze(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        int stride,
        PanelDisplayFrameFormat format)
    {
        if (width <= 0 || height <= 0 ||
            stride < checked(width * 4) ||
            pixels.Length != checked(stride * height) ||
            format is not (
                PanelDisplayFrameFormat.Bgra8888 or
                PanelDisplayFrameFormat.Rgba8888))
        {
            throw new InvalidDataException(
                "Cannot diagnose invalid video frame pixels.");
        }

        var sampleCoordinates = new (int X, int Y)[]
        {
            (0, 0),
            (width / 2, height / 2),
            (width - 1, height - 1),
            (width / 4, height / 4),
            (3 * width / 4, height / 4),
            (width / 4, 3 * height / 4),
            (3 * width / 4, 3 * height / 4),
        };
        var samples = new VideoPixelSample[sampleCoordinates.Length];
        for (var index = 0; index < sampleCoordinates.Length; index++)
        {
            var coordinate = sampleCoordinates[index];
            samples[index] = ReadPixel(
                pixels,
                stride,
                coordinate.X,
                coordinate.Y,
                format);
        }

        const int gridSize = 16;
        var minimum = 255;
        var maximum = 0;
        long total = 0;
        var nonBlack = 0;
        for (var gridY = 0; gridY < gridSize; gridY++)
        {
            var y = height == 1 ? 0 : gridY * (height - 1) / (gridSize - 1);
            for (var gridX = 0; gridX < gridSize; gridX++)
            {
                var x = width == 1 ? 0 : gridX * (width - 1) / (gridSize - 1);
                var sample = ReadPixel(pixels, stride, x, y, format);
                var luminance = (sample.Red + sample.Green + sample.Blue) / 3;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                total += luminance;
                if (sample.Red != 0 || sample.Green != 0 || sample.Blue != 0)
                {
                    nonBlack++;
                }
            }
        }

        var gridSamples = gridSize * gridSize;
        return new VideoFrameAnalysis(
            ComputeCrc32(pixels),
            samples,
            minimum,
            maximum,
            total / (double)gridSamples,
            nonBlack,
            gridSamples);
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc = Crc32Table[checked((int)((crc ^ value) & 0xFF))] ^ (crc >> 8);
        }

        return ~crc;
    }

    private void Write(
        string stage,
        ulong sequence,
        int width,
        int height,
        int stride,
        string format,
        int byteCount,
        VideoFrameAnalysis analysis)
    {
        _log.WriteLine(
            $"[VideoDebug] {stage}: sequence={sequence} " +
            $"dimensions={width}x{height} stride={stride} format={format} " +
            $"bytes={byteCount} crc32={analysis.Crc32:X8}");
        _log.WriteLine(
            $"[VideoDebug] {stage} pixels: " +
            string.Join(" ", analysis.Samples.Select(static sample =>
                $"({sample.X},{sample.Y})=" +
                $"{sample.Blue},{sample.Green},{sample.Red},{sample.Alpha}")));
        _log.WriteLine(
            $"[VideoDebug] {stage} luminance: min={analysis.MinimumRgb} " +
            $"max={analysis.MaximumRgb} " +
            $"avg={analysis.AverageRgb.ToString("F1", CultureInfo.InvariantCulture)} " +
            $"nonBlack={analysis.NonBlackSamples}/{analysis.GridSamples}");
    }

    private static VideoPixelSample ReadPixel(
        ReadOnlySpan<byte> pixels,
        int stride,
        int x,
        int y,
        PanelDisplayFrameFormat format)
    {
        var offset = checked(y * stride + x * 4);
        return format == PanelDisplayFrameFormat.Bgra8888
            ? new VideoPixelSample(
                x, y,
                pixels[offset],
                pixels[offset + 1],
                pixels[offset + 2],
                pixels[offset + 3])
            : new VideoPixelSample(
                x, y,
                pixels[offset + 2],
                pixels[offset + 1],
                pixels[offset],
                pixels[offset + 3]);
    }

    private static uint[] CreateCrc32Table()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = checked((uint)index);
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0
                    ? polynomial ^ (value >> 1)
                    : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static readonly uint[] Crc32Table = CreateCrc32Table();
}

internal readonly record struct VideoPixelSample(
    int X,
    int Y,
    byte Blue,
    byte Green,
    byte Red,
    byte Alpha);

internal sealed record VideoFrameAnalysis(
    uint Crc32,
    IReadOnlyList<VideoPixelSample> Samples,
    int MinimumRgb,
    int MaximumRgb,
    double AverageRgb,
    int NonBlackSamples,
    int GridSamples);
