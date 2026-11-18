using System.Runtime.InteropServices;

namespace PanelOS.DriverInterop;

/// <summary>
/// Managed representation of driver/PanelDisplayDriver/driver/Public.h.
/// Public.h remains the authoritative control contract; keep these values and
/// layouts synchronized whenever PANEL_DISPLAY_PROTOCOL_VERSION changes.
/// </summary>
public static class PanelDisplayDriverContract
{
    public const uint ProtocolVersion = 5;

    public static readonly Guid DeviceInterfaceGuid =
        new("2F21C12F-51EC-48CB-93D1-776DC7468B1B");

    public const uint IoctlConnectDisplay = 0x0022A400;
    public const uint IoctlDisconnectDisplay = 0x0022A404;
    public const uint IoctlGetStatus = 0x00226408;
    public const uint IoctlGetFrameInfo = 0x0022640C;
    public const uint IoctlReadLatestFrame = 0x00226412;
    public const uint IoctlGetCaptureMetrics = 0x00226414;
    public const uint IoctlSetCaptureConfig = 0x0022A418;
    public const uint IoctlGetCapturePacingMetrics = 0x0022641C;

    public const uint ConnectFlagUsePreferredMode = 0x00000001;
    public const int MaxReportedModes = 8;

    public const int DisplayModeSize = 12;
    public const int ConnectInputSize = 20;
    public const int CommandOutputSize = 12;
    public const int StatusOutputSize = 144;
    public const int FrameQuerySize = 16;
    public const int FrameReadInputSize = 24;
    public const int FrameInfoSize = 64;
    public const int CaptureConfigInputSize = 8;
    public const int CaptureConfigOutputSize = 16;
    public const int CaptureMetricsSize = 128;
    public const int CapturePacingMetricsSize = 200;
    public const int CaptureLatenessHistogramBuckets = 16;
    public static readonly ulong[] CaptureLatenessBucketUpperMicros =
    [
        250, 500, 1_000, 1_500, 2_000, 3_000, 4_000, 5_000,
        6_000, 8_000, 10_000, 12_000, 16_000, 24_000, 33_000, 1_000_000,
    ];
    public const uint MaximumFramePayloadBytes = 64 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayModeNative
    {
        internal uint Width;
        internal uint Height;
        internal uint RefreshHz;

        internal readonly PanelDisplayMode ToManaged() =>
            new(Width, Height, RefreshHz);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ConnectInputNative
    {
        internal uint Size;
        internal uint Flags;
        internal DisplayModeNative PreferredMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CommandOutputNative
    {
        internal uint Size;
        internal PanelDisplayCommandResult Result;
        internal PanelDisplayMonitorState MonitorState;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StatusOutputNative
    {
        internal uint Size;
        internal uint ProtocolVersion;
        internal PanelDisplayDriverState DriverState;
        internal PanelDisplayMonitorState MonitorState;
        internal DisplayModeNative ActiveMode;
        internal ulong FramesProcessed;
        internal uint ConnectCount;
        internal uint ModeCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxReportedModes)]
        internal DisplayModeNative[]? Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameQueryNative
    {
        internal uint Size;
        internal uint Reserved;
        internal ulong LastSequence;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameReadInputNative
    {
        internal uint Size;
        internal uint Reserved;
        internal ulong Generation;
        internal ulong Sequence;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FrameInfoNative
    {
        internal uint Size;
        internal PanelDisplayFrameResult Result;
        internal ulong Generation;
        internal ulong Sequence;
        internal ulong CaptureTimestampQpc;
        internal ulong QpcFrequency;
        internal uint Width;
        internal uint Height;
        internal uint Stride;
        internal PanelDisplayFrameFormat Format;
        internal uint PayloadBytes;
        internal uint Reserved;

        internal readonly PanelDisplayFrameInfo ToManaged() => new(
            Result,
            Generation,
            Sequence,
            CaptureTimestampQpc,
            QpcFrequency,
            Width,
            Height,
            Stride,
            Format,
            PayloadBytes);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaptureConfigInputNative
    {
        internal uint Size;
        internal uint TargetFramesPerSecond;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaptureConfigOutputNative
    {
        internal uint Size;
        internal uint ProtocolVersion;
        internal uint TargetFramesPerSecond;
        internal uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CaptureMetricsNative
    {
        internal uint Size;
        internal uint ProtocolVersion;
        internal ulong Generation;
        internal ulong QpcFrequency;
        internal ulong SwapchainAcquired;
        internal ulong CaptureThrottleSkipped;
        internal ulong CopySubmitted;
        internal ulong MapSuccess;
        internal ulong MapBusy;
        internal ulong MapFailure;
        internal ulong Published;
        internal ulong PublishDropped;
        internal ulong AcquireQpcTotal;
        internal ulong CopyQpcTotal;
        internal ulong MapAttemptQpcTotal;
        internal ulong CpuPublishQpcTotal;
        internal uint TargetFramesPerSecond;
        internal uint Reserved;

        internal readonly PanelDisplayCaptureMetrics ToManaged(
            CapturePacingMetricsNative pacing) => new(
            Generation,
            QpcFrequency,
            SwapchainAcquired,
            CaptureThrottleSkipped,
            CopySubmitted,
            MapSuccess,
            MapBusy,
            MapFailure,
            Published,
            PublishDropped,
            AcquireQpcTotal,
            CopyQpcTotal,
            MapAttemptQpcTotal,
            CpuPublishQpcTotal,
            TargetFramesPerSecond,
            pacing.ScheduledFrames,
            pacing.MissedDeadlines,
            pacing.DeadlineLatenessSamples,
            pacing.DeadlineLatenessQpcTotal,
            pacing.SchedulerTargetFramesPerSecond,
            pacing.DeadlineLatenessHistogram ??
                new ulong[CaptureLatenessHistogramBuckets]);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CapturePacingMetricsNative
    {
        internal uint Size;
        internal uint ProtocolVersion;
        internal ulong Generation;
        internal ulong QpcFrequency;
        internal ulong ScheduledFrames;
        internal ulong MissedDeadlines;
        internal ulong DeadlineLatenessSamples;
        internal ulong DeadlineLatenessQpcTotal;
        internal uint TargetFramesPerSecond;
        internal uint SchedulerTargetFramesPerSecond;
        internal uint HistogramBucketCount;
        internal uint Reserved;

        [MarshalAs(
            UnmanagedType.ByValArray,
            SizeConst = CaptureLatenessHistogramBuckets)]
        internal ulong[]? DeadlineLatenessHistogram;
    }
}

public enum PanelDisplayDriverState
{
    Initializing = 0,
    Ready = 1,
    Stopped = 2,
}

public enum PanelDisplayMonitorState
{
    Disconnected = 0,
    Connected = 1,
}

public enum PanelDisplayCommandResult
{
    Ok = 0,
    AlreadyConnected = 1,
    AlreadyDisconnected = 2,
    AdapterNotReady = 3,
    InvalidMode = 4,
    Failed = 5,
}

public readonly record struct PanelDisplayMode(
    uint Width,
    uint Height,
    uint RefreshHz);

public sealed record PanelDisplayDriverStatus(
    uint ProtocolVersion,
    PanelDisplayDriverState DriverState,
    PanelDisplayMonitorState MonitorState,
    PanelDisplayMode ActiveMode,
    ulong FramesProcessed,
    uint ConnectCount,
    IReadOnlyList<PanelDisplayMode> Modes);

public readonly record struct PanelDisplayCommandResponse(
    PanelDisplayCommandResult Result,
    PanelDisplayMonitorState MonitorState);

public enum PanelDisplayFrameResult : uint
{
    Ok = 0,
    NoFrame = 1,
    NoNewFrame = 2,
    FrameReplaced = 3,
}

public enum PanelDisplayFrameFormat : uint
{
    Unknown = 0,
    Bgra8888 = 1,
    Rgba8888 = 2,
}

public readonly record struct PanelDisplayFrameInfo(
    PanelDisplayFrameResult Result,
    ulong Generation,
    ulong Sequence,
    ulong CaptureTimestampQpc,
    ulong QpcFrequency,
    uint Width,
    uint Height,
    uint Stride,
    PanelDisplayFrameFormat Format,
    uint PayloadBytes);

public sealed record PanelDisplayDriverFrame(
    PanelDisplayFrameInfo Info,
    byte[] Pixels);

public readonly record struct PanelDisplayCaptureMetrics(
    ulong Generation,
    ulong QpcFrequency,
    ulong SwapchainAcquired,
    ulong CaptureThrottleSkipped,
    ulong CopySubmitted,
    ulong MapSuccess,
    ulong MapBusy,
    ulong MapFailure,
    ulong Published,
    ulong PublishDropped,
    ulong AcquireQpcTotal,
    ulong CopyQpcTotal,
    ulong MapAttemptQpcTotal,
    ulong CpuPublishQpcTotal,
    uint TargetFramesPerSecond,
    ulong ScheduledFrames,
    ulong MissedDeadlines,
    ulong DeadlineLatenessSamples,
    ulong DeadlineLatenessQpcTotal,
    uint SchedulerTargetFramesPerSecond,
    IReadOnlyList<ulong> DeadlineLatenessHistogram);
