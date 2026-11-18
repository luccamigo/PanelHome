using PanelOS.DriverInterop;

namespace PanelHost.Video;

internal readonly record struct Nv12VideoFrame(
    ulong Sequence,
    ulong CaptureTimestampMicros,
    int Width,
    int Height,
    byte[] Pixels)
{
    internal int ExpectedByteCount => checked(Width * Height * 3 / 2);
}

internal interface IVideoFrameConverter
{
    // CPU conversion is the correctness-first boundary for this milestone.
    // A later implementation can receive the D3D11 texture, use a D3D11 Video
    // Processor to produce NV12, and feed the hardware MFT without changing the
    // encoder or transport contracts.
    // The returned pixel buffer remains valid until the next Convert call on
    // the same converter instance. The video consumer is intentionally
    // sequential: Media Foundation copies the sample before another frame is
    // converted.
    Nv12VideoFrame Convert(FrameSnapshot source, int targetWidth, int targetHeight);
}

internal sealed class BgraToNv12FrameConverter : IVideoFrameConverter
{
    private byte[] _pixels = [];
    private int[] _sourceXOffsets = [];
    private int[] _sourceYOffsets = [];
    private ConversionGeometry _geometry;

    public Nv12VideoFrame Convert(
        FrameSnapshot source,
        int targetWidth,
        int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        Validate(source, targetWidth, targetHeight);

        PrepareGeometry(source, targetWidth, targetHeight);
        var pixels = _pixels;
        var sourcePixels = source.Pixels;
        var sourceXOffsets = _sourceXOffsets;
        var sourceYOffsets = _sourceYOffsets;
        var yPlaneBytes = checked(targetWidth * targetHeight);
        var redOffset = source.Format == PanelDisplayFrameFormat.Bgra8888 ? 2 : 0;
        var blueOffset = source.Format == PanelDisplayFrameFormat.Bgra8888 ? 0 : 2;

        if (source.Width == targetWidth && source.Height == targetHeight)
        {
            ConvertNativeResolution(
                sourcePixels,
                pixels,
                source.Stride,
                targetWidth,
                targetHeight,
                yPlaneBytes,
                redOffset,
                blueOffset);
        }
        else
        {
            ConvertResized(
                sourcePixels,
                pixels,
                sourceXOffsets,
                sourceYOffsets,
                targetWidth,
                targetHeight,
                yPlaneBytes,
                redOffset,
                blueOffset);
        }

        return new Nv12VideoFrame(
            source.Sequence,
            source.CaptureTimestampMicros,
            targetWidth,
            targetHeight,
            pixels);
    }

    private static void ConvertNativeResolution(
        byte[] sourcePixels,
        byte[] pixels,
        int sourceStride,
        int targetWidth,
        int targetHeight,
        int yPlaneBytes,
        int redOffset,
        int blueOffset)
    {
        // Native mode deliberately contains no coordinate maps or resize math.
        // It walks the padded BGRA/RGBA rows directly and writes one NV12 2x2
        // block per source block.
        for (var targetY = 0; targetY < targetHeight; targetY += 2)
        {
            var sourceRow0 = targetY * sourceStride;
            var sourceRow1 = sourceRow0 + sourceStride;
            var yRow0 = targetY * targetWidth;
            var yRow1 = yRow0 + targetWidth;
            var chromaRow = yPlaneBytes + (targetY / 2) * targetWidth;
            var offset00 = sourceRow0;
            var offset10 = sourceRow1;
            for (var targetX = 0; targetX < targetWidth; targetX += 2)
            {
                var offset01 = offset00 + 4;
                var offset11 = offset10 + 4;

                var red00 = sourcePixels[offset00 + redOffset];
                var green00 = sourcePixels[offset00 + 1];
                var blue00 = sourcePixels[offset00 + blueOffset];
                var red01 = sourcePixels[offset01 + redOffset];
                var green01 = sourcePixels[offset01 + 1];
                var blue01 = sourcePixels[offset01 + blueOffset];
                var red10 = sourcePixels[offset10 + redOffset];
                var green10 = sourcePixels[offset10 + 1];
                var blue10 = sourcePixels[offset10 + blueOffset];
                var red11 = sourcePixels[offset11 + redOffset];
                var green11 = sourcePixels[offset11 + 1];
                var blue11 = sourcePixels[offset11 + blueOffset];

                pixels[yRow0 + targetX] = ToLuma(red00, green00, blue00);
                pixels[yRow0 + targetX + 1] = ToLuma(red01, green01, blue01);
                pixels[yRow1 + targetX] = ToLuma(red10, green10, blue10);
                pixels[yRow1 + targetX + 1] = ToLuma(red11, green11, blue11);

                var red = (red00 + red01 + red10 + red11 + 2) / 4;
                var green = (green00 + green01 + green10 + green11 + 2) / 4;
                var blue = (blue00 + blue01 + blue10 + blue11 + 2) / 4;
                var chromaOffset = chromaRow + targetX;
                pixels[chromaOffset] = ToChromaU(red, green, blue);
                pixels[chromaOffset + 1] = ToChromaV(red, green, blue);
                offset00 += 8;
                offset10 += 8;
            }
        }
    }

    private static void ConvertResized(
        byte[] sourcePixels,
        byte[] pixels,
        int[] sourceXOffsets,
        int[] sourceYOffsets,
        int targetWidth,
        int targetHeight,
        int yPlaneBytes,
        int redOffset,
        int blueOffset)
    {
        // NV12 chroma describes a 2x2 luma block. Producing all four Y samples
        // and their averaged UV pair together reads each resized source pixel
        // once and keeps coordinate division outside the hot loop.
        for (var targetY = 0; targetY < targetHeight; targetY += 2)
        {
            var sourceRow0 = sourceYOffsets[targetY];
            var sourceRow1 = sourceYOffsets[targetY + 1];
            var yRow0 = targetY * targetWidth;
            var yRow1 = yRow0 + targetWidth;
            var chromaRow = yPlaneBytes + (targetY / 2) * targetWidth;
            for (var targetX = 0; targetX < targetWidth; targetX += 2)
            {
                var sourceColumn0 = sourceXOffsets[targetX];
                var sourceColumn1 = sourceXOffsets[targetX + 1];
                var offset00 = sourceRow0 + sourceColumn0;
                var offset01 = sourceRow0 + sourceColumn1;
                var offset10 = sourceRow1 + sourceColumn0;
                var offset11 = sourceRow1 + sourceColumn1;

                var red00 = sourcePixels[offset00 + redOffset];
                var green00 = sourcePixels[offset00 + 1];
                var blue00 = sourcePixels[offset00 + blueOffset];
                var red01 = sourcePixels[offset01 + redOffset];
                var green01 = sourcePixels[offset01 + 1];
                var blue01 = sourcePixels[offset01 + blueOffset];
                var red10 = sourcePixels[offset10 + redOffset];
                var green10 = sourcePixels[offset10 + 1];
                var blue10 = sourcePixels[offset10 + blueOffset];
                var red11 = sourcePixels[offset11 + redOffset];
                var green11 = sourcePixels[offset11 + 1];
                var blue11 = sourcePixels[offset11 + blueOffset];

                pixels[yRow0 + targetX] = ToLuma(red00, green00, blue00);
                pixels[yRow0 + targetX + 1] = ToLuma(red01, green01, blue01);
                pixels[yRow1 + targetX] = ToLuma(red10, green10, blue10);
                pixels[yRow1 + targetX + 1] = ToLuma(red11, green11, blue11);

                var red = (red00 + red01 + red10 + red11 + 2) / 4;
                var green = (green00 + green01 + green10 + green11 + 2) / 4;
                var blue = (blue00 + blue01 + blue10 + blue11 + 2) / 4;
                var chromaOffset = chromaRow + targetX;
                pixels[chromaOffset] = ToChromaU(red, green, blue);
                pixels[chromaOffset + 1] = ToChromaV(red, green, blue);
            }
        }
    }

    private void PrepareGeometry(
        FrameSnapshot source,
        int targetWidth,
        int targetHeight)
    {
        var geometry = new ConversionGeometry(
            source.Width,
            source.Height,
            source.Stride,
            targetWidth,
            targetHeight);
        if (_geometry == geometry)
        {
            return;
        }

        _pixels = new byte[checked(targetWidth * targetHeight * 3 / 2)];
        var nativeResolution = source.Width == targetWidth &&
            source.Height == targetHeight;
        _sourceXOffsets = nativeResolution ? [] : new int[targetWidth];
        _sourceYOffsets = nativeResolution ? [] : new int[targetHeight];
        if (nativeResolution)
        {
            _geometry = geometry;
            return;
        }
        for (var targetX = 0; targetX < targetWidth; targetX++)
        {
            _sourceXOffsets[targetX] = checked(
                targetX * source.Width / targetWidth * 4);
        }

        for (var targetY = 0; targetY < targetHeight; targetY++)
        {
            _sourceYOffsets[targetY] = checked(
                targetY * source.Height / targetHeight * source.Stride);
        }

        _geometry = geometry;
    }

    private static void Validate(
        FrameSnapshot source,
        int targetWidth,
        int targetHeight)
    {
        if (source.Sequence == 0 || source.Width <= 0 || source.Height <= 0 ||
            source.Stride < checked(source.Width * 4) ||
            source.Pixels.Length != checked(source.Stride * source.Height) ||
            source.Format is not (
                PanelDisplayFrameFormat.Bgra8888 or
                PanelDisplayFrameFormat.Rgba8888) ||
            targetWidth <= 0 || targetHeight <= 0 ||
            targetWidth > source.Width || targetHeight > source.Height ||
            (targetWidth & 1) != 0 || (targetHeight & 1) != 0)
        {
            throw new InvalidDataException("Cannot convert the supplied frame to NV12.");
        }
    }

    // BT.709 limited-range conversion. Integer arithmetic is deterministic
    // across the production encoder and host-side regression tests.
    private static byte ToLuma(int red, int green, int blue) =>
        Clamp(((47 * red + 157 * green + 16 * blue + 128) >> 8) + 16);

    private static byte ToChromaU(int red, int green, int blue) =>
        Clamp(((-26 * red - 87 * green + 112 * blue + 128) >> 8) + 128);

    private static byte ToChromaV(int red, int green, int blue) =>
        Clamp(((112 * red - 102 * green - 10 * blue + 128) >> 8) + 128);

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private readonly record struct ConversionGeometry(
        int SourceWidth,
        int SourceHeight,
        int SourceStride,
        int TargetWidth,
        int TargetHeight);
}
