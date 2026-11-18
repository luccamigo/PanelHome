using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Services;
using PanelOS.Windows.LoopMedia;
using Harness = PanelHost.Tests.StreamDeckSessionTests.StreamDeckHarness;

namespace PanelHost.Tests;

[TestClass]
public sealed class LoopMediaSessionTests
{
    private string _directory = null!;
    private JsonLoopMediaStore _store = null!;
    private LoopMediaService _service = null!;
    private ConcurrentQueue<string> _logs = null!;
    private readonly CancellationToken _token = CancellationToken.None;

    [TestInitialize]
    public async Task Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PanelHost.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _logs = new();
        _store = new(Path.Combine(_directory, "media.json"), Path.Combine(_directory, "media"), _logs.Enqueue);
        _service = new(_store, _logs.Enqueue);
        await _service.StartAsync(_token);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await _service.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public async Task WatcherPublishesImportReplacementRemovalAndCorruptionWithoutRestart()
    {
        await using var harness = await Harness.StartAsync(
            clientFeatures: [ProtocolFeatures.LoopMedia, ProtocolFeatures.StreamDeck], loopMediaService: _service);
        Assert.IsTrue(harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.LoopMedia));
        Assert.IsFalse((await StateAsync(harness)).Available);
        var source = Path.Combine(_directory, "private-name.gif");
        var bytes = new byte[2 * 1024 * 1024];
        "GIF89a"u8.CopyTo(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        var first = await _store.ImportAsync(source, _token);
        var state = await StateAsync(harness);
        Assert.IsTrue(state.Available);
        Assert.AreEqual(first.MediaId, state.MediaId);
        await File.WriteAllBytesAsync(source, "GIF87a"u8.ToArray());
        var second = await _store.ImportAsync(source, _token);
        state = await StateAsync(harness);
        Assert.AreEqual(second.MediaId, state.MediaId);
        Assert.IsFalse(File.Exists(_store.GetMediaPath(first)));
        await RequestAsync(harness, first.MediaId!, 0, "stale_media");
        await _store.RemoveAsync(_token);
        Assert.IsFalse((await StateAsync(harness)).Available);
        await File.WriteAllTextAsync(_store.Location, "{broken");
        Assert.IsFalse((await StateAsync(harness)).Available);
        Assert.IsTrue(_logs.Any(line => line.Contains("configuration unavailable")));
        Assert.IsTrue(_logs.All(line => line.StartsWith("[LoopMedia] ") &&
            !line.Contains(_directory) && !line.Contains("private-name")));
    }

    [TestMethod]
    public async Task PullReconstructs600KiBWithBoundedChunksAndRejectsBadRequests()
    {
        var bytes = new byte[600 * 1024];
        new Random(12).NextBytes(bytes);
        "GIF89a"u8.CopyTo(bytes);
        var source = Path.Combine(_directory, "secret.gif");
        await File.WriteAllBytesAsync(source, bytes);
        var media = await _store.ImportAsync(source, _token);
        await WaitForMediaAsync(media.MediaId!);
        await using var harness = await Harness.StartAsync(
            clientFeatures: [ProtocolFeatures.LoopMedia], loopMediaService: _service);
        Assert.AreEqual(media.MediaId, (await StateAsync(harness)).MediaId);
        using var reconstructed = new MemoryStream();
        var sizes = new List<int>();
        while (reconstructed.Length < bytes.Length)
        {
            var chunk = await RequestAsync(harness, media.MediaId!, reconstructed.Length);
            Assert.IsNull(chunk.Error);
            Assert.AreEqual((long)bytes.Length, chunk.TotalBytes);
            var data = Convert.FromBase64String(chunk.Data!);
            sizes.Add(data.Length);
            reconstructed.Write(data);
            Assert.AreEqual(reconstructed.Length == bytes.Length, chunk.Last);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(PanelJson.Serialize(chunk)) < PanelProtocol.MaxControlMessageBytes);
        }
        CollectionAssert.AreEqual(new[] { 262144, 262144, 90112 }, sizes);
        CollectionAssert.AreEqual(bytes, reconstructed.ToArray());
        Assert.AreEqual(media.MediaId, Convert.ToHexString(SHA256.HashData(reconstructed.ToArray())).ToLowerInvariant());
        await RequestAsync(harness, new string('a', 64), 0, "stale_media");
        await RequestAsync(harness, "../../x", 0, "stale_media");
        await RequestAsync(harness, new string('A', 64), 0, "stale_media");
        await RequestAsync(harness, media.MediaId!, -1, "invalid_offset");
        await RequestAsync(harness, media.MediaId!, bytes.Length, "invalid_offset");
        await RequestAsync(harness, media.MediaId!, long.MaxValue, "invalid_offset");
        File.Delete(_store.GetMediaPath(media));
        await RequestAsync(harness, media.MediaId!, 0, "unavailable");
        Assert.IsFalse(_service.GetState().Available);
    }

    [TestMethod]
    public async Task MissingWrongSizedOrUnreadableMediaIsUnavailableAndNeverLeaksLocalMetadata()
    {
        await using var harness = await Harness.StartAsync(
            clientFeatures: [ProtocolFeatures.LoopMedia], loopMediaService: _service);
        Assert.IsFalse((await StateAsync(harness)).Available);
        await RequestAsync(harness, new string('a', 64), 0, "unavailable");
        var config = new LoopMediaConfiguration
        {
            MediaId = new string('a', 64), Kind = "video", Mime = "video/mp4", SizeBytes = 9,
            OriginalFileName = "private-name.mp4",
        };
        await _store.SaveAsync(config, _token);
        Assert.IsFalse((await StateAsync(harness)).Available);
        Directory.CreateDirectory(_store.MediaDirectory);
        await File.WriteAllBytesAsync(_store.GetMediaPath(config), new byte[8]);
        Assert.IsFalse(_service.GetState().Available);
        await RequestAsync(harness, config.MediaId!, 0, "unavailable");
        await File.WriteAllBytesAsync(_store.GetMediaPath(config), new byte[9]);
        var json = PanelJson.Serialize(_service.GetState());
        Assert.IsFalse(json.Contains("originalFileName"));
        Assert.IsFalse(json.Contains("private-name"));
        Assert.IsFalse(json.Contains(_directory));
        using (var locked = new FileStream(_store.GetMediaPath(config), FileMode.Open,
            FileAccess.Read, FileShare.None))
            await RequestAsync(harness, config.MediaId!, 0, "unavailable");
        Assert.IsTrue(_logs.All(line => !line.Contains(_directory) && !line.Contains("private-name")));
    }

    [TestMethod]
    public async Task PeerRequestingLoopMediaFromHostWithoutServiceGetsNoFeatureOrMessages()
    {
        await using var harness = await Harness.StartAsync(
            clientFeatures: [ProtocolFeatures.LoopMedia, ProtocolFeatures.StreamDeck]);
        Assert.IsFalse(harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.LoopMedia));
        Assert.IsTrue(harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.StreamDeck));
        harness.RejectLoopMediaMessages = true;
        await harness.ReadLayoutAsync();
        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.LoopMediaRequest, MediaId = new string('a', 64), Offset = 0,
        });
        Assert.AreEqual(ErrorCodes.UnknownCommand,
            (await harness.ReadUntilAsync<ErrorMessage>(MessageTypes.Error)).Code);
        await harness.SendAsync(new PingMessage());
        await harness.ReadLineOfTypeAsync(MessageTypes.Pong);
    }

    [TestMethod]
    public async Task LegacyPeerReceivesNoLoopMessagesAndStreamDeckStillWorks()
    {
        await using var harness = await Harness.StartAsync(
            clientFeatures: [ProtocolFeatures.StreamDeck], loopMediaService: _service);
        Assert.IsFalse(harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.LoopMedia));
        harness.RejectLoopMediaMessages = true;
        Assert.IsTrue(harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.StreamDeck));
        await harness.ReadLayoutAsync();
        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.LoopMediaRequest, MediaId = new string('a', 64), Offset = 0,
        });
        Assert.AreEqual(ErrorCodes.UnknownCommand,
            (await harness.ReadUntilAsync<ErrorMessage>(MessageTypes.Error)).Code);
        await _store.RemoveAsync(_token);
        await harness.SendAsync(new PingMessage());
        var next = await harness.ReadLineOfTypeAsync(MessageTypes.Pong);
        Assert.IsFalse(next.Contains("loop_media"));
    }

    private static Task<LoopMediaStateMessage> StateAsync(Harness harness) =>
        harness.ReadUntilAsync<LoopMediaStateMessage>(MessageTypes.LoopMediaState).WaitAsync(TimeSpan.FromSeconds(2));

    private static async Task<LoopMediaChunkMessage> RequestAsync(Harness harness,
        string mediaId, long offset, string? error = null)
    {
        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.LoopMediaRequest, MediaId = mediaId, Offset = offset,
        });
        var chunk = await harness.ReadUntilAsync<LoopMediaChunkMessage>(MessageTypes.LoopMediaChunk);
        Assert.AreEqual(error, chunk.Error);
        if (error is not null)
        {
            Assert.IsNull(chunk.Data);
            Assert.IsNull(chunk.TotalBytes);
            Assert.IsNull(chunk.Last);
        }
        return chunk;
    }

    private async Task WaitForMediaAsync(string mediaId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (_service.GetState().MediaId != mediaId)
            await Task.Delay(20, timeout.Token);
    }
}
