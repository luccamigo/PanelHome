/*++

Module Name:

    LatestFrameStore.h

Abstract:

    Thread-safe latest-only frame handoff between the swapchain worker and the
    driver's DeviceIoControl callbacks. Publishing never waits for a reader:
    contention drops the candidate frame instead of delaying the compositor.

--*/

#pragma once

#include "../driver/Public.h"

#include <memory>
#include <vector>

namespace PanelOS
{
    // Enough for a padded 3840x2160 BGRA surface (~32 MiB) while bounding a
    // corrupt/anomalous stride well below a process-threatening allocation.
    constexpr ULONG MaxCapturedFramePayloadBytes = 64u * 1024u * 1024u;

    struct LatestFrameSnapshot
    {
        ULONGLONG Generation = 0;
        ULONGLONG Sequence = 0;
        ULONGLONG CaptureTimestampQpc = 0;
        ULONGLONG CaptureTimestampFrequency = 0;
        ULONG Width = 0;
        ULONG Height = 0;
        ULONG Stride = 0;
        PANEL_FRAME_FORMAT Format = PanelFrameFormatUnknown;
        std::vector<BYTE> Pixels;
    };

    class LatestFrameStore
    {
    public:
        LatestFrameStore();

        LatestFrameStore(const LatestFrameStore&) = delete;
        LatestFrameStore& operator=(const LatestFrameStore&) = delete;

        // Starts an empty generation for a newly assigned swapchain.
        ULONGLONG BeginGeneration();

        // Invalidates only the named generation. This prevents teardown of an
        // old processor from clearing a newer swapchain's snapshot.
        void InvalidateGeneration(ULONGLONG Generation);
        void InvalidateAll();

        // Copies a complete CPU snapshot and attempts to publish it. Invalid
        // metadata and lock contention both return false; neither can block the
        // swapchain on a DeviceIoControl reader.
        bool TryPublish(ULONGLONG Generation,
                        ULONGLONG CaptureTimestampQpc,
                        ULONGLONG CaptureTimestampFrequency,
                        ULONG Width,
                        ULONG Height,
                        ULONG Stride,
                        PANEL_FRAME_FORMAT Format,
                        const BYTE* Pixels,
                        size_t PayloadBytes,
                        ULONGLONG* PublishedSequence = nullptr);

        PANEL_FRAME_RESULT GetFrameInfo(ULONGLONG LastSequence,
                                        PANEL_FRAME_OUTPUT& Output) const;

        PANEL_FRAME_RESULT ReadFrame(ULONGLONG Generation,
                                     ULONGLONG Sequence,
                                     PANEL_FRAME_OUTPUT& Output,
                                     std::shared_ptr<const LatestFrameSnapshot>& Snapshot) const;

        ULONGLONG CurrentGeneration() const;
        ULONGLONG DroppedPublications() const;

    private:
        static bool IsPublicationValid(ULONG Width,
                                       ULONG Height,
                                       ULONG Stride,
                                       PANEL_FRAME_FORMAT Format,
                                       const BYTE* Pixels,
                                       size_t PayloadBytes);

        static void FillOutput(const LatestFrameSnapshot* Snapshot,
                               ULONGLONG CurrentGeneration,
                               PANEL_FRAME_RESULT Result,
                               PANEL_FRAME_OUTPUT& Output);

        mutable SRWLOCK m_Lock;
        ULONGLONG m_CurrentGeneration;
        std::shared_ptr<const LatestFrameSnapshot> m_Latest;
        volatile LONG64 m_NextSequence;
        volatile LONG64 m_DroppedPublications;
    };
}
