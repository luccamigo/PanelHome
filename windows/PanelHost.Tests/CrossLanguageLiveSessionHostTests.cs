using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Services;
using PanelHost.Transport;
using PanelOS.Windows.LoopMedia;

namespace PanelHost.Tests;

/// <summary>
/// Loopback host for the Kotlin live-session tests. The successful endpoints run
/// the production PanelClientSession; the two fault endpoints only cut/corrupt
/// the stream at an explicitly tested boundary.
/// </summary>
[TestClass]
public sealed class CrossLanguageLiveSessionHostTests
{
    private const string ManifestEnvironment = "PANELOS_LIVE_SESSION_MANIFEST";
    private static readonly TimeSpan HarnessTimeout = TimeSpan.FromMinutes(3);
    private string? _mediaDirectory;

    [TestCleanup]
    public void CleanupMedia()
    {
        if (_mediaDirectory is not null) Directory.Delete(_mediaDirectory, recursive: true);
    }

    [TestMethod]
    [TestCategory("CrossLanguageLiveHost")]
    public async Task Serve_Kotlin_live_session_scenarios()
    {
        var manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironment);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Assert.Inconclusive(
                $"Run scripts/run-cross-language-live-session.ps1; {ManifestEnvironment} is not set.");
            return;
        }

        using var cancellation = new CancellationTokenSource(HarnessTimeout);
        _mediaDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, "media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_mediaDirectory);
        var source = Path.Combine(_mediaDirectory, "source.gif");
        var bytes = new byte[600 * 1024];
        new Random(12).NextBytes(bytes);
        "GIF89a"u8.CopyTo(bytes);
        await File.WriteAllBytesAsync(source, bytes, cancellation.Token);
        var store = new JsonLoopMediaStore(
            Path.Combine(_mediaDirectory, "media.json"), Path.Combine(_mediaDirectory, "media"));
        await store.ImportAsync(source, cancellation.Token);
        await using var loopMedia = new LoopMediaService(store);
        await loopMedia.StartAsync(cancellation.Token);
        using var positive = StartListener();
        using var missingFeature = StartListener();
        using var wrongVersion = StartListener();
        using var invalidJson = StartListener();
        using var droppedHandshake = StartListener();
        using var reconnect = StartListener();
        var accepted = new int[6];

        var tasks = new[]
        {
            ServeProductionAsync(positive, 1, streamDeckAvailable: true, accepted, 0, cancellation.Token,
                loopMediaService: loopMedia),
            ServeProductionAsync(missingFeature, 1, streamDeckAvailable: false, accepted, 1, cancellation.Token),
            ServeProductionAsync(wrongVersion, 1, streamDeckAvailable: true, accepted, 2, cancellation.Token),
            ServeInvalidJsonAsync(invalidJson, accepted, 3, cancellation.Token),
            ServeDroppedHandshakeAsync(droppedHandshake, accepted, 4, cancellation.Token),
            ServeProductionAsync(
                reconnect,
                2,
                streamDeckAvailable: true,
                accepted,
                5,
                cancellation.Token,
                disconnectFirstAfter: TimeSpan.FromMilliseconds(750)),
        };

        var manifest = new LiveSessionManifest(
            Port(positive),
            Port(missingFeature),
            Port(wrongVersion),
            Port(invalidJson),
            Port(droppedHandshake),
            Port(reconnect),
            PanelBuildIdentity.Identity,
            PanelBuildIdentity.Number,
            PanelProtocol.PROTOCOL_VERSION,
            source);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest),
            cancellation.Token);

        var donePath = manifestPath + ".done";
        while (!File.Exists(donePath))
        {
            await Task.Delay(100, cancellation.Token);
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
        CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 2 }, accepted);
        await File.WriteAllTextAsync(
            manifestPath + ".host-passed",
            "ok",
            cancellation.Token);
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) =>
        ((IPEndPoint)listener.LocalEndpoint).Port;

    private static async Task ServeProductionAsync(
        TcpListener listener,
        int connectionCount,
        bool streamDeckAvailable,
        int[] accepted,
        int acceptedIndex,
        CancellationToken cancellationToken,
        TimeSpan? disconnectFirstAfter = null,
        LoopMediaService? loopMediaService = null)
    {
        await using var runtime =
            StreamDeckSessionTests.StreamDeckHarness.CreateRuntime();
        for (var index = 0; index < connectionCount; index++)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            Interlocked.Increment(ref accepted[acceptedIndex]);
            await using var desktop = new FakeDesktopService(TimeSpan.Zero);
            var session = new PanelClientSession(
                new FakeTelemetryService(
                    new TelemetryMessage
                    {
                        Cpu = new CpuTelemetry
                        {
                            Name = "Live CPU",
                            UsagePercent = 42.5,
                        },
                    }),
                new FakeMediaService(
                    new MediaStateMessage
                    {
                        Available = true,
                        MediaId = "live-media",
                        Title = "Live Session",
                        Artist = "PanelOS",
                        IsPlaying = true,
                        DurationMs = 120_000,
                    }),
                new FakeVolumeService(
                    new VolumeStateMessage
                    {
                        Available = true,
                        VolumePercent = 73,
                        IsMuted = false,
                    }),
                desktop,
                streamDeckService: streamDeckAvailable
                    ? runtime.Service
                    : UnavailableStreamDeckService.Instance,
                loopMediaService: loopMediaService);
            using var sessionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (index == 0 && disconnectFirstAfter is not null)
            {
                sessionCancellation.CancelAfter(disconnectFirstAfter.Value);
            }

            try
            {
                await session.HandleAsync(
                    new StreamPanelConnection(
                        PanelChannelId.Control,
                        client.GetStream(),
                        "cross-language-loopback",
                        ownsStream: false),
                    sessionCancellation.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or SocketException)
            {
                Console.WriteLine(
                    $"[CrossLanguageLiveHost] endpoint={acceptedIndex} " +
                    $"session ended with {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private static async Task ServeInvalidJsonAsync(
        TcpListener listener,
        int[] accepted,
        int acceptedIndex,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        Interlocked.Increment(ref accepted[acceptedIndex]);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        var helloLine = await reader.ReadLineAsync(cancellationToken);
        Assert.IsNotNull(helloLine);
        _ = PanelJson.Deserialize<HelloMessage>(helloLine);
        await writer.WriteAsync(
            PanelJson.SerializeLine(
                new HelloAckMessage
                {
                    HostName = "FAULT-HOST",
                    Features = [ProtocolFeatures.Desktop],
                    BuildIdentity = PanelBuildIdentity.Identity,
                    BuildNumber = PanelBuildIdentity.Number,
                }).AsMemory(),
            cancellationToken);
        await writer.WriteAsync("{definitely-not-json}\n".AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task ServeDroppedHandshakeAsync(
        TcpListener listener,
        int[] accepted,
        int acceptedIndex,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        Interlocked.Increment(ref accepted[acceptedIndex]);
        using var reader = new StreamReader(client.GetStream(), new UTF8Encoding(false));
        var helloLine = await reader.ReadLineAsync(cancellationToken);
        Assert.IsNotNull(helloLine);
        _ = PanelJson.Deserialize<HelloMessage>(helloLine);
        // Closing here is the fault: the production client must observe a drop
        // while waiting for HELLO_ACK and must never report Connected.
    }

    private sealed record LiveSessionManifest(
        int PositivePort,
        int MissingFeaturePort,
        int WrongVersionPort,
        int InvalidJsonPort,
        int DroppedHandshakePort,
        int ReconnectPort,
        string HostBuildIdentity,
        long HostBuildNumber,
        int ProtocolVersion,
        string LoopMediaPath);
}
