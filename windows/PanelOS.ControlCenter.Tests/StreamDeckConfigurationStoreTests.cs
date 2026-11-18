// A WPF-enabled project drops System.IO from the SDK's implicit usings.
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Serialization;

namespace PanelOS.ControlCenter.Tests;

[TestClass]
public sealed class StreamDeckConfigurationStoreTests
{
    private string _directory = string.Empty;
    private string _filePath = string.Empty;

    [TestInitialize]
    public void CreateTemporaryDirectory()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "PanelOS.ControlCenter.Tests",
            Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        _filePath = Path.Combine(_directory, "streamdeck.json");
    }

    [TestCleanup]
    public void RemoveTemporaryDirectory()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task MissingFileYieldsTheDefaultProfile()
    {
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(StreamDeckConfigurationSource.Defaults, result.Source);
        Assert.IsTrue(result.CanSave);
        Assert.AreEqual(
            StreamDeckDefaults.MainProfileId,
            result.Configuration.Profiles[0].Id);
        Assert.AreEqual(3, result.Configuration.Profiles[0].Pages[0].Rows);
        Assert.AreEqual(4, result.Configuration.Profiles[0].Pages[0].Columns);
        Assert.IsFalse(File.Exists(_filePath), "loading must not create the file");
    }

    [TestMethod]
    public async Task SavedConfigurationIsReadBack()
    {
        var store = new JsonStreamDeckConfigurationStore(_filePath);
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Buttons[0].Label = "Editado";

        await store.SaveAsync(configuration, CancellationToken.None);
        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(StreamDeckConfigurationSource.Loaded, result.Source);
        Assert.AreEqual(
            "Editado",
            result.Configuration.Profiles[0].Pages[0].Buttons[0].Label);
    }

    [TestMethod]
    public async Task SaveWritesNoTemporaryFileBehind()
    {
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        await store.SaveAsync(
            StreamDeckDefaults.CreateConfiguration(),
            CancellationToken.None);

        CollectionAssert.AreEquivalent(
            new[] { "streamdeck.json" },
            Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }

    [TestMethod]
    public async Task CorruptFileFallsBackToDefaultsAndKeepsTheOriginal()
    {
        await File.WriteAllTextAsync(_filePath, "{ this is not json", Encoding.UTF8);
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            result.Source);
        Assert.IsTrue(result.CanSave);
        Assert.AreEqual(
            StreamDeckDefaults.MainProfileId,
            result.Configuration.Profiles[0].Id);
        Assert.IsNotNull(result.QuarantinedFilePath);
        Assert.IsTrue(File.Exists(result.QuarantinedFilePath));
        Assert.IsFalse(File.Exists(_filePath));
    }

    [TestMethod]
    public async Task StructurallyInvalidFileFallsBackToDefaults()
    {
        var configuration = StreamDeckDefaults.CreateConfiguration();
        var page = configuration.Profiles[0].Pages[0];

        // Two buttons sharing an id: a tablet invocation could not be resolved.
        page.Buttons[1].Id = page.Buttons[0].Id;
        await File.WriteAllTextAsync(
            _filePath,
            StreamDeckJson.Serialize(configuration),
            Encoding.UTF8);
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            result.Source);
        StringAssert.Contains(result.Detail!, page.Buttons[0].Id);
    }

    [TestMethod]
    public async Task NewerSchemaIsNotOverwritten()
    {
        const string newer = """{"version": 99, "profiles": []}""";
        await File.WriteAllTextAsync(_filePath, newer, Encoding.UTF8);
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(StreamDeckConfigurationSource.UnsupportedSchema, result.Source);
        Assert.IsFalse(result.CanSave);
        Assert.AreEqual(newer, await File.ReadAllTextAsync(_filePath));
    }

    [TestMethod]
    public async Task InvalidConfigurationIsRejectedBeforeAnythingIsWritten()
    {
        var store = new JsonStreamDeckConfigurationStore(_filePath);
        var configuration = StreamDeckDefaults.CreateConfiguration();
        configuration.Profiles[0].Pages[0].Rows = 99;

        var exception = await Assert.ThrowsExceptionAsync<StreamDeckConfigurationException>(
            () => store.SaveAsync(configuration, CancellationToken.None));

        Assert.IsTrue(exception.Errors.Count > 0);
        Assert.IsFalse(File.Exists(_filePath));
    }

    [TestMethod]
    public void DefaultLocationLivesUnderLocalAppData()
    {
        var store = new JsonStreamDeckConfigurationStore();

        var expected = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify),
            "PanelOS",
            "streamdeck.json");
        Assert.AreEqual(expected, store.Location);
    }

    [TestMethod]
    public async Task OversizedFileIsRejectedBeforeJsonMaterialization()
    {
        await File.WriteAllBytesAsync(
            _filePath,
            new byte[StreamDeckValidator.MaximumConfigurationBytes + 1]);
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            result.Source);
        StringAssert.Contains(result.Detail!, "limite de bytes");
    }

    [TestMethod]
    public async Task ExcessiveCardinalityIsRejectedBeforeBindingModels()
    {
        var profiles = string.Join(",", Enumerable.Range(
            0,
            StreamDeckValidator.MaximumProfiles + 1).Select(index =>
                $$"""{"id":"p{{index}}","name":"P","pages":[]}"""));
        await File.WriteAllTextAsync(
            _filePath,
            $$"""{"version":2,"profiles":[{{profiles}}]}""",
            Encoding.UTF8);
        var store = new JsonStreamDeckConfigurationStore(_filePath);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(
            StreamDeckConfigurationSource.RecoveredFromInvalidFile,
            result.Source);
    }
}
