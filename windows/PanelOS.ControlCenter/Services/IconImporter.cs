using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PanelOS.Windows.StreamDeck.Icons;

namespace PanelOS.ControlCenter.Services;

/// <summary>
/// Turns a picture - a chosen image or an application's own icon - into a PNG
/// in the icon cache, and answers with the id that addresses it.
/// </summary>
public interface IIconImporter
{
    Task<string?> ImportImageAsync(
        string imagePath,
        CancellationToken cancellationToken);

    Task<string?> ImportExecutableIconAsync(
        string executablePath,
        CancellationToken cancellationToken);

    /// <summary>Path of a cached icon for previewing, or null.</summary>
    string? ResolveIconPath(string? iconId);
}

/// <summary>
/// WPF imaging plus the Win32 icon APIs. Both are in the box for a Windows
/// desktop application, so no imaging package is added for this.
/// </summary>
public sealed class IconImporter : IIconImporter
{
    /// <summary>Cached size. Big enough for a tablet button, small on disk.</summary>
    private const int IconSize = 128;
    private const int MaximumSourceDimension = 4096;
    private const long MaximumSourcePixels = 16L * 1024 * 1024;
    private const long MaximumSourceFileBytes = 32L * 1024 * 1024;

    private readonly IStreamDeckIconCache _cache;

    public IconImporter(IStreamDeckIconCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    public string? ResolveIconPath(string? iconId) => _cache.TryGetIconPath(iconId);

    public async Task<string?> ImportImageAsync(
        string imagePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
        {
            return null;
        }
        if (new FileInfo(imagePath).Length > MaximumSourceFileBytes)
        {
            return null;
        }

        try
        {
            var decoder = BitmapDecoder.Create(
                new Uri(imagePath, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            // An .ico holds several sizes; take the largest before scaling so
            // the result is as sharp as the file allows.
            var frame = decoder.Frames
                .OrderByDescending(candidate => candidate.PixelWidth)
                .First();
            if (frame.PixelWidth is <= 0 or > MaximumSourceDimension ||
                frame.PixelHeight is <= 0 or > MaximumSourceDimension ||
                (long)frame.PixelWidth * frame.PixelHeight > MaximumSourcePixels)
            {
                return null;
            }
            return await SaveAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is NotSupportedException or ArgumentException or
                FileFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<string?> ImportExecutableIconAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!File.Exists(executablePath))
        {
            return null;
        }

        var source = TryExtractIcon(executablePath);
        return source is null
            ? null
            : await SaveAsync(source, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SaveAsync(
        BitmapSource source,
        CancellationToken cancellationToken)
    {
        var png = Encode(Fit(source));
        return png.Length == 0
            ? null
            : await _cache.SaveAsync(png, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Scales onto a square canvas, keeping the aspect ratio and the alpha
    /// channel; a wide image is centred rather than stretched.
    /// </summary>
    private static BitmapSource Fit(BitmapSource source)
    {
        var scale = Math.Min(
            (double)IconSize / source.PixelWidth,
            (double)IconSize / source.PixelHeight);
        var width = Math.Max(1, source.PixelWidth * scale);
        var height = Math.Max(1, source.PixelHeight * scale);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            context.DrawImage(
                source,
                new Rect(
                    (IconSize - width) / 2,
                    (IconSize - height) / 2,
                    width,
                    height));
        }

        var target = new RenderTargetBitmap(
            IconSize,
            IconSize,
            96,
            96,
            PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static byte[] Encode(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource? TryExtractIcon(string executablePath)
    {
        // PrivateExtractIcons asks for the largest icon the file actually has,
        // instead of the 32x32 the associated-icon API returns.
        var handles = new IntPtr[1];
        var ids = new uint[1];
        var extracted = NativeMethods.PrivateExtractIcons(
            executablePath,
            0,
            IconSize,
            IconSize,
            handles,
            ids,
            1,
            0);
        if (extracted <= 0 || handles[0] == IntPtr.Zero)
        {
            return TryExtractAssociatedIcon(executablePath);
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                handles[0],
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(handles[0]);
        }
    }

    private static BitmapSource? TryExtractAssociatedIcon(string executablePath)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(
                executablePath);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or COMException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern int PrivateExtractIcons(
            string fileName,
            int iconIndex,
            int cxIcon,
            int cyIcon,
            IntPtr[] icons,
            uint[] iconIds,
            uint iconCount,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr icon);
    }
}
