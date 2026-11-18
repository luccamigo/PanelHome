/*++

Module Name:

    PanelFrameDiagnostics.h

Abstract:

    Small, stride-aware helpers used to diagnose the first captured frames of a
    swapchain. The helpers are independent of D3D/WDF so their byte-order,
    padding and colour behaviour can be covered by the native unit tests.

--*/

#pragma once

#include "../driver/Public.h"

#include <stddef.h>

namespace PanelOS
{
    struct FramePixelSample
    {
        BYTE Blue = 0;
        BYTE Green = 0;
        BYTE Red = 0;
        BYTE Alpha = 0;
    };

    struct FrameLuminanceSummary
    {
        ULONG Minimum = 0;
        ULONG Maximum = 0;
        ULONG Average = 0;
        ULONG NonBlackSamples = 0;
        ULONG SampleCount = 0;
    };

    struct FrameDiagnosticSummary
    {
        ULONG Crc32 = 0;
        FramePixelSample TopLeft;
        FramePixelSample TopRight;
        FramePixelSample Center;
        FramePixelSample BottomLeft;
        FramePixelSample BottomRight;
        FrameLuminanceSummary Luminance;
    };

    // Standard IEEE CRC-32 over the exact byte payload, including row padding.
    // This lets Driver and PanelHost prove that the IOCTL handoff is lossless.
    ULONG ComputeFrameCrc32(const BYTE* Bytes, size_t ByteCount);

    // Pixel samples and the 16x16 luminance grid honour Stride, so padding is
    // never mistaken for visible pixels. Samples are normalised to B/G/R/A
    // channel fields for both supported source byte orders.
    bool TryAnalyzeFrame(ULONG Width,
                         ULONG Height,
                         ULONG Stride,
                         PANEL_FRAME_FORMAT Format,
                         const BYTE* Pixels,
                         size_t PayloadBytes,
                         FrameDiagnosticSummary& Summary);
}
