using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Hosting;
using PanelOS.DriverInterop;
using PanelHost.Video;
using SharpGen.Runtime;

namespace PanelHost.Tests;

[TestClass]
public sealed class H264PipelineTests
{
    [TestMethod]
    public void EncoderSelectionPrefersHardware()
    {
        var selected = H264EncoderSelection.Select(
        [
            new H264EncoderDescriptor("software", false),
            new H264EncoderDescriptor("hardware", true),
        ]);

        Assert.AreEqual("hardware", selected?.Name);
        Assert.IsTrue(selected?.IsHardware);
    }

    [TestMethod]
    public void EncoderSelectionFallsBackToSoftware()
    {
        var selected = H264EncoderSelection.Select(
        [
            new H264EncoderDescriptor("software", false),
        ]);

        Assert.AreEqual("software", selected?.Name);
        Assert.IsFalse(selected?.IsHardware);
    }

    [TestMethod]
    public void DefaultConfigurationTargets1280x800At60Fps()
    {
        var options = VideoPipelineOptions.Default;
        var settings = options.CreateH264Settings(1280, 800);

        Assert.AreEqual(1280, settings.Width);
        Assert.AreEqual(800, settings.Height);
        Assert.AreEqual(60, settings.Fps);
        Assert.AreEqual(10_000_000, settings.Bitrate);
        Assert.AreEqual(60, settings.KeyFrameInterval);
    }

    [TestMethod]
    public void TargetFpsDefaultsTo60WithoutAnArgument()
    {
        Assert.AreEqual(60, ParseOptions([]).VideoTargetFps);
    }

    [TestMethod]
    public void ConfigurationRoundsNv12DimensionsDownWithoutUpscaling()
    {
        var settings = VideoPipelineOptions.Default.CreateH264Settings(1279, 799);

        Assert.AreEqual(1278, settings.Width);
        Assert.AreEqual(798, settings.Height);
    }

    [TestMethod]
    public void CommandLineSelectsExplicitCodec()
    {
        Assert.AreEqual(
            VideoCodecPreference.H264,
            ParseOptions(["--video-codec", "h264"]).VideoCodec);
        Assert.AreEqual(
            VideoCodecPreference.Zlib,
            ParseOptions(["--video-codec", "zlib"]).VideoCodec);
    }

    [TestMethod]
    [DataRow(30)]
    [DataRow(45)]
    [DataRow(60)]
    public void TargetFpsOptionConfiguresPacingAndOneSecondGop(int targetFps)
    {
        Assert.AreEqual(
            targetFps,
            ParseOptions(["--video-target-fps", targetFps.ToString()]).VideoTargetFps);

        var settings = (VideoPipelineOptions.Default with
        {
            PreferredSessionFps = targetFps,
        }).CreateH264Settings(1280, 800);

        Assert.AreEqual(targetFps, settings.Fps);
        Assert.AreEqual(targetFps, settings.KeyFrameInterval);
    }

    [TestMethod]
    public void TargetFpsOptionRejectsUnsupportedValues()
    {
        var commandLine = new PanelHostCommandLine((_, _) => Task.FromResult(0));
        Assert.AreNotEqual(
            0,
            commandLine.Parse(["--video-target-fps", "50"]).Errors.Count);
    }

    private static PanelHostOptions ParseOptions(string[] args)
    {
        var commandLine = new PanelHostCommandLine((_, _) => Task.FromResult(0));
        return commandLine.Bind(commandLine.Parse(args));
    }

    [DataTestMethod]
    [DataRow(60, 60)]
    [DataRow(45, 45)]
    [DataRow(30, 30)]
    public void ClientMaximumDefinesEffectiveEncoderFpsAndOneSecondGop(
        int clientMaxFps,
        int expectedEffectiveFps)
    {
        var negotiation = VideoFrameRatePolicy.Negotiate(
            VideoFrameRatePolicy.DefaultPreferredSessionFps,
            checked((uint)clientMaxFps));
        var settings = VideoPipelineOptions.Default.CreateH264Settings(
            1280,
            800,
            negotiation.EffectiveSessionFps);

        Assert.AreEqual(60, negotiation.HostPreferredFps);
        Assert.AreEqual(clientMaxFps, negotiation.ClientMaxFps);
        Assert.AreEqual(expectedEffectiveFps, negotiation.EffectiveSessionFps);
        Assert.AreEqual(expectedEffectiveFps, settings.Fps);
        Assert.AreEqual(expectedEffectiveFps, settings.KeyFrameInterval);
    }

    [DataTestMethod]
    [DataRow(30, 61)]
    [DataRow(45, 91)]
    [DataRow(60, 121)]
    public void DriverPollingRunsAboveTargetWithoutHarmonicPhaseLock(
        int targetFps,
        int expectedPollsPerSecond)
    {
        var pollsPerSecond = VideoFramePollingCadence.PollsPerSecond(targetFps);

        Assert.AreEqual(expectedPollsPerSecond, pollsPerSecond);
        Assert.IsTrue(pollsPerSecond > targetFps);
        Assert.AreEqual(1, GreatestCommonDivisor(pollsPerSecond, targetFps));
        Assert.AreEqual(
            1d / expectedPollsPerSecond,
            VideoFramePollingCadence.Interval(targetFps).TotalSeconds,
            0.000_000_1d);
    }

    [TestMethod]
    public void BgraRedConvertsToExpectedNv12LimitedRange()
    {
        var source = Frame(
            PanelDisplayFrameFormat.Bgra8888,
            stride: 8,
            [
                0, 0, 255, 0, 0, 0, 255, 255,
                0, 0, 255, 10, 0, 0, 255, 20,
            ]);

        var converted = new BgraToNv12FrameConverter().Convert(source, 2, 2);

        CollectionAssert.AreEqual(
            new byte[] { 63, 63, 63, 63, 102, 240 },
            converted.Pixels);
    }

    [TestMethod]
    public void RgbaConversionHonorsPaddedStride()
    {
        var source = Frame(
            PanelDisplayFrameFormat.Rgba8888,
            stride: 12,
            [
                255, 0, 0, 0, 255, 0, 0, 255, 99, 99, 99, 99,
                255, 0, 0, 0, 255, 0, 0, 255, 88, 88, 88, 88,
            ]);

        var converted = new BgraToNv12FrameConverter().Convert(source, 2, 2);

        CollectionAssert.AreEqual(
            new byte[] { 63, 63, 63, 63, 102, 240 },
            converted.Pixels);
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }

    [TestMethod]
    public void NativeResolutionFastPathAdvancesAcrossEverySourcePixel()
    {
        byte[] pixels =
        [
            0, 0, 0, 255, 0, 0, 0, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            0, 0, 0, 255, 0, 0, 0, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
        ];
        var source = new FrameSnapshot(
            1,
            1,
            100,
            Width: 4,
            Height: 2,
            Stride: 16,
            PanelDisplayFrameFormat.Bgra8888,
            pixels);

        var converted = new BgraToNv12FrameConverter().Convert(source, 4, 2);

        CollectionAssert.AreEqual(
            new byte[]
            {
                16, 16, 235, 235,
                16, 16, 235, 235,
                128, 128, 127, 128,
            },
            converted.Pixels);
    }

    [TestMethod]
    public void LengthPrefixedAccessUnitBecomesAnnexB()
    {
        var source = new byte[11];
        BinaryPrimitives.WriteUInt32BigEndian(source, 2);
        source[4] = 0x67;
        source[5] = 0x64;
        BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(6), 1);
        source[10] = 0x65;

        var annexB = H264AnnexB.NormalizeAccessUnit(source);

        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0, 0, 0, 1, 0x65 },
            annexB);
        Assert.IsTrue(H264AnnexB.ContainsIdr(annexB));
    }

    [TestMethod]
    public void AvcConfigurationRecordExtractsSpsAndPps()
    {
        byte[] avcConfiguration =
        [
            1, 100, 0, 31, 0xff,
            0xe1, 0, 2, 0x67, 0x64,
            1, 0, 2, 0x68, 0xee,
        ];

        var annexB = H264AnnexB.NormalizeSequenceHeader(avcConfiguration);

        CollectionAssert.AreEqual(
            new byte[] { 0, 0, 0, 1, 0x67, 0x64, 0, 0, 0, 1, 0x68, 0xee },
            annexB);
        CollectionAssert.AreEqual(annexB, H264AnnexB.ExtractParameterSets(annexB));
    }

    [TestMethod]
    public void H264PacketHeaderRoundTripsFlagsAndTiming()
    {
        var packet = new H264VideoPacket(
            42,
            123_456,
            H264PacketFlags.KeyFrame | H264PacketFlags.EndOfAccessUnit,
            [0, 0, 0, 1, 0x65],
            2_500);

        var bytes = PanelVideoProtocol.EncodeH264PacketHeader(packet);
        var decoded = PanelVideoProtocol.ParseH264PacketHeader(bytes);

        Assert.AreEqual(42UL, decoded.Sequence);
        Assert.AreEqual(123_456UL, decoded.CaptureTimestampMicros);
        Assert.AreEqual(
            H264PacketFlags.KeyFrame | H264PacketFlags.EndOfAccessUnit,
            decoded.Flags);
        Assert.AreEqual(5U, decoded.PayloadBytes);
        Assert.AreEqual(2_500UL, decoded.EncodeDurationMicros);
    }

    [TestMethod]
    public void H264OnlyHelloIsAccepted()
    {
        var hello = new VideoHello(1280, 800, 30, VideoCodecMask.H264);

        Assert.AreEqual(
            hello,
            PanelVideoProtocol.ParseHello(PanelVideoProtocol.EncodeHello(hello)));
    }

    [TestMethod]
    public void Vortice_output_alias_is_detached_without_releasing_owner()
    {
        var owner = new ComObject((nint)0x1234);
        var alias = new ComObject(owner.NativePointer);
        try
        {
            Assert.IsTrue(
                MediaFoundationComLifetime.DetachNonOwningAlias(alias, owner));
            Assert.AreEqual(nint.Zero, alias.NativePointer);
            Assert.AreEqual((nint)0x1234, owner.NativePointer);
        }
        finally
        {
            // Synthetic pointers are never valid COM references.
            alias.NativePointer = 0;
            owner.NativePointer = 0;
        }
    }

    [TestMethod]
    public void Distinct_returned_sample_keeps_its_independent_owner()
    {
        var owner = new ComObject((nint)0x1234);
        var returned = new ComObject((nint)0x5678);
        try
        {
            Assert.IsFalse(
                MediaFoundationComLifetime.DetachNonOwningAlias(returned, owner));
            Assert.AreEqual((nint)0x5678, returned.NativePointer);
            Assert.AreEqual((nint)0x1234, owner.NativePointer);
        }
        finally
        {
            // Synthetic pointers are never valid COM references.
            returned.NativePointer = 0;
            owner.NativePointer = 0;
        }
    }

    private static FrameSnapshot Frame(
        PanelDisplayFrameFormat format,
        int stride,
        byte[] pixels) => new(
            Generation: 1,
            Sequence: 1,
            CaptureTimestampMicros: 100,
            Width: 2,
            Height: 2,
            Stride: stride,
            Format: format,
            Pixels: pixels);
}
