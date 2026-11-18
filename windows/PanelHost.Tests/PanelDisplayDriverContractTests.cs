using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelOS.DriverInterop;

namespace PanelHost.Tests;

[TestClass]
public sealed class PanelDisplayDriverContractTests
{
    [TestMethod]
    public void DriverInteropTypesComeFromSharedAssembly()
    {
        const string expectedAssembly = "PanelOS.DriverInterop";
        Type[] representativeTypes =
        [
            typeof(PanelDisplayDriverContract),
            typeof(PanelDisplayDriverClient),
            typeof(IPanelDisplayDriverClient),
            typeof(PanelDisplayDriverStatus),
            typeof(PanelDisplayFrameInfo),
            typeof(PanelDisplayCaptureMetrics),
        ];

        foreach (var type in representativeTypes)
        {
            Assert.AreEqual(
                expectedAssembly,
                type.Assembly.GetName().Name,
                $"{type.FullName} came from an unexpected assembly.");
        }

        Assert.AreNotEqual(
            "PanelHost",
            typeof(PanelDisplayDriverContract).Assembly.GetName().Name);
        Assert.AreNotEqual(
            "PanelOS.Windows",
            typeof(PanelDisplayDriverContract).Assembly.GetName().Name);
    }

    [TestMethod]
    public void ConstantsMatchPublicHeaderProtocolV5()
    {
        Assert.AreEqual(5u, PanelDisplayDriverContract.ProtocolVersion);
        Assert.AreEqual(
            new Guid("2F21C12F-51EC-48CB-93D1-776DC7468B1B"),
            PanelDisplayDriverContract.DeviceInterfaceGuid);
        Assert.AreEqual(8, PanelDisplayDriverContract.MaxReportedModes);
        Assert.AreEqual(
            64u * 1024 * 1024,
            PanelDisplayDriverContract.MaximumFramePayloadBytes);
        Assert.AreEqual(
            0x00000001u,
            PanelDisplayDriverContract.ConnectFlagUsePreferredMode);
    }

    [TestMethod]
    public void IoctlValuesMatchPublicHeaderCtlCodeDefinitions()
    {
        const uint fileDeviceUnknown = 0x22;
        const uint methodBuffered = 0;
        const uint methodOutDirect = 2;
        const uint fileReadAccess = 1;
        const uint fileWriteAccess = 2;
        const uint panelIoctlBase = 0x900;

        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase,
                methodBuffered,
                fileWriteAccess),
            PanelDisplayDriverContract.IoctlConnectDisplay);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 1,
                methodBuffered,
                fileWriteAccess),
            PanelDisplayDriverContract.IoctlDisconnectDisplay);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 2,
                methodBuffered,
                fileReadAccess),
            PanelDisplayDriverContract.IoctlGetStatus);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 3,
                methodBuffered,
                fileReadAccess),
            PanelDisplayDriverContract.IoctlGetFrameInfo);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 4,
                methodOutDirect,
                fileReadAccess),
            PanelDisplayDriverContract.IoctlReadLatestFrame);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 5,
                methodBuffered,
                fileReadAccess),
            PanelDisplayDriverContract.IoctlGetCaptureMetrics);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 6,
                methodBuffered,
                fileWriteAccess),
            PanelDisplayDriverContract.IoctlSetCaptureConfig);
        Assert.AreEqual(
            CtlCode(
                fileDeviceUnknown,
                panelIoctlBase + 7,
                methodBuffered,
                fileReadAccess),
            PanelDisplayDriverContract.IoctlGetCapturePacingMetrics);

        Assert.AreEqual(0x0022A400u, PanelDisplayDriverContract.IoctlConnectDisplay);
        Assert.AreEqual(0x0022A404u, PanelDisplayDriverContract.IoctlDisconnectDisplay);
        Assert.AreEqual(0x00226408u, PanelDisplayDriverContract.IoctlGetStatus);
        Assert.AreEqual(0x0022640Cu, PanelDisplayDriverContract.IoctlGetFrameInfo);
        Assert.AreEqual(0x00226412u, PanelDisplayDriverContract.IoctlReadLatestFrame);
        Assert.AreEqual(0x00226414u, PanelDisplayDriverContract.IoctlGetCaptureMetrics);
        Assert.AreEqual(0x0022A418u, PanelDisplayDriverContract.IoctlSetCaptureConfig);
        Assert.AreEqual(
            0x0022641Cu,
            PanelDisplayDriverContract.IoctlGetCapturePacingMetrics);
    }

    [TestMethod]
    public void EnumValuesMatchPublicHeader()
    {
        Assert.AreEqual(0, (int)PanelDisplayDriverState.Initializing);
        Assert.AreEqual(1, (int)PanelDisplayDriverState.Ready);
        Assert.AreEqual(2, (int)PanelDisplayDriverState.Stopped);

        Assert.AreEqual(0, (int)PanelDisplayMonitorState.Disconnected);
        Assert.AreEqual(1, (int)PanelDisplayMonitorState.Connected);

        Assert.AreEqual(0, (int)PanelDisplayCommandResult.Ok);
        Assert.AreEqual(1, (int)PanelDisplayCommandResult.AlreadyConnected);
        Assert.AreEqual(2, (int)PanelDisplayCommandResult.AlreadyDisconnected);
        Assert.AreEqual(3, (int)PanelDisplayCommandResult.AdapterNotReady);
        Assert.AreEqual(4, (int)PanelDisplayCommandResult.InvalidMode);
        Assert.AreEqual(5, (int)PanelDisplayCommandResult.Failed);

        Assert.AreEqual(0u, (uint)PanelDisplayFrameResult.Ok);
        Assert.AreEqual(1u, (uint)PanelDisplayFrameResult.NoFrame);
        Assert.AreEqual(2u, (uint)PanelDisplayFrameResult.NoNewFrame);
        Assert.AreEqual(3u, (uint)PanelDisplayFrameResult.FrameReplaced);
        Assert.AreEqual(0u, (uint)PanelDisplayFrameFormat.Unknown);
        Assert.AreEqual(1u, (uint)PanelDisplayFrameFormat.Bgra8888);
        Assert.AreEqual(2u, (uint)PanelDisplayFrameFormat.Rgba8888);
    }

    [TestMethod]
    public void NativeStructureSizesMatchPublicHeader()
    {
        Assert.AreEqual(
            PanelDisplayDriverContract.DisplayModeSize,
            Marshal.SizeOf<PanelDisplayDriverContract.DisplayModeNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.ConnectInputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.ConnectInputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.CommandOutputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.CommandOutputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.StatusOutputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.StatusOutputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.FrameQuerySize,
            Marshal.SizeOf<PanelDisplayDriverContract.FrameQueryNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.FrameReadInputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.FrameReadInputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.FrameInfoSize,
            Marshal.SizeOf<PanelDisplayDriverContract.FrameInfoNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.CaptureConfigInputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.CaptureConfigInputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.CaptureConfigOutputSize,
            Marshal.SizeOf<PanelDisplayDriverContract.CaptureConfigOutputNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.CaptureMetricsSize,
            Marshal.SizeOf<PanelDisplayDriverContract.CaptureMetricsNative>());
        Assert.AreEqual(
            PanelDisplayDriverContract.CapturePacingMetricsSize,
            Marshal.SizeOf<
                PanelDisplayDriverContract.CapturePacingMetricsNative>());
    }

    [TestMethod]
    public void StatusLayoutPreservesNativeAlignmentAndFixedModesArray()
    {
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("ProtocolVersion", 4);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("DriverState", 8);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("MonitorState", 12);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("ActiveMode", 16);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("FramesProcessed", 32);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("ConnectCount", 40);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("ModeCount", 44);
        AssertOffset<PanelDisplayDriverContract.StatusOutputNative>("Modes", 48);
    }

    [TestMethod]
    public void SmallerNativeLayoutsMatchPublicHeaderFieldOrder()
    {
        AssertOffset<PanelDisplayDriverContract.DisplayModeNative>("Width", 0);
        AssertOffset<PanelDisplayDriverContract.DisplayModeNative>("Height", 4);
        AssertOffset<PanelDisplayDriverContract.DisplayModeNative>("RefreshHz", 8);

        AssertOffset<PanelDisplayDriverContract.ConnectInputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.ConnectInputNative>("Flags", 4);
        AssertOffset<PanelDisplayDriverContract.ConnectInputNative>("PreferredMode", 8);

        AssertOffset<PanelDisplayDriverContract.CommandOutputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.CommandOutputNative>("Result", 4);
        AssertOffset<PanelDisplayDriverContract.CommandOutputNative>("MonitorState", 8);

        AssertOffset<PanelDisplayDriverContract.FrameQueryNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.FrameQueryNative>("LastSequence", 8);
        AssertOffset<PanelDisplayDriverContract.FrameReadInputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.FrameReadInputNative>("Generation", 8);
        AssertOffset<PanelDisplayDriverContract.FrameReadInputNative>("Sequence", 16);

        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Result", 4);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Generation", 8);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Sequence", 16);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("CaptureTimestampQpc", 24);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("QpcFrequency", 32);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Width", 40);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Height", 44);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Stride", 48);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Format", 52);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("PayloadBytes", 56);
        AssertOffset<PanelDisplayDriverContract.FrameInfoNative>("Reserved", 60);

        AssertOffset<PanelDisplayDriverContract.CaptureConfigInputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.CaptureConfigInputNative>(
            "TargetFramesPerSecond", 4);
        AssertOffset<PanelDisplayDriverContract.CaptureConfigOutputNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.CaptureConfigOutputNative>(
            "ProtocolVersion", 4);
        AssertOffset<PanelDisplayDriverContract.CaptureConfigOutputNative>(
            "TargetFramesPerSecond", 8);
        AssertOffset<PanelDisplayDriverContract.CaptureConfigOutputNative>("Reserved", 12);

        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("ProtocolVersion", 4);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("Generation", 8);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("QpcFrequency", 16);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("SwapchainAcquired", 24);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("CaptureThrottleSkipped", 32);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("CopySubmitted", 40);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("MapSuccess", 48);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("MapBusy", 56);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("MapFailure", 64);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("Published", 72);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("PublishDropped", 80);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("AcquireQpcTotal", 88);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("CopyQpcTotal", 96);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("MapAttemptQpcTotal", 104);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("CpuPublishQpcTotal", 112);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>(
            "TargetFramesPerSecond", 120);
        AssertOffset<PanelDisplayDriverContract.CaptureMetricsNative>("Reserved", 124);

        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>("Size", 0);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "ProtocolVersion", 4);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "Generation", 8);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "QpcFrequency", 16);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "ScheduledFrames", 24);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "MissedDeadlines", 32);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "DeadlineLatenessSamples", 40);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "DeadlineLatenessQpcTotal", 48);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "TargetFramesPerSecond", 56);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "SchedulerTargetFramesPerSecond", 60);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "HistogramBucketCount", 64);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "Reserved", 68);
        AssertOffset<PanelDisplayDriverContract.CapturePacingMetricsNative>(
            "DeadlineLatenessHistogram", 72);
    }

    private static void AssertOffset<T>(string fieldName, int expected)
        where T : struct =>
        Assert.AreEqual(
            expected,
            Marshal.OffsetOf<T>(fieldName).ToInt32(),
            $"Unexpected native offset for {typeof(T).Name}.{fieldName}.");

    private static uint CtlCode(
        uint deviceType,
        uint function,
        uint method,
        uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;
}
