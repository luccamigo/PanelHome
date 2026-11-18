using System.Security.Cryptography;

namespace PanelOS.Windows.StreamDeck.Icons;

/// <summary>
/// Stores button images on the PC and hands out an opaque id for them.
/// </summary>
/// <remarks>
/// The cache exists so the configuration - and the layout the tablet will
/// eventually receive - can reference an image without carrying either the
/// bytes or a filesystem path. The id is derived from the content, so two
/// buttons pointing at the same image share one file.
/// </remarks>
public interface IStreamDeckIconCache
{
    string Directory { get; }

    /// <summary>
    /// Full path of a cached icon, or null when the id is unknown or malformed.
    /// </summary>
    string? TryGetIconPath(string? iconId);

    /// <summary>Stores PNG bytes and returns the id that addresses them.</summary>
    Task<string> SaveAsync(byte[] pngBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Bytes of a cached icon, or null when the id is unknown, malformed or the
    /// file is larger than <paramref name="maximumBytes"/>.
    /// </summary>
    /// <remarks>
    /// The size limit is checked before reading, not after: the id arrives from
    /// the tablet, and a request must not be able to make PanelHost allocate an
    /// arbitrary buffer because something large happens to sit in the cache.
    /// </remarks>
    Task<byte[]?> TryReadAsync(
        string? iconId,
        int maximumBytes,
        CancellationToken cancellationToken);
}

public sealed class StreamDeckIconCache : IStreamDeckIconCache
{
    /// <summary>
    /// Largest icon that may be handed out. Imported icons are 128x128 PNGs of
    /// a few kilobytes, so this is generous; it exists to bound what a request
    /// can cost, not to reject real icons.
    /// </summary>
    public const int MaximumIconBytes = 512 * 1024;
    public const int MaximumIconDimension = 128;

    private const string FileExtension = ".png";

    private readonly string _directory;

    public StreamDeckIconCache()
        : this(PanelOSPaths.IconCacheDirectory)
    {
    }

    public StreamDeckIconCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public string Directory => _directory;

    public string? TryGetIconPath(string? iconId)
    {
        if (!IsValidIconId(iconId))
        {
            return null;
        }

        var path = Path.Combine(_directory, iconId + FileExtension);
        return File.Exists(path) ? path : null;
    }

    public async Task<byte[]?> TryReadAsync(
        string? iconId,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var path = TryGetIconPath(iconId);
        if (path is null)
        {
            return null;
        }

        try
        {
            if (new FileInfo(path).Length > maximumBytes)
            {
                return null;
            }

            var header = new byte[24];
            await using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true))
            {
                if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) !=
                    header.Length ||
                    !TryGetPngDimensions(header, out var width, out var height) ||
                    width is <= 0 or > MaximumIconDimension ||
                    height is <= 0 or > MaximumIconDimension)
                {
                    return null;
                }
            }

            return await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<string> SaveAsync(
        byte[] pngBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            throw new ArgumentException(
                "Uma imagem vazia não pode ser armazenada.",
                nameof(pngBytes));
        }
        if (pngBytes.Length > MaximumIconBytes ||
            !TryGetPngDimensions(pngBytes, out var width, out var height) ||
            width is <= 0 or > MaximumIconDimension ||
            height is <= 0 or > MaximumIconDimension)
        {
            throw new ArgumentException(
                "O PNG do Ã­cone tem tamanho ou dimensÃµes invÃ¡lidas.",
                nameof(pngBytes));
        }

        var iconId = CreateIconId(pngBytes);
        System.IO.Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, iconId + FileExtension);
        if (File.Exists(path))
        {
            // Content addressed: the same image is already stored.
            return iconId;
        }

        var temporaryPath = path + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, pngBytes, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
        return iconId;
    }

    /// <summary>
    /// Ids are generated here, but they are also read back from a configuration
    /// file that a person can edit, so the shape is validated before it ever
    /// reaches <see cref="Path.Combine(string, string)"/>.
    /// </summary>
    public static bool IsValidIconId(string? iconId)
    {
        if (string.IsNullOrEmpty(iconId) || iconId.Length is < 5 or > 64)
        {
            return false;
        }

        foreach (var character in iconId)
        {
            if (!char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character) &&
                character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string CreateIconId(byte[] pngBytes)
    {
        var hash = SHA256.HashData(pngBytes);
        return "icon-" + Convert.ToHexString(hash, 0, 10).ToLowerInvariant();
    }

    internal static bool TryGetPngDimensions(
        ReadOnlySpan<byte> bytes,
        out int width,
        out int height)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        width = 0;
        height = 0;
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
        height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
        return width > 0 && height > 0;
    }
}
