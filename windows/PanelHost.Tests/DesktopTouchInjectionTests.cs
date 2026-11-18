using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PanelHost.Input;
using PanelHost.Services;

namespace PanelHost.Tests;

[TestClass]
public sealed class DesktopTouchInjectionTests
{
    private static readonly TimeSpan DisabledHoldInterval = TimeSpan.FromHours(1);

    [DataTestMethod]
    [DataRow(0d, 0d, -1920, 0)]
    [DataRow(1d, 1d, -1, 1199)]
    [DataRow(0.5d, 0.5d, -960, 600)]
    [DataRow(-2d, 4d, -1920, 1199)]
    public void Normalized_coordinates_map_and_clamp_on_negative_monitor(
        double x,
        double y,
        int expectedX,
        int expectedY)
    {
        var point = PanelDisplayCoordinateMapper.Map(
            new PanelDisplayBounds(-1920, 0, 1920, 1200),
            x,
            y);

        Assert.AreEqual(new ScreenTouchPoint(expectedX, expectedY), point);
    }

    [TestMethod]
    public void Coordinate_mapping_supports_positive_and_above_primary_monitors()
    {
        Assert.AreEqual(
            new ScreenTouchPoint(1970, 250),
            PanelDisplayCoordinateMapper.Map(
                new PanelDisplayBounds(1920, 200, 100, 100),
                0.5,
                0.5));
        Assert.AreEqual(
            new ScreenTouchPoint(1919, -1),
            PanelDisplayCoordinateMapper.Map(
                new PanelDisplayBounds(0, -1200, 1920, 1200),
                1,
                1));
    }

    [TestMethod]
    public void Down_move_and_up_emit_the_required_native_state_sequence()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var generation = service.ActiveGeneration;

        Assert.IsTrue(service.Process(Command(DesktopTouchAction.Down, generation, 0, 0)).Accepted);
        Assert.IsTrue(service.Process(Command(DesktopTouchAction.Move, generation, 0.5, 0.5)).Accepted);
        Assert.IsTrue(service.Process(Command(DesktopTouchAction.Up, generation, 0.5, 0.5)).Accepted);

        CollectionAssert.AreEqual(
            new[]
            {
                PointerFlags.InRange | PointerFlags.InContact | PointerFlags.Down,
                PointerFlags.InRange | PointerFlags.InContact | PointerFlags.Update,
                PointerFlags.Up,
            },
            native.Contacts.Select(contact => contact.PointerInfo.PointerFlags).ToArray());
        Assert.AreEqual(new NativePoint(-1920, 0), native.Contacts[0].PointerInfo.PixelLocation);
        Assert.AreEqual(new NativePoint(-960, 600), native.Contacts[1].PointerInfo.PixelLocation);
        Assert.AreEqual(native.Contacts[1].PointerInfo.PixelLocation, native.Contacts[2].PointerInfo.PixelLocation);
        Assert.AreEqual((uint)0, native.Contacts[0].PointerInfo.PointerId);
        Assert.AreEqual(default(NativePoint), native.Contacts[0].PointerInfo.PixelLocationRaw);
        Assert.AreEqual(default(NativeRect), native.Contacts[0].ContactRaw);
        Assert.AreEqual((uint)0, native.Contacts[0].PointerInfo.Time);
        Assert.AreEqual((ulong)0, native.Contacts[0].PointerInfo.PerformanceCount);
    }

    [TestMethod]
    public void Up_at_a_new_coordinate_emits_final_update_before_up()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var generation = service.ActiveGeneration;

        service.Process(Command(DesktopTouchAction.Down, generation, 0.1, 0.1));
        service.Process(Command(DesktopTouchAction.Up, generation, 0.9, 0.8));

        Assert.AreEqual(3, native.Contacts.Count);
        Assert.AreEqual(
            PointerFlags.InRange | PointerFlags.InContact | PointerFlags.Update,
            native.Contacts[1].PointerInfo.PointerFlags);
        Assert.AreEqual(PointerFlags.Up, native.Contacts[2].PointerInfo.PointerFlags);
        Assert.AreEqual(
            native.Contacts[1].PointerInfo.PixelLocation,
            native.Contacts[2].PointerInfo.PixelLocation);
    }

    [TestMethod]
    public void Cancel_emits_canceled_up_and_duplicate_or_orphan_events_are_ignored()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var generation = service.ActiveGeneration;

        Assert.IsFalse(service.Process(Command(DesktopTouchAction.Move, generation, 0.2, 0.2)).Accepted);
        Assert.IsFalse(service.Process(Command(DesktopTouchAction.Up, generation, 0.2, 0.2)).Accepted);
        Assert.IsTrue(service.Process(Command(DesktopTouchAction.Down, generation, 0.2, 0.2)).Accepted);
        Assert.IsFalse(service.Process(Command(DesktopTouchAction.Down, generation, 0.3, 0.3)).Accepted);
        Assert.IsTrue(service.Process(Command(DesktopTouchAction.Cancel, generation)).Accepted);

        Assert.AreEqual(2, native.Contacts.Count);
        Assert.AreEqual(
            PointerFlags.Canceled | PointerFlags.Up,
            native.Contacts[1].PointerInfo.PointerFlags);
    }

    [TestMethod]
    public void Desktop_stop_and_control_disconnect_cancel_an_active_contact()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var firstGeneration = service.ActiveGeneration;
        service.Process(Command(DesktopTouchAction.Down, firstGeneration, 0.2, 0.2));

        service.SetDesktopState(new DesktopServiceSnapshot(DesktopServiceState.Stopped));

        Assert.AreEqual(PointerFlags.Canceled | PointerFlags.Up, native.Contacts[^1].PointerInfo.PointerFlags);
        Assert.AreEqual(0L, service.ActiveGeneration);

        service.SetDesktopState(new DesktopServiceSnapshot(DesktopServiceState.Running));
        var secondGeneration = service.ActiveGeneration;
        Assert.IsTrue(secondGeneration > firstGeneration);
        service.Process(Command(DesktopTouchAction.Down, secondGeneration, 0.4, 0.4));
        service.DeactivateDesktop("test disconnect");

        Assert.AreEqual(PointerFlags.Canceled | PointerFlags.Up, native.Contacts[^1].PointerInfo.PointerFlags);
        Assert.AreEqual(0L, service.ActiveGeneration);
    }

    [TestMethod]
    public void Stale_generation_is_ignored_without_touching_the_new_session()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var currentGeneration = service.ActiveGeneration;

        var result = service.Process(
            Command(DesktopTouchAction.Down, currentGeneration + 1, 0.5, 0.5));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("invalid_generation", result.ErrorCode);
        Assert.AreEqual(0, native.Contacts.Count);
    }

    [TestMethod]
    public void Injection_failure_is_structured_counted_and_does_not_throw()
    {
        var native = new FakeTouchNativeApi { NextInjectionResult = false };
        using var service = CreateRunningService(native);

        var result = service.Process(
            Command(DesktopTouchAction.Down, service.ActiveGeneration, 0.5, 0.5));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("touch_injection_failed", result.ErrorCode);
        Assert.AreEqual(new TouchInjectionMetrics(1, 0, 1), service.Metrics);
    }

    [TestMethod]
    public void Error_not_ready_retries_the_identical_frame_without_advancing_state()
    {
        var native = new FakeTouchNativeApi
        {
            NextInjectionResult = false,
            NextError = NativeErrors.NotReady,
        };
        using var service = CreateRunningService(native);

        var result = service.Process(
            Command(DesktopTouchAction.Down, service.ActiveGeneration, 0.5, 0.5));

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(2, native.Contacts.Count);
        Assert.AreEqual(native.Contacts[0].PointerInfo, native.Contacts[1].PointerInfo);
        Assert.AreEqual(new TouchInjectionMetrics(1, 1, 0), service.Metrics);
        service.Process(Command(DesktopTouchAction.Cancel, service.ActiveGeneration));
    }

    [TestMethod]
    public void Move_injection_failure_attempts_explicit_cancel_at_last_known_point()
    {
        var native = new FakeTouchNativeApi();
        using var service = CreateRunningService(native);
        var generation = service.ActiveGeneration;
        service.Process(Command(DesktopTouchAction.Down, generation, 0.2, 0.2));
        native.NextInjectionResult = false;

        var result = service.Process(
            Command(DesktopTouchAction.Move, generation, 0.8, 0.8));

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual("touch_injection_failed", result.ErrorCode);
        Assert.AreEqual(3, native.Contacts.Count);
        Assert.AreEqual(
            PointerFlags.Canceled | PointerFlags.Up,
            native.Contacts[^1].PointerInfo.PointerFlags);
        Assert.AreEqual(
            native.Contacts[0].PointerInfo.PixelLocation,
            native.Contacts[^1].PointerInfo.PixelLocation);
        Assert.AreEqual(new TouchInjectionMetrics(2, 2, 1), service.Metrics);
    }

    [TestMethod]
    public void Stationary_contact_receives_a_low_frequency_hold_update()
    {
        var native = new FakeTouchNativeApi();
        using var service = new WindowsTouchInjectionService(
            native,
            new FakePanelDisplayLocator(new PanelDisplayBounds(0, 0, 100, 100)),
            TextWriter.Null,
            TimeSpan.FromMilliseconds(10));
        service.SetDesktopState(new DesktopServiceSnapshot(DesktopServiceState.Running));
        service.Process(Command(DesktopTouchAction.Down, service.ActiveGeneration, 0.5, 0.5));
        native.Injected.Reset();

        Assert.IsTrue(native.Injected.Wait(TimeSpan.FromSeconds(1)));
        service.Process(Command(DesktopTouchAction.Up, service.ActiveGeneration, 0.5, 0.5));

        Assert.IsTrue(native.Contacts.Any(contact =>
            contact.PointerInfo.PointerFlags ==
                (PointerFlags.InRange | PointerFlags.InContact | PointerFlags.Update)));
    }

    [TestMethod]
    public void Display_locator_selects_panel_target_by_identity_and_source_mode()
    {
        var adapter = new DisplayConfigLuid(7, -2);
        var native = new FakeDisplayConfigNativeApi(
            [
                Path(adapter, sourceId: 4, targetId: 10),
                Path(adapter, sourceId: 8, targetId: 11),
            ],
            [
                SourceMode(adapter, id: 4, left: 0, top: 0, width: 1920, height: 1080),
                SourceMode(adapter, id: 8, left: -1280, top: -120, width: 1280, height: 800),
            ],
            panelTargetId: 11);
        var locator = new WindowsPanelDisplayLocator(native);

        Assert.IsTrue(locator.TryLocate(out var bounds, out var detail));
        Assert.AreEqual(new PanelDisplayBounds(-1280, -120, 1280, 800), bounds);
        StringAssert.Contains(detail, "PanelOS");
    }

    [TestMethod]
    public void Native_interop_layouts_match_the_Windows_x64_contract()
    {
        Assert.AreEqual(72, Marshal.SizeOf<DisplayConfigPathInfo>());
        Assert.AreEqual(64, Marshal.SizeOf<DisplayConfigModeInfo>());
        Assert.AreEqual(420, Marshal.SizeOf<DisplayConfigTargetDeviceName>());
        Assert.AreEqual(96, Marshal.SizeOf<PointerInfo>());
        Assert.AreEqual(144, Marshal.SizeOf<PointerTouchInfo>());
        Assert.AreEqual((IntPtr)0, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PointerType)));
        Assert.AreEqual((IntPtr)4, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PointerId)));
        Assert.AreEqual((IntPtr)12, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PointerFlags)));
        Assert.AreEqual((IntPtr)16, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.SourceDevice)));
        Assert.AreEqual((IntPtr)24, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.WindowTarget)));
        Assert.AreEqual((IntPtr)32, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PixelLocation)));
        Assert.AreEqual((IntPtr)48, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PixelLocationRaw)));
        Assert.AreEqual((IntPtr)64, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.Time)));
        Assert.AreEqual((IntPtr)80, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.PerformanceCount)));
        Assert.AreEqual((IntPtr)88, Marshal.OffsetOf<PointerInfo>(nameof(PointerInfo.ButtonChangeType)));
        Assert.AreEqual((IntPtr)96, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.TouchFlags)));
        Assert.AreEqual((IntPtr)100, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.TouchMask)));
        Assert.AreEqual((IntPtr)104, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.Contact)));
        Assert.AreEqual((IntPtr)120, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.ContactRaw)));
        Assert.AreEqual((IntPtr)136, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.Orientation)));
        Assert.AreEqual((IntPtr)140, Marshal.OffsetOf<PointerTouchInfo>(nameof(PointerTouchInfo.Pressure)));
    }

    [TestMethod]
    public void Native_touch_constants_match_installed_winuser_header()
    {
        Assert.AreEqual(2u, (uint)PointerInputType.Touch);
        Assert.AreEqual(0x00000002u, (uint)PointerFlags.InRange);
        Assert.AreEqual(0x00000004u, (uint)PointerFlags.InContact);
        Assert.AreEqual(0x00000010u, (uint)PointerFlags.FirstButton);
        Assert.AreEqual(0x00002000u, (uint)PointerFlags.Primary);
        Assert.AreEqual(0x00004000u, (uint)PointerFlags.Confidence);
        Assert.AreEqual(0x00008000u, (uint)PointerFlags.Canceled);
        Assert.AreEqual(0x00010000u, (uint)PointerFlags.Down);
        Assert.AreEqual(0x00020000u, (uint)PointerFlags.Update);
        Assert.AreEqual(0x00040000u, (uint)PointerFlags.Up);
        Assert.AreEqual(0u, (uint)TouchFlags.None);
        Assert.AreEqual(1u, (uint)TouchMask.ContactArea);
    }

    private static WindowsTouchInjectionService CreateRunningService(FakeTouchNativeApi native)
    {
        var service = new WindowsTouchInjectionService(
            native,
            new FakePanelDisplayLocator(new PanelDisplayBounds(-1920, 0, 1920, 1200)),
            TextWriter.Null,
            DisabledHoldInterval);
        service.SetDesktopState(new DesktopServiceSnapshot(DesktopServiceState.Running));
        return service;
    }

    private static NormalizedTouchCommand Command(
        DesktopTouchAction action,
        long generation,
        double? x = null,
        double? y = null) =>
        new(action, 0, x, y, generation);

    private static DisplayConfigPathInfo Path(
        DisplayConfigLuid adapter,
        uint sourceId,
        uint targetId) =>
        new()
        {
            SourceInfo = new DisplayConfigPathSourceInfo
            {
                AdapterId = adapter,
                Id = sourceId,
            },
            TargetInfo = new DisplayConfigPathTargetInfo
            {
                AdapterId = adapter,
                Id = targetId,
                TargetAvailable = true,
            },
        };

    private static DisplayConfigModeInfo SourceMode(
        DisplayConfigLuid adapter,
        uint id,
        int left,
        int top,
        uint width,
        uint height) =>
        new()
        {
            InfoType = DisplayConfigModeInfoType.Source,
            Id = id,
            AdapterId = adapter,
            ModeInfo = new DisplayConfigModeInfoUnion
            {
                SourceMode = new DisplayConfigSourceMode
                {
                    Width = width,
                    Height = height,
                    Position = new NativePoint(left, top),
                },
            },
        };
}

internal sealed class FakeTouchNativeApi : IWindowsTouchNativeApi
{
    internal List<PointerTouchInfo> Contacts { get; } = [];
    internal ManualResetEventSlim Injected { get; } = new(false);
    internal bool NextInjectionResult { get; set; } = true;
    internal int NextError { get; set; } = 87;
    private int _lastError = 87;

    public bool InitializeTouchInjection(uint maxCount, TouchFeedbackMode feedbackMode)
    {
        Assert.AreEqual((uint)1, maxCount);
        Assert.AreEqual(TouchFeedbackMode.None, feedbackMode);
        return true;
    }

    public bool InjectTouchInput(uint count, PointerTouchInfo[] contacts)
    {
        Assert.AreEqual((uint)1, count);
        Contacts.Add(contacts.Single());
        Injected.Set();
        var result = NextInjectionResult;
        _lastError = NextError;
        NextInjectionResult = true;
        NextError = 87;
        return result;
    }

    public int GetLastError() => _lastError;
}

internal sealed class FakePanelDisplayLocator(PanelDisplayBounds bounds) : IPanelDisplayLocator
{
    public bool TryLocate(out PanelDisplayBounds locatedBounds, out string detail)
    {
        locatedBounds = bounds;
        detail = "PanelOS test display";
        return true;
    }
}

internal sealed class FakeDisplayConfigNativeApi(
    DisplayConfigPathInfo[] paths,
    DisplayConfigModeInfo[] modes,
    uint panelTargetId) : IDisplayConfigNativeApi
{
    public int GetDisplayConfigBufferSizes(
        DisplayConfigQueryFlags flags,
        out uint pathCount,
        out uint modeCount)
    {
        pathCount = checked((uint)paths.Length);
        modeCount = checked((uint)modes.Length);
        return 0;
    }

    public int QueryDisplayConfig(
        DisplayConfigQueryFlags flags,
        ref uint pathCount,
        DisplayConfigPathInfo[] pathBuffer,
        ref uint modeCount,
        DisplayConfigModeInfo[] modeBuffer)
    {
        paths.CopyTo(pathBuffer, 0);
        modes.CopyTo(modeBuffer, 0);
        return 0;
    }

    public int GetTargetDeviceName(ref DisplayConfigTargetDeviceName targetName)
    {
        targetName.MonitorFriendlyDeviceName = targetName.Header.Id == panelTargetId
            ? "PanelOS"
            : "Primary Display";
        targetName.MonitorDevicePath = targetName.Header.Id == panelTargetId
            ? @"\\?\DISPLAY#PNL0001#TEST"
            : @"\\?\DISPLAY#OTHER#TEST";
        return 0;
    }
}
