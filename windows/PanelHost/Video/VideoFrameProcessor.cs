using System.IO.Compression;
using PanelOS.DriverInterop;

namespace PanelHost.Video;

internal static class VideoFrameProcessor
{
    internal const int PreviewMaximumWidth = 640;
    internal const int PreviewMaximumHeight = 400;

    internal static EncodedVideoFrame Process(
        FrameSnapshot source,
        int negotiatedMaxWidth,
        int negotiatedMaxHeight)
    {
        var preview = CreatePreview(
            source,
            negotiatedMaxWidth,
            negotiatedMaxHeight);
        return EncodePreview(preview);
    }

    internal static VideoPreviewFrame CreatePreview(
        FrameSnapshot source,
        int negotiatedMaxWidth,
        int negotiatedMaxHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        var maxWidth = Math.Min(PreviewMaximumWidth, negotiatedMaxWidth);
        var maxHeight = Math.Min(PreviewMaximumHeight, negotiatedMaxHeight);
        if (maxWidth <= 0 || maxHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(negotiatedMaxWidth),
                "Negotiated video dimensions must be positive.");
        }

        ValidateSource(source);
        var (width, height) = FitInside(
            source.Width,
            source.Height,
            maxWidth,
            maxHeight);
        var pixels = NormalizeAndResize(source, width, height);

        return new VideoPreviewFrame(
            source.Sequence,
            source.CaptureTimestampMicros,
            width,
            height,
            pixels);
    }

    internal static EncodedVideoFrame EncodePreview(VideoPreviewFrame preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Sequence == 0 ||
            preview.Width <= 0 || preview.Height <= 0 ||
            preview.Width > PreviewMaximumWidth ||
            preview.Height > PreviewMaximumHeight ||
            preview.Pixels.Length != checked(preview.Width * preview.Height * 4))
        {
            throw new InvalidDataException("Video preview contains invalid pixels.");
        }

        using var compressed = new MemoryStream(
            Math.Min(preview.Pixels.Length, 256 * 1024));
        using (var zlib = new ZLibStream(
            compressed,
            CompressionLevel.Fastest,
            leaveOpen: true))
        {
            zlib.Write(preview.Pixels);
        }

        return new EncodedVideoFrame(
            preview.Sequence,
            preview.CaptureTimestampMicros,
            checked((uint)preview.Width),
            checked((uint)preview.Height),
            checked((uint)preview.Pixels.Length),
            compressed.ToArray());
    }

    internal static byte[] NormalizeAndResize(
        FrameSnapshot source,
        int targetWidth,
        int targetHeight)
    {
        ValidateSource(source);
        if (targetWidth <= 0 || targetHeight <= 0 ||
            targetWidth > source.Width || targetHeight > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWidth),
                "Target dimensions must be positive and cannot upscale.");
        }

        var destination = new byte[checked(targetWidth * targetHeight * 4)];
        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = checked(y * source.Height / targetHeight);
            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = checked(x * source.Width / targetWidth);
                var sourceOffset = checked(
                    sourceY * source.Stride + sourceX * 4);
                var destinationOffset = checked((y * targetWidth + x) * 4);
                if (source.Format == PanelDisplayFrameFormat.Bgra8888)
                {
                    destination[destinationOffset] = source.Pixels[sourceOffset];
                    destination[destinationOffset + 1] =
                        source.Pixels[sourceOffset + 1];
                    destination[destinationOffset + 2] =
                        source.Pixels[sourceOffset + 2];
                }
                else
                {
                    destination[destinationOffset] = source.Pixels[sourceOffset + 2];
                    destination[destinationOffset + 1] =
                        source.Pixels[sourceOffset + 1];
                    destination[destinationOffset + 2] =
                        source.Pixels[sourceOffset];
                }

                // DXGI swapchain alpha is not a desktop transparency contract.
                // Canonical video frames are always opaque so alpha=0 cannot
                // make otherwise valid desktop pixels disappear on Android.
                destination[destinationOffset + 3] = 0xFF;
            }
        }

        return destination;
    }

    internal static (int Width, int Height) FitInside(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int maximumHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 ||
            maximumWidth <= 0 || maximumHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        }

        if (sourceWidth <= maximumWidth && sourceHeight <= maximumHeight)
        {
            return (sourceWidth, sourceHeight);
        }

        var widthScale = (double)maximumWidth / sourceWidth;
        var heightScale = (double)maximumHeight / sourceHeight;
        var scale = Math.Min(widthScale, heightScale);
        return (
            Math.Max(1, (int)Math.Floor(sourceWidth * scale)),
            Math.Max(1, (int)Math.Floor(sourceHeight * scale)));
    }

    private static void ValidateSource(FrameSnapshot source)
    {
        if (source.Sequence == 0 || source.Width <= 0 || source.Height <= 0 ||
            source.Width > 16_384 || source.Height > 16_384 ||
            source.Stride < checked(source.Width * 4) ||
            source.Format is not (
                PanelDisplayFrameFormat.Bgra8888 or
                PanelDisplayFrameFormat.Rgba8888) ||
            source.Pixels.Length != checked(source.Stride * source.Height))
        {
            throw new InvalidDataException("Frame source returned invalid pixels.");
        }
    }
}

internal sealed record EncodedVideoFrame(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    uint Width,
    uint Height,
    uint UncompressedBytes,
    byte[] CompressedPixels);

internal sealed record VideoPreviewFrame(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    int Width,
    int Height,
    byte[] Pixels)
{
    internal int Stride => checked(Width * 4);
}
