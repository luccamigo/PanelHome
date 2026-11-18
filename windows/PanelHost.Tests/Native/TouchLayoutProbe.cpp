#include <windows.h>
#include <cstddef>
#include <cstdio>
#include <cstdlib>

static void run_touch_probe(int x, int y)
{
    const auto initialized = InitializeTouchInjection(1, TOUCH_FEEDBACK_INDIRECT);
    std::printf(
        "InitializeTouchInjection=%d error=%lu target=(%d,%d)\n",
        initialized,
        initialized ? ERROR_SUCCESS : GetLastError(),
        x,
        y);
    if (!initialized)
    {
        return;
    }

    POINTER_TOUCH_INFO contact{};
    contact.pointerInfo.pointerType = PT_TOUCH;
    contact.pointerInfo.pointerId = 0;
    contact.pointerInfo.pointerFlags =
        POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;
    contact.pointerInfo.ptPixelLocation = {x, y};
    contact.touchFlags = TOUCH_FLAG_NONE;
    contact.touchMask = TOUCH_MASK_CONTACTAREA;
    contact.rcContact = {x - 2, y - 2, x + 3, y + 3};

    const auto down = InjectTouchInput(1, &contact);
    std::printf(
        "InjectTouchInput(DOWN)=%d error=%lu pointerId=%u flags=0x%08X\n",
        down,
        down ? ERROR_SUCCESS : GetLastError(),
        contact.pointerInfo.pointerId,
        contact.pointerInfo.pointerFlags);
    if (!down)
    {
        return;
    }

    Sleep(100);
    contact.pointerInfo.pointerFlags = POINTER_FLAG_UP;
    const auto up = InjectTouchInput(1, &contact);
    std::printf(
        "InjectTouchInput(UP)=%d error=%lu flags=0x%08X\n",
        up,
        up ? ERROR_SUCCESS : GetLastError(),
        contact.pointerInfo.pointerFlags);
}

int main(int argc, char** argv)
{
    const auto previousDpiContext = SetThreadDpiAwarenessContext(
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    std::printf(
        "SetThreadDpiAwarenessContext=%d error=%lu virtual=(%d,%d %dx%d)\n",
        previousDpiContext != nullptr,
        previousDpiContext != nullptr ? ERROR_SUCCESS : GetLastError(),
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));
    std::printf("sizeof(POINT)=%zu\n", sizeof(POINT));
    std::printf("sizeof(RECT)=%zu\n", sizeof(RECT));
    std::printf("sizeof(POINTER_INFO)=%zu\n", sizeof(POINTER_INFO));
    std::printf("sizeof(POINTER_TOUCH_INFO)=%zu\n", sizeof(POINTER_TOUCH_INFO));
    std::printf("offsetof(POINTER_INFO,pointerType)=%zu\n", offsetof(POINTER_INFO, pointerType));
    std::printf("offsetof(POINTER_INFO,pointerId)=%zu\n", offsetof(POINTER_INFO, pointerId));
    std::printf("offsetof(POINTER_INFO,pointerFlags)=%zu\n", offsetof(POINTER_INFO, pointerFlags));
    std::printf("offsetof(POINTER_INFO,sourceDevice)=%zu\n", offsetof(POINTER_INFO, sourceDevice));
    std::printf("offsetof(POINTER_INFO,hwndTarget)=%zu\n", offsetof(POINTER_INFO, hwndTarget));
    std::printf("offsetof(POINTER_INFO,ptPixelLocation)=%zu\n", offsetof(POINTER_INFO, ptPixelLocation));
    std::printf("offsetof(POINTER_INFO,ptPixelLocationRaw)=%zu\n", offsetof(POINTER_INFO, ptPixelLocationRaw));
    std::printf("offsetof(POINTER_INFO,dwTime)=%zu\n", offsetof(POINTER_INFO, dwTime));
    std::printf("offsetof(POINTER_INFO,PerformanceCount)=%zu\n", offsetof(POINTER_INFO, PerformanceCount));
    std::printf("offsetof(POINTER_INFO,ButtonChangeType)=%zu\n", offsetof(POINTER_INFO, ButtonChangeType));
    std::printf("offsetof(POINTER_TOUCH_INFO,touchFlags)=%zu\n", offsetof(POINTER_TOUCH_INFO, touchFlags));
    std::printf("offsetof(POINTER_TOUCH_INFO,touchMask)=%zu\n", offsetof(POINTER_TOUCH_INFO, touchMask));
    std::printf("offsetof(POINTER_TOUCH_INFO,rcContact)=%zu\n", offsetof(POINTER_TOUCH_INFO, rcContact));
    std::printf("offsetof(POINTER_TOUCH_INFO,rcContactRaw)=%zu\n", offsetof(POINTER_TOUCH_INFO, rcContactRaw));
    std::printf("offsetof(POINTER_TOUCH_INFO,orientation)=%zu\n", offsetof(POINTER_TOUCH_INFO, orientation));
    std::printf("offsetof(POINTER_TOUCH_INFO,pressure)=%zu\n", offsetof(POINTER_TOUCH_INFO, pressure));
    std::printf(
        "flags INRANGE=0x%08X INCONTACT=0x%08X FIRSTBUTTON=0x%08X "
        "PRIMARY=0x%08X CONFIDENCE=0x%08X CANCELED=0x%08X "
        "DOWN=0x%08X UPDATE=0x%08X UP=0x%08X\n",
        POINTER_FLAG_INRANGE,
        POINTER_FLAG_INCONTACT,
        POINTER_FLAG_FIRSTBUTTON,
        POINTER_FLAG_PRIMARY,
        POINTER_FLAG_CONFIDENCE,
        POINTER_FLAG_CANCELED,
        POINTER_FLAG_DOWN,
        POINTER_FLAG_UPDATE,
        POINTER_FLAG_UP);
    const auto x = argc >= 3
        ? std::atoi(argv[1])
        : GetSystemMetrics(SM_CXSCREEN) / 2;
    const auto y = argc >= 3
        ? std::atoi(argv[2])
        : GetSystemMetrics(SM_CYSCREEN) / 2;
    run_touch_probe(x, y);
    return 0;
}
