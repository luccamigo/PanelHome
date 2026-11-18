using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.ControlCenter.Services;
using PanelOS.ControlCenter.ViewModels;
using PanelOS.Windows.LoopMedia;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class LoopMediaTests
{
    private string _directory = null!;
    private JsonLoopMediaStore _store = null!;
    private readonly CancellationToken _token = CancellationToken.None;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PanelOS.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new(Path.Combine(_directory, "media.json"), Path.Combine(_directory, "media"));
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public async Task ImportDetectsContentHashesCopiesAndReplacesOnlyAfterSave()
    {
        var gifBytes = new byte[2 * 1024 * 1024];
        "GIF89a"u8.CopyTo(gifBytes);
        var gif = await SourceAsync("renamed.mp4", gifBytes);
        var first = await _store.ImportAsync(gif, _token);
        Assert.AreEqual("gif", first.Kind);
        Assert.AreEqual("image/gif", first.Mime);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(gifBytes)).ToLowerInvariant(), first.MediaId);
        Assert.AreEqual((long)gifBytes.Length, first.SizeBytes);
        Assert.AreEqual("renamed.mp4", first.OriginalFileName);
        CollectionAssert.AreEqual(gifBytes, await File.ReadAllBytesAsync(_store.GetMediaPath(first)));
        Assert.AreEqual(first, await _store.LoadAsync(_token));
        Assert.IsTrue(_store.GetMediaPath(first).EndsWith(".gif"));

        var mp4 = await SourceAsync("renamed.gif", new byte[] { 0, 0, 0, 20, 102, 116, 121, 112, 1 });
        var second = await _store.ImportAsync(mp4, _token);
        Assert.AreEqual("video", second.Kind);
        Assert.AreEqual("video/mp4", second.Mime);
        Assert.IsTrue(File.Exists(_store.GetMediaPath(second)));
        Assert.IsFalse(File.Exists(_store.GetMediaPath(first)));
        await _store.RemoveAsync(_token);
        Assert.IsNull((await _store.LoadAsync(_token)).MediaId);
        Assert.AreEqual("{\"schemaVersion\":1}", await File.ReadAllTextAsync(_store.Location));
        Assert.AreEqual(0, Directory.GetFiles(_store.MediaDirectory).Length);
    }

    [TestMethod]
    public async Task InvalidAndOversizeFilesLeavePreviousConfigurationAndBytesIntact()
    {
        var first = await _store.ImportAsync(await SourceAsync("good.gif", "GIF87a"u8.ToArray()), _token);
        var json = await File.ReadAllTextAsync(_store.Location);
        var invalid = await SourceAsync("fake.gif", new byte[] { 137, 80, 78, 71 });
        var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => _store.ImportAsync(invalid, _token));
        Assert.AreEqual(LoopMediaValidator.UnsupportedFormat, error.Message);
        var large = Path.Combine(_directory, "large.mp4");
        using (var file = File.Create(large)) file.SetLength(LoopMediaValidator.MaximumBytes + 1);
        error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => _store.ImportAsync(large, _token));
        Assert.AreEqual(LoopMediaValidator.TooLarge, error.Message);
        var empty = await SourceAsync("empty.gif", []);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => _store.ImportAsync(empty, _token));
        Assert.AreEqual(json, await File.ReadAllTextAsync(_store.Location));
        Assert.IsTrue(File.Exists(_store.GetMediaPath(first)));
        Assert.AreEqual(1, Directory.GetFiles(_store.MediaDirectory).Length);
    }

    [TestMethod]
    public async Task Exact64MiBLimitIsAcceptedAndCopiedCompletely()
    {
        var source = Path.Combine(_directory, "limit.gif");
        await using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
        {
            await file.WriteAsync("GIF89a"u8.ToArray());
            file.SetLength(67_108_864);
        }
        var media = await _store.ImportAsync(source, _token);
        Assert.AreEqual(67_108_864L, media.SizeBytes);
        Assert.AreEqual("gif", media.Kind);
        var copied = _store.GetMediaPath(media);
        Assert.AreEqual(67_108_864L, new FileInfo(copied).Length);
        Assert.AreEqual((await HashAsync(source)), await HashAsync(copied));
        Assert.AreEqual(media, await _store.LoadAsync(_token));
    }

    [TestMethod]
    public async Task FailedSaveWhenReimportingCurrentFilePreservesItsConfigurationAndBytes()
    {
        var bytes = "GIF89a"u8.ToArray();
        var source = await SourceAsync("current.gif", bytes);
        var current = await _store.ImportAsync(source, _token);
        var json = await File.ReadAllTextAsync(_store.Location);
        using (var locked = new FileStream(_store.Location, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => _store.ImportAsync(source, _token));
        }
        Assert.AreEqual(json, await File.ReadAllTextAsync(_store.Location));
        Assert.AreEqual(current, await _store.LoadAsync(_token));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(_store.GetMediaPath(current)));
        Assert.AreEqual(1, Directory.GetFiles(_store.MediaDirectory).Length);
        Assert.AreEqual(0, Directory.GetFiles(_store.MediaDirectory, "*.tmp").Length);
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }

    [TestMethod]
    public async Task FailedAtomicSavePreservesPreviousMediaAndCleansNewCopy()
    {
        var first = await _store.ImportAsync(await SourceAsync("first.gif", "GIF89a"u8.ToArray()), _token);
        var second = await SourceAsync("second.gif", "GIF87a"u8.ToArray());
        using (var locked = new FileStream(_store.Location, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsExceptionAsync<UnauthorizedAccessException>(() => _store.ImportAsync(second, _token));
        }
        Assert.AreEqual(first, await _store.LoadAsync(_token));
        Assert.AreEqual(1, Directory.GetFiles(_store.MediaDirectory).Length);
        Assert.AreEqual(0, Directory.GetFiles(_directory, "*.tmp").Length);
    }

    [DataTestMethod]
    [DataRow("{")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{\"schemaVersion\":2}")]
    [DataRow("{\"schemaVersion\":1,\"mediaId\":\"../../secret\"}")]
    [DataRow("{\"schemaVersion\":1,\"sizeBytes\":4}")]
    public async Task InvalidConfigurationIsEmptyWithoutCrash(string json)
    {
        var logs = new List<string>();
        var store = new JsonLoopMediaStore(_store.Location, _store.MediaDirectory, logs.Add);
        await File.WriteAllTextAsync(store.Location, json);
        Assert.IsNull((await store.LoadAsync(_token)).MediaId);
        Assert.AreEqual(1, logs.Count);
        Assert.IsFalse(logs[0].Contains(_directory));
    }

    // The text is Portuguese, so the size is grouped as pt-BR ("1.500") whatever the machine culture is (a CI runner is en-US).
    [DataTestMethod]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("")]
    [DataRow("pt-BR")]
    public async Task ViewModelFormatsTheSizeAsPortugueseWhateverTheMachineCulture(string machineCulture)
    {
        var picker = new Picker();
        var viewModel = new LoopMediaViewModel(_store, picker);
        await viewModel.LoadAsync(_token);
        picker.Path = await SourceAsync("sized.gif", [.. "GIF89a"u8.ToArray(), .. new byte[1494]]);
        await viewModel.ChooseCommand.ExecuteAsync(_token);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(machineCulture);

            Assert.AreEqual("GIF · 1.500 bytes", viewModel.Details);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public async Task ViewModelReportsImportRemoveFormatSizeAndReadErrorsInline()
    {
        var picker = new Picker();
        var viewModel = new LoopMediaViewModel(_store, picker);
        await viewModel.LoadAsync(_token);
        Assert.AreEqual("Nenhuma mídia configurada", viewModel.FileName);
        Assert.IsFalse(viewModel.RemoveCommand.CanExecute(null));
        picker.Path = await SourceAsync("current.gif", "GIF89a"u8.ToArray());
        await viewModel.ChooseCommand.ExecuteAsync(_token);
        Assert.AreEqual("current.gif", viewModel.FileName);
        StringAssert.Contains(viewModel.Details, "GIF");
        Assert.IsTrue(viewModel.RemoveCommand.CanExecute(null));
        await viewModel.ImportAsync(await SourceAsync("png.gif", "PNG"u8.ToArray()), _token);
        Assert.AreEqual(LoopMediaValidator.UnsupportedFormat, viewModel.Error);
        await viewModel.ImportAsync(Path.Combine(_directory, "missing.gif"), _token);
        Assert.AreEqual(LoopMediaValidator.Unreadable, viewModel.Error);
        using (var locked = new FileStream(picker.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            await viewModel.ImportAsync(picker.Path, _token);
        Assert.AreEqual(LoopMediaValidator.Unreadable, viewModel.Error);
        var large = Path.Combine(_directory, "large.gif");
        using (var file = File.Create(large)) file.SetLength(LoopMediaValidator.MaximumBytes + 1);
        await viewModel.ImportAsync(large, _token);
        Assert.AreEqual(LoopMediaValidator.TooLarge, viewModel.Error);
        Assert.AreEqual("current.gif", viewModel.FileName);
        picker.Path = null;
        await viewModel.ChooseCommand.ExecuteAsync(_token);
        Assert.AreEqual("current.gif", viewModel.FileName);
        await viewModel.RemoveCommand.ExecuteAsync(_token);
        Assert.AreEqual("Nenhuma mídia configurada", viewModel.FileName);
        Assert.AreEqual(string.Empty, viewModel.Error);
        Assert.IsFalse(viewModel.RemoveCommand.CanExecute(null));
    }

    [TestMethod]
    public void ValidatorRejectsTraversalUppercaseAndInvalidMetadata()
    {
        foreach (var id in new[] { "../../x", new string('A', 64), new string('a', 63), new string('g', 64) })
            Assert.IsFalse(LoopMediaValidator.IsValidMediaId(id));
        Assert.IsTrue(LoopMediaValidator.IsValidMediaId(new string('a', 64)));
        var valid = new LoopMediaConfiguration
        {
            MediaId = new string('a', 64), Kind = "gif", Mime = "image/gif", SizeBytes = 1,
        };
        Assert.IsTrue(LoopMediaValidator.IsValid(valid));
        Assert.IsTrue(LoopMediaValidator.IsValid(valid with { SizeBytes = LoopMediaValidator.MaximumBytes }));
        Assert.IsFalse(LoopMediaValidator.IsValid(valid with { SizeBytes = 0 }));
        Assert.IsFalse(LoopMediaValidator.IsValid(valid with { SizeBytes = LoopMediaValidator.MaximumBytes + 1 }));
        Assert.IsFalse(LoopMediaValidator.IsValid(valid with { Mime = "video/mp4" }));
        Assert.ThrowsException<InvalidDataException>(() => _store.GetMediaPath(valid with { MediaId = "../../x" }));
    }

    private async Task<string> SourceAsync(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private sealed class Picker : ILoopMediaFilePicker
    {
        public string? Path { get; set; }
        public string? PickFile() => Path;
    }
}
