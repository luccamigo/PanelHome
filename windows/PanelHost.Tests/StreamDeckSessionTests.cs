using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Protocol;
using PanelHost.Server;
using PanelHost.Services;
using PanelHost.Transport;
using PanelOS.Windows.StreamDeck;
using PanelOS.Windows.StreamDeck.Icons;
using PanelOS.Windows.StreamDeck.Runtime;

// Both namespaces define a MediaCommand; the Stream Deck domain's one is the
// action a button carries, not the protocol command the tablet sends.
using StreamDeckMediaCommand = PanelOS.Windows.StreamDeck.MediaCommand;

namespace PanelHost.Tests;

/// <summary>
/// The Stream Deck over the control channel, end to end: layout out, id in.
/// </summary>
/// <remarks>
/// The session is driven with the real shared runtime - the same configuration
/// store, executor and icon cache the Control Center uses - so these tests
/// cover the actual resolution path rather than a stand-in for it.
/// </remarks>
[TestClass]
public sealed class StreamDeckSessionTests
{
    [TestMethod]
    public async Task The_current_layout_is_sent_immediately_after_the_handshake()
    {
        await using var harness = await StreamDeckHarness.StartAsync();

        var layout = await harness.ReadLayoutAsync();

        Assert.IsTrue(
            harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.StreamDeck));
        Assert.AreEqual(harness.Revision, layout.Revision);
        Assert.AreEqual("Principal", layout.Profile!.Name);
        Assert.AreEqual(1, layout.Pages.Count);
        Assert.AreEqual(2, layout.Pages[0].Rows);
        Assert.AreEqual(2, layout.Pages[0].Columns);
        CollectionAssert.AreEquivalent(
            new[] { "btn-media", "btn-app", "btn-off" },
            layout.Pages[0].Buttons.Select(button => button.Id).ToArray());
    }

    [TestMethod]
    public async Task The_layout_never_carries_action_configuration()
    {
        await using var harness = await StreamDeckHarness.StartAsync();

        var line = await harness.ReadLineOfTypeAsync(MessageTypes.StreamDeckLayout);

        // The configuration behind btn-app names an executable and arguments;
        // none of it may cross the cable.
        foreach (var forbidden in new[]
                 {
                     "notepad", ".exe", "executablePath", "arguments",
                     "workingDirectory", "url", "sourcePath", "script",
                 })
        {
            StringAssert.DoesNotMatch(
                line,
                new System.Text.RegularExpressions.Regex(
                    System.Text.RegularExpressions.Regex.Escape(forbidden),
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                $"the layout must not mention {forbidden}");
        }
    }

    [TestMethod]
    public async Task A_client_without_the_feature_gets_no_layout_and_no_invocation()
    {
        await using var harness = await StreamDeckHarness.StartAsync(clientFeatures: []);

        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.StreamDeckInvoke,
            ButtonId = "btn-media",
            Revision = harness.Revision,
        });
        var error = await harness.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);

        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
        Assert.AreEqual(0, harness.Keyboard.SentChords.Count);
    }

    [TestMethod]
    public async Task A_host_without_the_stream_deck_never_announces_the_feature()
    {
        await using var harness = await StreamDeckHarness.StartAsync(
            streamDeckAvailable: false);

        Assert.IsFalse(
            harness.HelloAcknowledgement.SupportsFeature(ProtocolFeatures.StreamDeck));

        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.StreamDeckInvoke,
            ButtonId = "btn-media",
            Revision = "whatever",
        });
        var error = await harness.ReadUntilAsync<ErrorMessage>(MessageTypes.Error);
        Assert.AreEqual(ErrorCodes.UnknownCommand, error.Code);
    }

    [TestMethod]
    public async Task A_valid_invocation_runs_the_button_and_answers_success()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var result = await harness.InvokeAsync("btn-media", harness.Revision);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("btn-media", result.ButtonId);
        Assert.IsNull(result.ErrorCode);
        Assert.AreEqual(1, harness.Keyboard.SentChords.Count);
    }

    [TestMethod]
    public async Task A_stale_revision_runs_nothing_and_answers_with_a_fresh_layout()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var result = await harness.InvokeAsync("btn-media", "0000000000000000");

        // A button pressed against an old layout may now mean something else,
        // so nothing runs and the panel is corrected instead.
        Assert.IsFalse(result.Success);
        Assert.AreEqual("stale_layout", result.ErrorCode);
        Assert.AreEqual(0, harness.Keyboard.SentChords.Count);

        var layout = await harness.ReadLayoutAsync();
        Assert.AreEqual(harness.Revision, layout.Revision);
    }

    [TestMethod]
    public async Task An_unknown_button_is_reported_without_running_anything()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var result = await harness.InvokeAsync("btn-inventado", harness.Revision);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(StreamDeckErrorCodes.ButtonNotFound, result.ErrorCode);
        Assert.AreEqual(0, harness.Keyboard.SentChords.Count);
    }

    [TestMethod]
    public async Task A_disabled_button_is_refused_by_the_shared_executor()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var result = await harness.InvokeAsync("btn-off", harness.Revision);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(StreamDeckErrorCodes.ButtonDisabled, result.ErrorCode);
        Assert.AreEqual(0, harness.Keyboard.SentChords.Count);
    }

    [TestMethod]
    public async Task A_failure_is_reported_by_code_without_quoting_the_configuration()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var result = await harness.InvokeAsync("btn-app", harness.Revision);

        // The configured executable does not exist; the code is stable and the
        // text must not name the path that failed.
        Assert.IsFalse(result.Success);
        Assert.AreEqual(StreamDeckErrorCodes.ApplicationNotFound, result.ErrorCode);
        Assert.IsNotNull(result.Message);
        StringAssert.DoesNotMatch(
            result.Message,
            new System.Text.RegularExpressions.Regex("notepad", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    [TestMethod]
    public async Task An_invocation_without_a_button_id_is_refused()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        await harness.SendAsync(new CommandMessage
        {
            Command = CommandNames.StreamDeckInvoke,
            Revision = harness.Revision,
        });
        var result = await harness.ReadUntilAsync<StreamDeckInvokeResultMessage>(
            MessageTypes.StreamDeckInvokeResult);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("invalid_request", result.ErrorCode);
    }

    [TestMethod]
    public async Task A_saved_change_pushes_the_new_layout_to_the_connected_tablet()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        var first = await harness.ReadLayoutAsync();

        harness.RenameFirstButton("Ao vivo");

        var second = await harness.ReadLayoutAsync();
        Assert.AreNotEqual(first.Revision, second.Revision);
        Assert.AreEqual(
            "Ao vivo",
            second.Pages[0].Buttons.Single(button => button.Id == "btn-media").Label);
    }

    [TestMethod]
    public async Task Reconnecting_sends_the_layout_again_without_being_asked()
    {
        await using var shared = StreamDeckHarness.CreateRuntime();
        await using (var first = await StreamDeckHarness.StartAsync(runtime: shared))
        {
            await first.ReadLayoutAsync();
        }

        await using var second = await StreamDeckHarness.StartAsync(runtime: shared);
        var layout = await second.ReadLayoutAsync();

        Assert.AreEqual(shared.Revision, layout.Revision);
        Assert.AreEqual(1, layout.Pages.Count);
    }

    [TestMethod]
    public async Task Only_cached_icons_the_layout_uses_are_served()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var icons = await harness.RequestIconsAsync(
            StreamDeckHarness.KnownIconId,
            StreamDeckHarness.UnusedIconId);

        var served = icons.Single(icon => icon.IconId == StreamDeckHarness.KnownIconId);
        Assert.IsTrue(served.Available);
        Assert.AreEqual("image/png", served.Mime);
        CollectionAssert.AreEqual(
            StreamDeckHarness.SamplePng,
            Convert.FromBase64String(served.Data!));

        // An icon the published layout does not refer to is not readable, even
        // when it happens to be in the PC's cache.
        var refused = icons.Single(icon => icon.IconId == StreamDeckHarness.UnusedIconId);
        Assert.IsFalse(refused.Available);
        Assert.IsNull(refused.Data);
    }

    [DataTestMethod]
    [DataRow(@"..\..\Windows\System32\config")]
    [DataRow("../../etc/passwd")]
    [DataRow(@"C:\Windows\win.ini")]
    [DataRow("icon-abc/../../x")]
    [DataRow("ICON-ABC")]
    public async Task A_malformed_icon_id_is_answered_as_unavailable(string iconId)
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var icons = await harness.RequestIconsAsync(iconId);

        Assert.AreEqual(1, icons.Count);
        Assert.IsFalse(icons[0].Available);
        Assert.IsNull(icons[0].Data);
    }

    [TestMethod]
    public async Task An_icon_the_cache_does_not_hold_is_answered_as_unavailable()
    {
        await using var harness = await StreamDeckHarness.StartAsync();
        await harness.ReadLayoutAsync();

        var icons = await harness.RequestIconsAsync(StreamDeckHarness.MissingIconId);

        Assert.IsFalse(icons[0].Available);
    }

    [TestMethod]
    public async Task An_icon_larger_than_the_limit_is_never_transferred()
    {
        // The cached sample is larger than this, so the limit is what refuses it.
        await using var harness = await StreamDeckHarness.StartAsync(maximumIconBytes: 8);
        await harness.ReadLayoutAsync();

        var icons = await harness.RequestIconsAsync(StreamDeckHarness.KnownIconId);

        Assert.IsFalse(icons[0].Available);
        Assert.IsNull(icons[0].Data);
    }

    /// <summary>
    /// The shared Stream Deck runtime plus one connected client, wired the way
    /// PanelHost wires it in Program.cs.
    /// </summary>
    internal sealed class StreamDeckRuntime : IAsyncDisposable
    {
        public required FakeStreamDeckStore Store { get; init; }

        public required StreamDeckConfigurationProvider Provider { get; init; }

        public required StreamDeckService Service { get; init; }

        public required RecordingKeyboard Keyboard { get; init; }

        public required string IconDirectory { get; init; }

        public string Revision => Provider.Snapshot.Revision;

        public Action Notify { get; set; } = () => { };

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            try
            {
                Directory.Delete(IconDirectory, recursive: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal sealed class StreamDeckHarness : IAsyncDisposable
    {
        internal const string KnownIconId = "icon-aaaaaaaaaa";
        internal const string UnusedIconId = "icon-bbbbbbbbbb";
        internal const string MissingIconId = "icon-cccccccccc";

        internal static readonly byte[] SamplePng = CreatePngHeader();

        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        private static byte[] CreatePngHeader()
        {
            var bytes = new byte[24];
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }
                .CopyTo(bytes, 0);
            bytes[11] = 13;
            "IHDR"u8.CopyTo(bytes.AsSpan(12));
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                bytes.AsSpan(16), 128);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
                bytes.AsSpan(20), 128);
            return bytes;
        }

        private readonly TcpClient _client;
        private readonly TcpClient _serverClient;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _sessionTask;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private readonly StreamDeckRuntime _runtime;
        private readonly bool _ownsRuntime;
        private readonly FakeDesktopService _desktopService;

        private StreamDeckHarness(
            TcpClient client,
            TcpClient serverClient,
            CancellationTokenSource cancellation,
            Task sessionTask,
            StreamDeckRuntime runtime,
            bool ownsRuntime,
            FakeDesktopService desktopService)
        {
            _client = client;
            _serverClient = serverClient;
            _cancellation = cancellation;
            _sessionTask = sessionTask;
            _runtime = runtime;
            _ownsRuntime = ownsRuntime;
            _desktopService = desktopService;
            _reader = new StreamReader(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            _writer = new StreamWriter(
                client.GetStream(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                leaveOpen: true);
        }

        public HelloAckMessage HelloAcknowledgement { get; private set; } = new();
        public bool RejectLoopMediaMessages { get; set; }

        public string Revision => _runtime.Revision;

        public RecordingKeyboard Keyboard => _runtime.Keyboard;

        public static StreamDeckRuntime CreateRuntime(int maximumIconBytes = 512 * 1024)
        {
            var iconDirectory = Path.Combine(
                Path.GetTempPath(),
                "PanelHost.Tests",
                Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(iconDirectory);
            File.WriteAllBytes(
                Path.Combine(iconDirectory, KnownIconId + ".png"),
                SamplePng);
            File.WriteAllBytes(
                Path.Combine(iconDirectory, UnusedIconId + ".png"),
                SamplePng);

            var store = new FakeStreamDeckStore(CreateConfiguration());
            var changes = new NotifyingChangeSource();
            var provider = new StreamDeckConfigurationProvider(
                store,
                changes,
                options: new StreamDeckReloadOptions
                {
                    Debounce = TimeSpan.Zero,
                    RetryDelay = TimeSpan.Zero,
                },
                delay: (_, _) => Task.CompletedTask);
            provider.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var keyboard = new RecordingKeyboard();
            var service = new StreamDeckService(
                provider,
                new WindowsStreamDeckActionExecutor(
                    store,
                    keyboard,
                    new RecordingLauncher(),
                    new MissingFileSystem()),
                new StreamDeckIconCache(iconDirectory),
                maximumIconBytes: maximumIconBytes);

            return new StreamDeckRuntime
            {
                Store = store,
                Provider = provider,
                Service = service,
                Keyboard = keyboard,
                IconDirectory = iconDirectory,
                Notify = changes.Raise,
            };
        }

        public static async Task<StreamDeckHarness> StartAsync(
            string[]? clientFeatures = null,
            bool streamDeckAvailable = true,
            int maximumIconBytes = 512 * 1024,
            StreamDeckRuntime? runtime = null,
            LoopMediaService? loopMediaService = null)
        {
            var ownsRuntime = runtime is null;
            runtime ??= CreateRuntime(maximumIconBytes);

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new TcpClient();
            var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
            var serverClient = await listener.AcceptTcpClientAsync();
            await connectTask;

            var cancellation = new CancellationTokenSource();
            var desktopService = new FakeDesktopService(TimeSpan.Zero);
            var session = new PanelClientSession(
                new FakeTelemetryService(),
                new FakeMediaService(),
                new FakeVolumeService(),
                desktopService,
                streamDeckService: streamDeckAvailable
                    ? runtime.Service
                    : UnavailableStreamDeckService.Instance,
                loopMediaService: loopMediaService);
            var sessionTask = session.HandleAsync(
                new StreamPanelConnection(
                    PanelChannelId.Control,
                    serverClient.GetStream(),
                    "test-control",
                    ownsStream: false),
                cancellation.Token);
            var harness = new StreamDeckHarness(
                client,
                serverClient,
                cancellation,
                sessionTask,
                runtime,
                ownsRuntime,
                desktopService);

            await harness.SendAsync(new HelloMessage
            {
                DeviceName = "PanelOS Test",
                Features = clientFeatures ?? [ProtocolFeatures.StreamDeck],
            });
            harness.HelloAcknowledgement =
                await harness.ReadUntilAsync<HelloAckMessage>(MessageTypes.HelloAck);
            return harness;
        }

        public void RenameFirstButton(string label)
        {
            var configuration = CreateConfiguration();
            configuration.Profiles[0].Pages[0].Buttons[0].Label = label;
            _runtime.Store.Configuration = configuration;
            _runtime.Notify();
        }

        public Task<StreamDeckLayoutMessage> ReadLayoutAsync() =>
            ReadUntilAsync<StreamDeckLayoutMessage>(MessageTypes.StreamDeckLayout);

        public async Task<StreamDeckInvokeResultMessage> InvokeAsync(
            string buttonId,
            string revision)
        {
            await SendAsync(new CommandMessage
            {
                Command = CommandNames.StreamDeckInvoke,
                ButtonId = buttonId,
                Revision = revision,
            });
            return await ReadUntilAsync<StreamDeckInvokeResultMessage>(
                MessageTypes.StreamDeckInvokeResult);
        }

        public async Task<IReadOnlyList<StreamDeckIconMessage>> RequestIconsAsync(
            params string[] iconIds)
        {
            await SendAsync(new CommandMessage
            {
                Command = CommandNames.StreamDeckIconsRequest,
                IconIds = iconIds,
            });

            var received = new List<StreamDeckIconMessage>();
            while (received.Count < iconIds.Length)
            {
                received.Add(await ReadUntilAsync<StreamDeckIconMessage>(
                    MessageTypes.StreamDeckIcon));
            }

            return received;
        }

        public async Task SendAsync<T>(T message) where T : class
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            await _writer.WriteAsync(
                PanelJson.SerializeLine(message).AsMemory(),
                timeout.Token);
            await _writer.FlushAsync(timeout.Token);
        }

        public async Task<T> ReadUntilAsync<T>(string messageType) where T : class =>
            PanelJson.Deserialize<T>(await ReadLineOfTypeAsync(messageType));

        public async Task<string> ReadLineOfTypeAsync(string messageType)
        {
            using var timeout = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token);
                Assert.IsNotNull(line, "PanelHost closed the test connection.");
                if (RejectLoopMediaMessages)
                    Assert.IsFalse(PanelJson.Deserialize<MessageEnvelope>(line).Type.StartsWith("loop_media."));
                if (PanelJson.Deserialize<MessageEnvelope>(line).Type == messageType)
                {
                    return line;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _client.Dispose();
            try
            {
                await _sessionTask.WaitAsync(TestTimeout);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or SocketException)
            {
            }
            finally
            {
                _reader.Dispose();
                try
                {
                    await _writer.DisposeAsync();
                }
                catch (Exception exception) when (
                    exception is ObjectDisposedException or IOException)
                {
                }

                _serverClient.Dispose();
                _cancellation.Dispose();
                await _desktopService.DisposeAsync();
                if (_ownsRuntime)
                {
                    await _runtime.DisposeAsync();
                }
            }
        }

        private static StreamDeckConfiguration CreateConfiguration() => new()
        {
            ActiveProfileId = "profile-a",
            Profiles =
            [
                new StreamDeckProfile
                {
                    Id = "profile-a",
                    Name = "Principal",
                    Pages =
                    [
                        new StreamDeckPage
                        {
                            Id = "page-a",
                            Name = "Principal",
                            Rows = 2,
                            Columns = 2,
                            Buttons =
                            [
                                new StreamDeckButton
                                {
                                    Id = "btn-media",
                                    Row = 0,
                                    Column = 0,
                                    Label = "Play/Pause",
                                    Icon = new StreamDeckIcon
                                    {
                                        Kind = StreamDeckIconKind.Custom,
                                        IconId = KnownIconId,
                                        SourcePath = @"C:\Fotos\play.png",
                                    },
                                    Action = new MediaCommandAction
                                    {
                                        Command = StreamDeckMediaCommand.PlayPause,
                                    },
                                },
                                new StreamDeckButton
                                {
                                    Id = "btn-app",
                                    Row = 0,
                                    Column = 1,
                                    Label = "Bloco",
                                    Action = new LaunchApplicationAction
                                    {
                                        ExecutablePath = @"C:\Windows\notepad.exe",
                                        Arguments = "--secreto",
                                        WorkingDirectory = @"C:\Windows",
                                    },
                                },
                                new StreamDeckButton
                                {
                                    Id = "btn-off",
                                    Row = 1,
                                    Column = 0,
                                    Label = "Desligado",
                                    Enabled = false,
                                    Action = new MediaCommandAction
                                    {
                                        Command = StreamDeckMediaCommand.Next,
                                    },
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        private sealed class NotifyingChangeSource : IStreamDeckConfigurationChangeSource
        {
            public event EventHandler? Changed;

            public void Start()
            {
            }

            public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

            public void Dispose()
            {
            }
        }
    }

    internal sealed class RecordingKeyboard : IKeyboardInputService
    {
        public List<IReadOnlyList<KeyboardInputEvent>> SentChords { get; } = [];

        public void Send(IReadOnlyList<KeyboardInputEvent> events) =>
            SentChords.Add(events);
    }

    internal sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessStartInfo> Started { get; } = [];

        public void Start(ProcessStartInfo startInfo) => Started.Add(startInfo);
    }

    /// <summary>Nothing exists, so a configured executable is "not found".</summary>
    internal sealed class MissingFileSystem : IFileSystemProbe
    {
        public bool FileExists(string path) => false;

        public bool DirectoryExists(string path) => false;
    }
}
