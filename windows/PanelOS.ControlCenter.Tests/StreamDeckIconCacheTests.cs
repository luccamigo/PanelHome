using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows;
using PanelOS.Windows.StreamDeck.Icons;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckIconCacheTests
{
    private static readonly byte[] SamplePng = PngHeader(128, 128);

    private string _directory = string.Empty;

    [TestInitialize]
    public void CreateTemporaryDirectory()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "PanelOS.ControlCenter.Tests",
            Guid.NewGuid().ToString("n"));
    }

    [TestCleanup]
    public void RemoveTemporaryDirectory()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task SavingAnImageReturnsAnIdThatResolvesToAFile()
    {
        var cache = new StreamDeckIconCache(_directory);

        var iconId = await cache.SaveAsync(SamplePng, CancellationToken.None);

        Assert.IsTrue(StreamDeckIconCache.IsValidIconId(iconId));
        var path = cache.TryGetIconPath(iconId);
        Assert.IsNotNull(path);
        Assert.IsTrue(File.Exists(path));
        CollectionAssert.AreEqual(SamplePng, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(".png", Path.GetExtension(path));
    }

    [TestMethod]
    public async Task TheSameImageIsStoredOnce()
    {
        var cache = new StreamDeckIconCache(_directory);

        var first = await cache.SaveAsync(SamplePng, CancellationToken.None);
        var second = await cache.SaveAsync(
            [.. SamplePng],
            CancellationToken.None);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public async Task DifferentImagesGetDifferentIds()
    {
        var cache = new StreamDeckIconCache(_directory);

        var first = await cache.SaveAsync(SamplePng, CancellationToken.None);
        var second = await cache.SaveAsync(PngHeader(64, 64), CancellationToken.None);

        Assert.AreNotEqual(first, second);
        Assert.AreEqual(2, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public void UnknownAndMalformedIdsResolveToNothing()
    {
        var cache = new StreamDeckIconCache(_directory);

        Assert.IsNull(cache.TryGetIconPath(null));
        Assert.IsNull(cache.TryGetIconPath(string.Empty));
        Assert.IsNull(cache.TryGetIconPath("icon-desconhecido"));
    }

    [TestMethod]
    public void AnIconIdCannotEscapeTheCacheDirectory()
    {
        // The id is read back from an editable configuration file, so it is
        // validated before it is ever combined into a path.
        foreach (var candidate in new[]
                 {
                     @"..\..\Windows\System32\config",
                     "../../etc/passwd",
                     @"C:\Windows\win.ini",
                     "icon-abc/../../x",
                     "icon abc",
                     "ICON-ABC",
                 })
        {
            Assert.IsFalse(
                StreamDeckIconCache.IsValidIconId(candidate),
                $"{candidate} não é um iconId válido");
            Assert.IsNull(new StreamDeckIconCache(_directory).TryGetIconPath(candidate));
        }
    }

    [TestMethod]
    public void ValidIdsAreLowercaseAsciiWithHyphens()
    {
        Assert.IsTrue(StreamDeckIconCache.IsValidIconId("icon-0123456789abcdef"));
        Assert.IsFalse(StreamDeckIconCache.IsValidIconId("abc"));
        Assert.IsFalse(StreamDeckIconCache.IsValidIconId(new string('a', 65)));
    }

    [TestMethod]
    public void TheDefaultCacheLivesUnderLocalAppData()
    {
        var cache = new StreamDeckIconCache();

        Assert.AreEqual(PanelOSPaths.IconCacheDirectory, cache.Directory);
        StringAssert.Contains(cache.Directory, @"PanelOS\icons");
    }

    [TestMethod]
    public async Task OversizedDimensionsAreRejectedBeforeStorage()
    {
        var cache = new StreamDeckIconCache(_directory);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            cache.SaveAsync(PngHeader(4096, 4096), CancellationToken.None));

        Assert.AreEqual(0, Directory.Exists(_directory)
            ? Directory.GetFiles(_directory).Length
            : 0);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
            .CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }
}
