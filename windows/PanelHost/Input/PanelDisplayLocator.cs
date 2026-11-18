using System.Runtime.InteropServices;

namespace PanelHost.Input;

internal sealed class WindowsPanelDisplayLocator : IPanelDisplayLocator
{
    private readonly IDisplayConfigNativeApi _native;

    internal WindowsPanelDisplayLocator()
        : this(WindowsDisplayConfigNativeApi.Instance)
    {
    }

    internal WindowsPanelDisplayLocator(IDisplayConfigNativeApi native)
    {
        _native = native;
    }

    public bool TryLocate(out PanelDisplayBounds bounds, out string detail)
    {
        bounds = default;
        detail = string.Empty;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var sizeResult = _native.GetDisplayConfigBufferSizes(
                DisplayConfigQueryFlags.OnlyActivePaths,
                out var pathCount,
                out var modeCount);
            if (sizeResult != 0)
            {
                detail = $"GetDisplayConfigBufferSizes failed: Win32={sizeResult}";
                return false;
            }

            var paths = new DisplayConfigPathInfo[pathCount];
            var modes = new DisplayConfigModeInfo[modeCount];
            var queryResult = _native.QueryDisplayConfig(
                DisplayConfigQueryFlags.OnlyActivePaths,
                ref pathCount,
                paths,
                ref modeCount,
                modes);
            if (queryResult == NativeErrors.InsufficientBuffer)
            {
                continue;
            }
            if (queryResult != 0)
            {
                detail = $"QueryDisplayConfig failed: Win32={queryResult}";
                return false;
            }

            var matches = new List<(PanelDisplayBounds Bounds, string Identity)>();
            foreach (var path in paths.Take(checked((int)pathCount)))
            {
                var targetName = DisplayConfigTargetDeviceName.Create(path.TargetInfo);
                var nameResult = _native.GetTargetDeviceName(ref targetName);
                if (nameResult != 0 || !IsPanelOsTarget(targetName))
                {
                    continue;
                }

                var sourceMode = modes
                    .Take(checked((int)modeCount))
                    .FirstOrDefault(mode =>
                        mode.InfoType == DisplayConfigModeInfoType.Source &&
                        mode.Id == path.SourceInfo.Id &&
                        mode.AdapterId == path.SourceInfo.AdapterId);
                if (sourceMode.InfoType != DisplayConfigModeInfoType.Source)
                {
                    continue;
                }

                var source = sourceMode.ModeInfo.SourceMode;
                var candidate = new PanelDisplayBounds(
                    source.Position.X,
                    source.Position.Y,
                    checked((int)source.Width),
                    checked((int)source.Height));
                if (candidate.IsValid)
                {
                    matches.Add((
                        candidate,
                        $"friendlyName=\"{targetName.MonitorFriendlyDeviceName}\" " +
                        $"devicePath=\"{targetName.MonitorDevicePath}\""));
                }
            }

            if (matches.Count == 1)
            {
                bounds = matches[0].Bounds;
                detail = matches[0].Identity;
                return true;
            }

            detail = matches.Count == 0
                ? "No active PanelOS/PNL0001 display target was found."
                : $"Multiple active PanelOS display targets were found ({matches.Count}).";
            return false;
        }

        detail = "Display configuration changed repeatedly during discovery.";
        return false;
    }

    private static bool IsPanelOsTarget(DisplayConfigTargetDeviceName targetName) =>
        targetName.MonitorFriendlyDeviceName.Contains(
            "PanelOS",
            StringComparison.OrdinalIgnoreCase) ||
        targetName.MonitorDevicePath.Contains(
            "PNL0001",
            StringComparison.OrdinalIgnoreCase);
}

internal interface IDisplayConfigNativeApi
{
    int GetDisplayConfigBufferSizes(
        DisplayConfigQueryFlags flags,
        out uint pathCount,
        out uint modeCount);

    int QueryDisplayConfig(
        DisplayConfigQueryFlags flags,
        ref uint pathCount,
        DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        DisplayConfigModeInfo[] modes);

    int GetTargetDeviceName(ref DisplayConfigTargetDeviceName targetName);
}

internal sealed class WindowsDisplayConfigNativeApi : IDisplayConfigNativeApi
{
    internal static WindowsDisplayConfigNativeApi Instance { get; } = new();

    public int GetDisplayConfigBufferSizes(
        DisplayConfigQueryFlags flags,
        out uint pathCount,
        out uint modeCount) =>
        NativeMethods.GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount);

    public int QueryDisplayConfig(
        DisplayConfigQueryFlags flags,
        ref uint pathCount,
        DisplayConfigPathInfo[] paths,
        ref uint modeCount,
        DisplayConfigModeInfo[] modes) =>
        NativeMethods.QueryDisplayConfig(
            flags,
            ref pathCount,
            paths,
            ref modeCount,
            modes,
            IntPtr.Zero);

    public int GetTargetDeviceName(ref DisplayConfigTargetDeviceName targetName) =>
        NativeMethods.DisplayConfigGetDeviceInfo(ref targetName);
}

[Flags]
internal enum DisplayConfigQueryFlags : uint
{
    OnlyActivePaths = 0x00000002,
}

internal enum DisplayConfigModeInfoType : uint
{
    Source = 1,
    Target = 2,
    DesktopImage = 3,
}

internal enum DisplayConfigDeviceInfoType : uint
{
    GetTargetName = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct DisplayConfigLuid(uint LowPart, int HighPart);

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativePoint(int X, int Y);

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathSourceInfo
{
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigRational
{
    internal uint Numerator;
    internal uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathTargetInfo
{
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint OutputTechnology;
    internal uint Rotation;
    internal uint Scaling;
    internal DisplayConfigRational RefreshRate;
    internal uint ScanLineOrdering;
    [MarshalAs(UnmanagedType.Bool)]
    internal bool TargetAvailable;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigPathInfo
{
    internal DisplayConfigPathSourceInfo SourceInfo;
    internal DisplayConfigPathTargetInfo TargetInfo;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigSourceMode
{
    internal uint Width;
    internal uint Height;
    internal uint PixelFormat;
    internal NativePoint Position;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DisplayConfigModeInfoUnion
{
    [FieldOffset(0)]
    internal DisplayConfigSourceMode SourceMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigModeInfo
{
    internal DisplayConfigModeInfoType InfoType;
    internal uint Id;
    internal DisplayConfigLuid AdapterId;
    internal DisplayConfigModeInfoUnion ModeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayConfigDeviceInfoHeader
{
    internal DisplayConfigDeviceInfoType Type;
    internal uint Size;
    internal DisplayConfigLuid AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayConfigTargetDeviceName
{
    internal DisplayConfigDeviceInfoHeader Header;
    internal uint Flags;
    internal uint OutputTechnology;
    internal ushort EdidManufactureId;
    internal ushort EdidProductCodeId;
    internal uint ConnectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string MonitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string MonitorDevicePath;

    internal static DisplayConfigTargetDeviceName Create(
        DisplayConfigPathTargetInfo target) =>
        new()
        {
            Header = new DisplayConfigDeviceInfoHeader
            {
                Type = DisplayConfigDeviceInfoType.GetTargetName,
                Size = checked((uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>()),
                AdapterId = target.AdapterId,
                Id = target.Id,
            },
            MonitorFriendlyDeviceName = string.Empty,
            MonitorDevicePath = string.Empty,
        };
}

internal static partial class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern int GetDisplayConfigBufferSizes(
        DisplayConfigQueryFlags flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    internal static extern int QueryDisplayConfig(
        DisplayConfigQueryFlags flags,
        ref uint numPathArrayElements,
        [In, Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [In, Out] DisplayConfigModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    internal static extern int DisplayConfigGetDeviceInfo(
        ref DisplayConfigTargetDeviceName requestPacket);
}

internal static class NativeErrors
{
    internal const int NotReady = 21;
    internal const int InsufficientBuffer = 122;
}
