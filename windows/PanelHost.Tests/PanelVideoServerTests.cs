using System.Net.Sockets;
using System.IO.Compression;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Services;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelVideoServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Second_client_is_rejected_and_slot_reopens_after_disconnect()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            new EmptyFrameSource(),
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);

        using var first = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(first));

        // No frame is ever published. Inactivity is valid for a static desktop
        // and must not release the single-client slot.
        await Task.Delay(250);

        using var second = await ConnectAsync(server.BoundPort);
        var secondResponse = await ReadResponseAsync(second);
        Assert.AreEqual(VideoMessageType.Reject, secondResponse.Type);
        Assert.AreEqual(VideoRejectCode.Busy, secondResponse.RejectCode);

        first.Dispose();
        var acceptedAfterDisconnect = false;
        for (var attempt = 0; attempt < 20 && !acceptedAfterDisconnect; attempt++)
        {
            await Task.Delay(25);
            using var candidate = await ConnectAsync(server.BoundPort);
            var type = await HandshakeAsync(candidate);
            acceptedAfterDisconnect = type == VideoMessageType.Ack;
        }

        Assert.IsTrue(acceptedAfterDisconnect, "Video client slot stayed occupied.");
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Leaving_running_state_closes_video_connection()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            new EmptyFrameSource(),
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(client));

        var readBuffer = new byte[1];
        var closed = client.GetStream().ReadAsync(readBuffer).AsTask();
        await desktop.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, await closed.WaitAsync(TestTimeout));
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Client_fin_has_explicit_session_close_reason()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        using var log = new StringWriter();
        var server = new PanelVideoServer(
            desktop,
            new EmptyFrameSource(),
            log,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);

        using (var client = await ConnectAsync(server.BoundPort))
        {
            Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(client));
        }

        await WaitUntilAsync(() =>
            log.ToString().Contains(
                "[Video] Session closing: reason=ClientFin",
                StringComparison.Ordinal));
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Control_disconnect_has_explicit_session_close_reason()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        using var log = new StringWriter();
        var lifetime = new VideoSessionLifetime();
        var server = new PanelVideoServer(
            desktop,
            new EmptyFrameSource(),
            log,
            port: 0,
            videoSessionLifetime: lifetime);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(client));

        lifetime.NotifyControlDisconnected();

        var buffer = new byte[1];
        Assert.AreEqual(
            0,
            await client.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TestTimeout));
        await WaitUntilAsync(() =>
            log.ToString().Contains(
                "[Video] Session closing: reason=ControlDisconnected",
                StringComparison.Ordinal));
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Frame_source_is_polled_only_with_running_desktop_and_client()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        var source = new CountingFrameSource();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);

        Assert.AreEqual(IPAddress.Loopback, server.BoundEndpoint.Address);
        using (var stoppedClient = await ConnectAsync(server.BoundPort))
        {
            var rejection = await ReadResponseAsync(stoppedClient);
            Assert.AreEqual(VideoMessageType.Reject, rejection.Type);
            Assert.AreEqual(
                VideoRejectCode.DesktopNotRunning,
                rejection.RejectCode);
        }

        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        await Task.Delay(150);
        Assert.AreEqual(0, source.CallCount);

        using var runningClient = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(runningClient));
        await WaitUntilAsync(() => source.CallCount > 0);
        Assert.IsTrue(source.CallCount <= 3, "Frame source polling exceeded 10 FPS.");

        await desktop.StopAsync(CancellationToken.None);
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [DataTestMethod]
    [DataRow(30)]
    [DataRow(45)]
    [DataRow(60)]
    public async Task Negotiated_H264_target_is_forwarded_end_to_end(int targetFps)
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new ConfigurableFrameSource();
        using var log = new StringWriter();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            log,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
                PreferredSessionFps = targetFps,
            },
            h264EncoderFactory: new RecordingH264EncoderFactory());
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(1280, 800, 60, VideoCodecMask.H264)));
        var response = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TestTimeout);
        var ack = PanelVideoProtocol.ParseAck(response);

        await WaitUntilAsync(() => source.ConfiguredTargets.Count >= 1);

        Assert.AreEqual((uint)targetFps, ack.Fps);
        Assert.AreEqual(targetFps, source.ConfiguredTargets[0]);
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);

        StringAssert.Contains(log.ToString(), $"hostPreferred={targetFps}");
        StringAssert.Contains(
            log.ToString(),
            $"hostPreferred={targetFps} clientMax=60 effective={targetFps} " +
            $"driverTarget={targetFps} encoderFps={targetFps} clamp=none");
    }

    [DataTestMethod]
    [DataRow(60)]
    [DataRow(45)]
    [DataRow(30)]
    public async Task Client_cap_defines_effective_session_and_driver_target(
        int clientMaxFps)
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new ConfigurableFrameSource();
        using var log = new StringWriter();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            log,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
                PreferredSessionFps = 60,
            },
            h264EncoderFactory: new RecordingH264EncoderFactory());
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(
                1280,
                800,
                checked((uint)clientMaxFps),
                VideoCodecMask.H264)));
        var response = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TestTimeout);
        var ack = PanelVideoProtocol.ParseAck(response);

        await WaitUntilAsync(() => source.ConfiguredTargets.Count >= 1);

        Assert.AreEqual(checked((uint)clientMaxFps), ack.Fps);
        Assert.AreEqual(clientMaxFps, source.ConfiguredTargets[0]);
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);

        StringAssert.Contains(
            log.ToString(),
            $"hostPreferred=60 clientMax={clientMaxFps} " +
            $"effective={clientMaxFps} driverTarget={clientMaxFps} " +
            $"encoderFps={clientMaxFps} " +
            $"clamp={(clientMaxFps < 60 ? "client_max" : "none")}");
    }

    [TestMethod]
    public async Task Reconnect_preserves_frame_rate_negotiation_without_crashing()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new ConfigurableFrameSource();
        using var log = new StringWriter();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            log,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
            },
            h264EncoderFactory: new RecordingH264EncoderFactory());
        var serverTask = server.RunAsync(cancellation.Token);

        using (var firstClient = await ConnectAsync(server.BoundPort))
        {
            var firstAck = await HandshakeH264Async(firstClient, clientMaxFps: 45);
            Assert.AreEqual(45u, firstAck.Fps);
            await WaitUntilAsync(() => source.ConfiguredTargets.Count >= 1);
        }

        await WaitUntilAsync(() => log.ToString().Contains("[Video] Session closing:"));
        await Task.Delay(50);

        using (var secondClient = await ConnectAsync(server.BoundPort))
        {
            var secondAck = await HandshakeH264Async(secondClient, clientMaxFps: 45);
            Assert.AreEqual(45u, secondAck.Fps);
            await WaitUntilAsync(() => source.ConfiguredTargets.Count >= 2);
        }

        CollectionAssert.AreEqual(new[] { 45, 45 }, source.ConfiguredTargets.ToArray());
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Server_sends_fixed_header_followed_by_complete_zlib_payload()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new CountingFrameSource(new FrameSnapshot(
            1,
            9,
            123_000,
            2,
            1,
            8,
            PanelDisplayFrameFormat.Rgba8888,
            [10, 20, 30, 0, 40, 50, 60, 0]));
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(client));

        var headerBytes = new byte[PanelVideoProtocol.FrameHeaderSize];
        await client.GetStream().ReadExactlyAsync(headerBytes)
            .AsTask()
            .WaitAsync(TestTimeout);
        var header = PanelVideoProtocol.ParseFrameHeader(headerBytes);
        var compressed = new byte[header.CompressedBytes];
        await client.GetStream().ReadExactlyAsync(compressed)
            .AsTask()
            .WaitAsync(TestTimeout);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await zlib.CopyToAsync(output);

        Assert.AreEqual(9UL, header.Sequence);
        CollectionAssert.AreEqual(
            new byte[] { 30, 20, 10, 255, 60, 50, 40, 255 },
            output.ToArray());
        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Test_pattern_uses_normal_frame_header_zlib_and_socket_path()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            new VideoTestPatternFrameSource(),
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        Assert.AreEqual(VideoMessageType.Ack, await HandshakeAsync(client));

        var headerBytes = new byte[PanelVideoProtocol.FrameHeaderSize];
        await client.GetStream().ReadExactlyAsync(headerBytes)
            .AsTask()
            .WaitAsync(TestTimeout);
        var header = PanelVideoProtocol.ParseFrameHeader(headerBytes);
        var compressed = new byte[header.CompressedBytes];
        await client.GetStream().ReadExactlyAsync(compressed)
            .AsTask()
            .WaitAsync(TestTimeout);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await zlib.CopyToAsync(output);
        var pixels = output.ToArray();

        Assert.AreEqual(1UL, header.Sequence);
        Assert.AreEqual(640u, header.Width);
        Assert.AreEqual(400u, header.Height);
        Assert.AreEqual(640u * 400u * 4u, header.UncompressedBytes);
        Assert.AreEqual(header.UncompressedBytes, checked((uint)pixels.Length));
        AssertPixel(pixels, 640 * 4, 100, 100, 0, 0, 255, 255);
        AssertPixel(pixels, 640 * 4, 500, 100, 0, 255, 0, 255);
        AssertPixel(pixels, 640 * 4, 100, 300, 255, 0, 0, 255);
        AssertPixel(pixels, 640 * 4, 500, 300, 255, 255, 255, 255);

        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [DataTestMethod]
    [DataRow(30)]
    [DataRow(45)]
    [DataRow(60)]
    public async Task H264_session_sends_configuration_then_keyframe_and_uses_effective_fps(
        int targetFps)
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new CountingFrameSource(new FrameSnapshot(
            1,
            41,
            456_000,
            2,
            2,
            8,
            PanelDisplayFrameFormat.Bgra8888,
            [
                0, 0, 255, 255, 0, 255, 0, 255,
                255, 0, 0, 255, 255, 255, 255, 255,
            ]));
        var encoderFactory = new RecordingH264EncoderFactory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            TextWriter.Null,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
                PreferredSessionFps = targetFps,
            },
            h264EncoderFactory: encoderFactory);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(1280, 800, 60, VideoCodecMask.H264)));
        var ackBytes = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(ackBytes).AsTask().WaitAsync(TestTimeout);
        var ack = PanelVideoProtocol.ParseAck(ackBytes);

        Assert.AreEqual(VideoCodec.H264, ack.SelectedCodec);
        Assert.AreEqual(1280u, ack.MaxWidth);
        Assert.AreEqual(800u, ack.MaxHeight);
        Assert.AreEqual((uint)targetFps, ack.Fps);

        var configuration = await ReadH264PacketAsync(stream);
        var keyFrame = await ReadH264PacketAsync(stream);
        Assert.AreEqual(H264PacketFlags.Config, configuration.Header.Flags);
        Assert.AreEqual(41UL, configuration.Header.Sequence);
        CollectionAssert.AreEqual(RecordingH264Encoder.Configuration, configuration.Payload);
        Assert.AreEqual(
            H264PacketFlags.KeyFrame | H264PacketFlags.EndOfAccessUnit,
            keyFrame.Header.Flags);
        Assert.AreEqual(41UL, keyFrame.Header.Sequence);
        Assert.AreEqual(456_000UL, keyFrame.Header.CaptureTimestampMicros);
        CollectionAssert.AreEqual(RecordingH264Encoder.KeyFrame, keyFrame.Payload);

        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
        Assert.IsTrue(encoderFactory.Encoder?.Disposed);
        Assert.IsTrue(encoderFactory.Encoder?.ForceKeyFrameRequested);
        Assert.AreEqual(targetFps, encoderFactory.Encoder?.Settings.Fps);
        Assert.AreEqual(targetFps, encoderFactory.Encoder?.Settings.KeyFrameInterval);
    }

    [DataTestMethod]
    [DataRow("server_cancellation")]
    [DataRow("socket_close")]
    [DataRow("desktop_stop")]
    [DataRow("usb_disconnect")]
    public async Task Active_encode_finishes_before_encoder_disposal(string shutdownKind)
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new CountingFrameSource(CreateTinyFrame(71));
        var encoderFactory = new BlockingH264EncoderFactory();
        var lifetime = new VideoSessionLifetime();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            TextWriter.Null,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
            },
            h264EncoderFactory: encoderFactory,
            videoSessionLifetime: lifetime);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(1280, 800, 30, VideoCodecMask.H264)));
        var ack = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(ack).AsTask().WaitAsync(TestTimeout);
        await encoderFactory.EncodeStarted.Task.WaitAsync(TestTimeout);

        switch (shutdownKind)
        {
            case "server_cancellation":
                cancellation.Cancel();
                break;
            case "socket_close":
                client.Dispose();
                break;
            case "desktop_stop":
                await desktop.StopAsync(CancellationToken.None);
                break;
            case "usb_disconnect":
                lifetime.NotifyControlDisconnected();
                break;
            default:
                Assert.Fail($"Unknown shutdown kind {shutdownKind}.");
                break;
        }

        await encoderFactory.Disposed.Task.WaitAsync(TestTimeout);
        Assert.IsFalse(encoderFactory.DisposeWhileEncoding);
        Assert.IsTrue(encoderFactory.EncodeExitedBeforeDispose);
        Assert.AreEqual(1, encoderFactory.DisposeCount);

        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Session_boundary_skips_frame_from_already_connected_monitor()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        var source = new BoundaryFrameSource(CreateTinyFrame(908));
        var encoderFactory = new RecordingH264EncoderFactory();
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            source,
            TextWriter.Null,
            port: 0,
            options: VideoPipelineOptions.Default with
            {
                CodecPreference = VideoCodecPreference.H264,
            },
            h264EncoderFactory: encoderFactory);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(1280, 800, 30, VideoCodecMask.H264)));
        var ack = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(ack).AsTask().WaitAsync(TestTimeout);
        await source.BoundaryObserved.Task.WaitAsync(TestTimeout);

        await Task.Delay(150);
        Assert.IsNull(
            encoderFactory.Encoder,
            "The pre-session frame reached the encoder.");

        source.Publish(CreateTinyFrame(909));
        var configuration = await ReadH264PacketAsync(stream);
        var keyFrame = await ReadH264PacketAsync(stream);
        Assert.AreEqual(H264PacketFlags.Config, configuration.Header.Flags);
        Assert.AreEqual(909UL, configuration.Header.Sequence);
        Assert.AreEqual(909UL, keyFrame.Header.Sequence);

        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    [TestMethod]
    public async Task Auto_negotiation_informs_client_of_zlib_fallback_when_H264_is_unavailable()
    {
        await using var desktop = new FakeDesktopService(TimeSpan.Zero);
        await desktop.StartAsync(new DesktopRequest(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var server = new PanelVideoServer(
            desktop,
            new EmptyFrameSource(),
            TextWriter.Null,
            port: 0);
        var serverTask = server.RunAsync(cancellation.Token);
        using var client = await ConnectAsync(server.BoundPort);
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(
                1280,
                800,
                30,
                VideoCodecMask.H264 | VideoCodecMask.ZlibBgra8888)));
        var response = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TestTimeout);

        var ack = PanelVideoProtocol.ParseAck(response);
        Assert.AreEqual(VideoCodec.ZlibBgra8888, ack.SelectedCodec);
        Assert.AreEqual(640u, ack.MaxWidth);
        Assert.AreEqual(400u, ack.MaxHeight);
        Assert.AreEqual(10u, ack.Fps);

        cancellation.Cancel();
        await serverTask.WaitAsync(TestTimeout);
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(TestTimeout);
        return client;
    }

    private static async Task<VideoMessageType> HandshakeAsync(TcpClient client)
    {
        await client.GetStream().WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(
                640,
                400,
                10,
                VideoCodecMask.ZlibBgra8888)));
        return (await ReadResponseAsync(client)).Type;
    }

    private static async Task<VideoAck> HandshakeH264Async(
        TcpClient client,
        int clientMaxFps)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(PanelVideoProtocol.EncodeHello(
            new VideoHello(
                1280,
                800,
                checked((uint)clientMaxFps),
                VideoCodecMask.H264)));
        var response = new byte[PanelVideoProtocol.AckSize];
        await stream.ReadExactlyAsync(response).AsTask().WaitAsync(TestTimeout);
        return PanelVideoProtocol.ParseAck(response);
    }

    private static async Task<(VideoMessageType Type, VideoRejectCode RejectCode)>
        ReadResponseAsync(TcpClient client)
    {
        var response = new byte[PanelVideoProtocol.AckSize];
        await client.GetStream().ReadExactlyAsync(response)
            .AsTask()
            .WaitAsync(TestTimeout);
        var type = (VideoMessageType)BitConverter.ToUInt16(response, 6);
        var reject = type == VideoMessageType.Reject
            ? PanelVideoProtocol.ParseReject(response)
            : default;
        return (type, reject);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for video server condition.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<(H264PacketHeader Header, byte[] Payload)>
        ReadH264PacketAsync(NetworkStream stream)
    {
        var headerBytes = new byte[PanelVideoProtocol.H264PacketHeaderSize];
        await stream.ReadExactlyAsync(headerBytes).AsTask().WaitAsync(TestTimeout);
        var header = PanelVideoProtocol.ParseH264PacketHeader(headerBytes);
        var payload = new byte[header.PayloadBytes];
        await stream.ReadExactlyAsync(payload).AsTask().WaitAsync(TestTimeout);
        return (header, payload);
    }

    private static void AssertPixel(
        byte[] pixels,
        int stride,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        var offset = checked(y * stride + x * 4);
        CollectionAssert.AreEqual(
            new[] { blue, green, red, alpha },
            pixels[offset..(offset + 4)]);
    }

    private static FrameSnapshot CreateTinyFrame(ulong sequence) => new(
        Generation: 7,
        Sequence: sequence,
        CaptureTimestampMicros: VideoSessionLatency.GetCurrentTimestampMicros(),
        Width: 2,
        Height: 2,
        Stride: 8,
        Format: PanelDisplayFrameFormat.Bgra8888,
        Pixels:
        [
            0, 0, 255, 255, 0, 255, 0, 255,
            255, 0, 0, 255, 255, 255, 255, 255,
        ]);

    private sealed class CountingFrameSource : IFrameSource
    {
        private readonly FrameSnapshot? _frame;
        private int _callCount;

        internal CountingFrameSource(FrameSnapshot? frame = null) =>
            _frame = frame;

        internal int CallCount => Volatile.Read(ref _callCount);

        public Task<FrameSnapshot?> GetLatestFrameAsync(
            ulong lastSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(
                _frame is not null && _frame.Sequence > lastSequence
                    ? _frame
                    : null);
        }
    }

    private sealed class ConfigurableFrameSource :
        IFrameSource,
        IFrameSourceCaptureConfiguration
    {
        private readonly List<int> _configuredTargets = [];
        private readonly object _sync = new();

        internal IReadOnlyList<int> ConfiguredTargets
        {
            get
            {
                lock (_sync)
                {
                    return _configuredTargets.ToArray();
                }
            }
        }

        public Task ConfigureCaptureTargetFpsAsync(
            int targetFramesPerSecond,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _configuredTargets.Add(targetFramesPerSecond);
            }

            return Task.CompletedTask;
        }

        public Task<FrameSnapshot?> GetLatestFrameAsync(
            ulong lastSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<FrameSnapshot?>(null);
        }
    }

    private sealed class BoundaryFrameSource(FrameSnapshot initialFrame) :
        IFrameSource,
        IFrameSourceSessionBoundary
    {
        private readonly object _sync = new();
        private FrameSnapshot _latest = initialFrame;

        internal TaskCompletionSource BoundaryObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal void Publish(FrameSnapshot frame)
        {
            lock (_sync)
            {
                _latest = frame;
            }
        }

        public Task<FrameSourceSessionBoundary> BeginSessionAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FrameSnapshot latest;
            lock (_sync)
            {
                latest = _latest;
            }

            BoundaryObserved.TrySetResult();
            return Task.FromResult(new FrameSourceSessionBoundary(
                latest.Generation,
                latest.Sequence));
        }

        public Task<FrameSnapshot?> GetLatestFrameAsync(
            ulong lastSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return Task.FromResult<FrameSnapshot?>(
                    _latest.Sequence > lastSequence ? _latest : null);
            }
        }
    }

    private sealed class RecordingH264EncoderFactory : IH264EncoderFactory
    {
        private static readonly H264EncoderDescriptor Descriptor =
            new("Test hardware encoder", IsHardware: true);

        internal RecordingH264Encoder? Encoder { get; private set; }

        public H264EncoderDescriptor? SelectedEncoder => Descriptor;

        public Task<H264EncoderDescriptor?> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<H264EncoderDescriptor?>(Descriptor);
        }

        public Task<IH264Encoder> CreateAsync(
            H264EncoderSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Encoder = new RecordingH264Encoder(settings, Descriptor);
            return Task.FromResult<IH264Encoder>(Encoder);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingH264EncoderFactory : IH264EncoderFactory
    {
        private static readonly H264EncoderDescriptor Descriptor =
            new("Blocking test encoder", IsHardware: true);

        private int _disposeWhileEncoding;
        private int _encodeExitedBeforeDispose;
        private int _disposeCount;

        internal TaskCompletionSource EncodeStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Disposed { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool DisposeWhileEncoding =>
            Volatile.Read(ref _disposeWhileEncoding) != 0;

        internal bool EncodeExitedBeforeDispose =>
            Volatile.Read(ref _encodeExitedBeforeDispose) != 0;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public H264EncoderDescriptor? SelectedEncoder => Descriptor;

        public Task<H264EncoderDescriptor?> ProbeAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<H264EncoderDescriptor?>(Descriptor);
        }

        public Task<IH264Encoder> CreateAsync(
            H264EncoderSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IH264Encoder>(new BlockingH264Encoder(
                settings,
                Descriptor,
                this));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class BlockingH264Encoder(
            H264EncoderSettings settings,
            H264EncoderDescriptor descriptor,
            BlockingH264EncoderFactory owner) : IH264Encoder
        {
            private int _encoding;
            private int _encodeExited;

            public H264EncoderDescriptor Descriptor { get; } = descriptor;

            public H264EncoderSettings Settings { get; } = settings;

            public async Task<IReadOnlyList<H264EncodedAccessUnit>> EncodeAsync(
                Nv12VideoFrame frame,
                bool forceKeyFrame,
                CancellationToken cancellationToken)
            {
                Interlocked.Exchange(ref _encoding, 1);
                owner.EncodeStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return Array.Empty<H264EncodedAccessUnit>();
                }
                finally
                {
                    Interlocked.Exchange(ref _encodeExited, 1);
                    Interlocked.Exchange(ref _encoding, 0);
                }
            }

            public ValueTask DisposeAsync()
            {
                if (Volatile.Read(ref _encoding) != 0)
                {
                    Interlocked.Exchange(ref owner._disposeWhileEncoding, 1);
                }
                if (Volatile.Read(ref _encodeExited) != 0)
                {
                    Interlocked.Exchange(ref owner._encodeExitedBeforeDispose, 1);
                }

                Interlocked.Increment(ref owner._disposeCount);
                owner.Disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingH264Encoder(
        H264EncoderSettings settings,
        H264EncoderDescriptor descriptor) : IH264Encoder
    {
        internal static readonly byte[] Configuration = [0, 0, 0, 1, 0x67, 0x42];
        internal static readonly byte[] KeyFrame = [0, 0, 0, 1, 0x65, 0x01];

        public H264EncoderDescriptor Descriptor { get; } = descriptor;

        public H264EncoderSettings Settings { get; } = settings;

        internal bool Disposed { get; private set; }

        internal bool ForceKeyFrameRequested { get; private set; }

        public Task<IReadOnlyList<H264EncodedAccessUnit>> EncodeAsync(
            Nv12VideoFrame frame,
            bool forceKeyFrame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForceKeyFrameRequested = forceKeyFrame;
            IReadOnlyList<H264EncodedAccessUnit> result =
            [
                new H264EncodedAccessUnit(
                    frame.Sequence,
                    frame.CaptureTimestampMicros,
                    KeyFrame,
                    IsKeyFrame: true,
                    EncodeDurationMicros: 700,
                    DecoderConfiguration: Configuration),
            ];
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
