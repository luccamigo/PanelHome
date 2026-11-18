using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;
using PanelHost.Video;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelDisplayDriverFrameSourceTests
{
    [TestMethod]
    [DataRow(30)]
    [DataRow(45)]
    [DataRow(60)]
    public async Task Capture_target_is_forwarded_to_driver(int targetFps)
    {
        var client = new FakeFrameClient();
        using var log = new StringWriter();
        var source = new PanelDisplayDriverFrameSource(client, log);

        await source.ConfigureCaptureTargetFpsAsync(
            targetFps,
            CancellationToken.None);

        Assert.AreEqual((uint)targetFps, client.ConfiguredTargetFps);
        StringAssert.Contains(
            log.ToString(),
            $"sendingDriverTarget={targetFps} success=true configured={targetFps} " +
            $"protocol=v{PanelDisplayDriverContract.ProtocolVersion}");
    }

    [TestMethod]
    public async Task Missing_v5_capture_configuration_fails_instead_of_using_30_fps()
    {
        using var log = new StringWriter();
        var source = new PanelDisplayDriverFrameSource(
            new ReadOnlyFrameClient(),
            log);

        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            source.ConfigureCaptureTargetFpsAsync(60, CancellationToken.None));

        StringAssert.Contains(
            log.ToString(),
            "sendingDriverTarget=60 success=false");
        StringAssert.Contains(log.ToString(), "reason=capture_configuration_unavailable");
    }

    [TestMethod]
    public async Task Runtime_target_changes_30_45_60_30_are_forwarded()
    {
        var client = new FakeFrameClient();
        var source = new PanelDisplayDriverFrameSource(client);

        await source.ConfigureCaptureTargetFpsAsync(30, CancellationToken.None);
        await source.ConfigureCaptureTargetFpsAsync(45, CancellationToken.None);
        await source.ConfigureCaptureTargetFpsAsync(60, CancellationToken.None);
        await source.ConfigureCaptureTargetFpsAsync(30, CancellationToken.None);

        Assert.AreEqual(4, client.ConfigurationCount);
        Assert.AreEqual(30u, client.ConfiguredTargetFps);
    }

    [TestMethod]
    public async Task New_session_with_connected_monitor_uses_latest_sequence_as_boundary()
    {
        var client = new FakeFrameClient
        {
            InfoHandler = _ => FrameInfo() with
            {
                Generation = 9,
                Sequence = 908,
            },
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var boundary = await source.BeginSessionAsync(CancellationToken.None);

        Assert.AreEqual(9UL, boundary.Generation);
        Assert.AreEqual(908UL, boundary.LastSequence);
        Assert.AreEqual(0UL, client.LastSequence);
        Assert.AreEqual(0, client.ReadCount);
    }

    [TestMethod]
    public async Task No_new_frame_returns_null_without_reading_payload()
    {
        var client = new FakeFrameClient
        {
            InfoHandler = _ => FrameInfo(PanelDisplayFrameResult.NoNewFrame),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var frame = await source.GetLatestFrameAsync(42, CancellationToken.None);

        Assert.IsNull(frame);
        Assert.AreEqual(42UL, client.LastSequence);
        Assert.AreEqual(0, client.ReadCount);
    }

    [TestMethod]
    public async Task Available_frame_is_validated_and_qpc_becomes_microseconds()
    {
        var info = FrameInfo();
        var pixels = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var client = new FakeFrameClient
        {
            InfoHandler = _ => info,
            ReadHandler = _ => new PanelDisplayDriverFrame(info, pixels),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var frame = await source.GetLatestFrameAsync(0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(7UL, frame.Generation);
        Assert.AreEqual(11UL, frame.Sequence);
        Assert.AreEqual(2_500_000UL, frame.CaptureTimestampMicros);
        CollectionAssert.AreEqual(pixels, frame.Pixels);
    }

    [TestMethod]
    public async Task Replaced_frame_is_requeried_without_becoming_fatal()
    {
        var first = FrameInfo() with { Sequence = 11 };
        var second = FrameInfo() with { Sequence = 12 };
        var infoCalls = 0;
        var client = new FakeFrameClient
        {
            InfoHandler = _ => Interlocked.Increment(ref infoCalls) == 1
                ? first
                : second,
            ReadHandler = info => info.Sequence == first.Sequence
                ? new PanelDisplayDriverFrame(
                    info with { Result = PanelDisplayFrameResult.FrameReplaced },
                    [])
                : new PanelDisplayDriverFrame(second, new byte[32]),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var frame = await source.GetLatestFrameAsync(10, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(12UL, frame.Sequence);
        Assert.AreEqual(2, client.ReadCount);
    }

    [TestMethod]
    public async Task Buffer_size_race_is_requeried_without_becoming_fatal()
    {
        var info = FrameInfo();
        var attempts = 0;
        var client = new FakeFrameClient
        {
            InfoHandler = _ => info,
            ReadHandler = _ => Interlocked.Increment(ref attempts) == 1
                ? throw new PanelDisplayDriverException(
                    PanelDisplayDriverFailureKind.IoError,
                    "READ_LATEST_FRAME",
                    "Scripted size race.",
                    122,
                    @"\\?\panel-display-test")
                : new PanelDisplayDriverFrame(info, new byte[32]),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var frame = await source.GetLatestFrameAsync(0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(11UL, frame.Sequence);
        Assert.AreEqual(2, client.ReadCount);
    }

    [TestMethod]
    public async Task Invalid_payload_length_is_rejected()
    {
        var info = FrameInfo();
        var client = new FakeFrameClient
        {
            InfoHandler = _ => info,
            ReadHandler = _ => new PanelDisplayDriverFrame(info, new byte[31]),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            source.GetLatestFrameAsync(0, CancellationToken.None));
    }

    [TestMethod]
    public async Task Padded_stride_and_second_row_are_preserved_for_processor()
    {
        var info = FrameInfo() with
        {
            Width = 2,
            Height = 2,
            Stride = 12,
            PayloadBytes = 24,
        };
        var pixels = new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 90, 91, 92, 93,
            11, 12, 13, 14, 15, 16, 17, 18, 94, 95, 96, 97,
        };
        var client = new FakeFrameClient
        {
            InfoHandler = _ => info,
            ReadHandler = _ => new PanelDisplayDriverFrame(info, pixels),
        };
        var source = new PanelDisplayDriverFrameSource(client);

        var frame = await source.GetLatestFrameAsync(0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(12, frame.Stride);
        CollectionAssert.AreEqual(pixels, frame.Pixels);
        var compact = VideoFrameProcessor.NormalizeAndResize(frame, 2, 2);
        CollectionAssert.AreEqual(
            new byte[]
            {
                1, 2, 3, 255, 5, 6, 7, 255,
                11, 12, 13, 255, 15, 16, 17, 255,
            },
            compact);
    }

    private static PanelDisplayFrameInfo FrameInfo(
        PanelDisplayFrameResult result = PanelDisplayFrameResult.Ok) => new(
            result,
            Generation: result == PanelDisplayFrameResult.Ok ? 7UL : 0UL,
            Sequence: result == PanelDisplayFrameResult.Ok ? 11UL : 0UL,
            CaptureTimestampQpc: result == PanelDisplayFrameResult.Ok ? 25UL : 0UL,
            QpcFrequency: result == PanelDisplayFrameResult.Ok ? 10UL : 0UL,
            Width: result == PanelDisplayFrameResult.Ok ? 4U : 0U,
            Height: result == PanelDisplayFrameResult.Ok ? 2U : 0U,
            Stride: result == PanelDisplayFrameResult.Ok ? 16U : 0U,
            Format: result == PanelDisplayFrameResult.Ok
                ? PanelDisplayFrameFormat.Bgra8888
                : PanelDisplayFrameFormat.Unknown,
            PayloadBytes: result == PanelDisplayFrameResult.Ok ? 32U : 0U);

    private sealed class FakeFrameClient :
        IPanelDisplayDriverFrameClient,
        IPanelDisplayDriverCaptureConfigurationClient
    {
        internal Func<ulong, PanelDisplayFrameInfo>? InfoHandler { get; init; }

        internal Func<PanelDisplayFrameInfo, PanelDisplayDriverFrame>? ReadHandler
        {
            get;
            init;
        }

        internal ulong LastSequence { get; private set; }

        internal int ReadCount { get; private set; }

        internal uint ConfiguredTargetFps { get; private set; }

        internal int ConfigurationCount { get; private set; }

        public Task<uint> SetCaptureTargetFpsAsync(
            uint targetFramesPerSecond,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationCount++;
            ConfiguredTargetFps = targetFramesPerSecond;
            return Task.FromResult(targetFramesPerSecond);
        }

        public Task<PanelDisplayFrameInfo> GetFrameInfoAsync(
            ulong lastSequence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSequence = lastSequence;
            return Task.FromResult((InfoHandler ?? throw new InvalidOperationException())(
                lastSequence));
        }

        public Task<PanelDisplayDriverFrame> ReadFrameAsync(
            PanelDisplayFrameInfo requestedFrame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return Task.FromResult((ReadHandler ?? throw new InvalidOperationException())(
                requestedFrame));
        }
    }

    private sealed class ReadOnlyFrameClient : IPanelDisplayDriverFrameClient
    {
        public Task<PanelDisplayFrameInfo> GetFrameInfoAsync(
            ulong lastSequence,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PanelDisplayDriverFrame> ReadFrameAsync(
            PanelDisplayFrameInfo requestedFrame,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
