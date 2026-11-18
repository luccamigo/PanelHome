/*++

Module Name:

    LatestFrameStore.cpp

Abstract:

    Latest-only frame store implementation. Lock hold times cover pointer swaps
    and snapshot acquisition only; allocation and pixel copies happen outside.

--*/

#include "LatestFrameStore.h"

#include <limits>

namespace PanelOS
{
    LatestFrameStore::LatestFrameStore()
        : m_Lock(SRWLOCK_INIT)
        , m_CurrentGeneration(0)
        , m_NextSequence(0)
        , m_DroppedPublications(0)
    {
    }

    ULONGLONG LatestFrameStore::BeginGeneration()
    {
        AcquireSRWLockExclusive(&m_Lock);

        ++m_CurrentGeneration;
        if (m_CurrentGeneration == 0)
        {
            ++m_CurrentGeneration;
        }

        m_Latest.reset();
        const ULONGLONG generation = m_CurrentGeneration;

        ReleaseSRWLockExclusive(&m_Lock);
        return generation;
    }

    void LatestFrameStore::InvalidateGeneration(ULONGLONG Generation)
    {
        AcquireSRWLockExclusive(&m_Lock);

        if (Generation != 0 && m_CurrentGeneration == Generation)
        {
            ++m_CurrentGeneration;
            if (m_CurrentGeneration == 0)
            {
                ++m_CurrentGeneration;
            }
            m_Latest.reset();
        }

        ReleaseSRWLockExclusive(&m_Lock);
    }

    void LatestFrameStore::InvalidateAll()
    {
        AcquireSRWLockExclusive(&m_Lock);

        ++m_CurrentGeneration;
        if (m_CurrentGeneration == 0)
        {
            ++m_CurrentGeneration;
        }
        m_Latest.reset();

        ReleaseSRWLockExclusive(&m_Lock);
    }

    bool LatestFrameStore::IsPublicationValid(ULONG Width,
                                               ULONG Height,
                                               ULONG Stride,
                                               PANEL_FRAME_FORMAT Format,
                                               const BYTE* Pixels,
                                               size_t PayloadBytes)
    {
        if (Width == 0 || Height == 0 || Pixels == nullptr || PayloadBytes == 0)
        {
            return false;
        }

        if (Format != PanelFrameFormatBgra8888 && Format != PanelFrameFormatRgba8888)
        {
            return false;
        }

        const ULONGLONG minimumStride = static_cast<ULONGLONG>(Width) * 4u;
        const ULONGLONG expectedPayload = static_cast<ULONGLONG>(Stride) * Height;

        if (Stride < minimumStride ||
            expectedPayload != PayloadBytes ||
            expectedPayload > MaxCapturedFramePayloadBytes ||
            expectedPayload > (std::numeric_limits<ULONG>::max)())
        {
            return false;
        }

        return true;
    }

    bool LatestFrameStore::TryPublish(ULONGLONG Generation,
                                      ULONGLONG CaptureTimestampQpc,
                                      ULONGLONG CaptureTimestampFrequency,
                                      ULONG Width,
                                      ULONG Height,
                                      ULONG Stride,
                                      PANEL_FRAME_FORMAT Format,
                                      const BYTE* Pixels,
                                      size_t PayloadBytes,
                                      ULONGLONG* PublishedSequence)
    {
        if (PublishedSequence != nullptr)
        {
            *PublishedSequence = 0;
        }

        if (Generation == 0 || CaptureTimestampFrequency == 0 ||
            !IsPublicationValid(Width, Height, Stride, Format, Pixels, PayloadBytes))
        {
            InterlockedIncrement64(&m_DroppedPublications);
            return false;
        }

        std::shared_ptr<LatestFrameSnapshot> candidate;

        try
        {
            candidate = std::make_shared<LatestFrameSnapshot>();
            candidate->Generation = Generation;
            candidate->Sequence = static_cast<ULONGLONG>(InterlockedIncrement64(&m_NextSequence));
            candidate->CaptureTimestampQpc = CaptureTimestampQpc;
            candidate->CaptureTimestampFrequency = CaptureTimestampFrequency;
            candidate->Width = Width;
            candidate->Height = Height;
            candidate->Stride = Stride;
            candidate->Format = Format;
            candidate->Pixels.assign(Pixels, Pixels + PayloadBytes);
        }
        catch (...)
        {
            InterlockedIncrement64(&m_DroppedPublications);
            return false;
        }

        if (!TryAcquireSRWLockExclusive(&m_Lock))
        {
            InterlockedIncrement64(&m_DroppedPublications);
            return false;
        }

        bool published = false;
        if (m_CurrentGeneration == Generation)
        {
            if (PublishedSequence != nullptr)
            {
                *PublishedSequence = candidate->Sequence;
            }
            m_Latest = std::move(candidate);
            published = true;
        }

        ReleaseSRWLockExclusive(&m_Lock);

        if (!published)
        {
            InterlockedIncrement64(&m_DroppedPublications);
        }

        return published;
    }

    void LatestFrameStore::FillOutput(const LatestFrameSnapshot* Snapshot,
                                      ULONGLONG CurrentGeneration,
                                      PANEL_FRAME_RESULT Result,
                                      PANEL_FRAME_OUTPUT& Output)
    {
        Output = {};
        Output.Size = sizeof(Output);
        Output.Result = Result;
        Output.Generation = CurrentGeneration;

        if (Snapshot == nullptr)
        {
            return;
        }

        Output.Generation = Snapshot->Generation;
        Output.Sequence = Snapshot->Sequence;
        Output.CaptureTimestampQpc = Snapshot->CaptureTimestampQpc;
        Output.CaptureTimestampFrequency = Snapshot->CaptureTimestampFrequency;
        Output.Width = Snapshot->Width;
        Output.Height = Snapshot->Height;
        Output.Stride = Snapshot->Stride;
        Output.Format = Snapshot->Format;
        Output.PayloadBytes = static_cast<ULONG>(Snapshot->Pixels.size());
    }

    PANEL_FRAME_RESULT LatestFrameStore::GetFrameInfo(ULONGLONG LastSequence,
                                                       PANEL_FRAME_OUTPUT& Output) const
    {
        AcquireSRWLockShared(&m_Lock);
        const auto snapshot = m_Latest;
        const ULONGLONG generation = m_CurrentGeneration;
        ReleaseSRWLockShared(&m_Lock);

        PANEL_FRAME_RESULT result = PanelFrameResultOk;
        if (!snapshot)
        {
            result = PanelFrameResultNoFrame;
        }
        else if (snapshot->Sequence <= LastSequence)
        {
            result = PanelFrameResultNoNewFrame;
        }

        FillOutput(snapshot.get(), generation, result, Output);
        return result;
    }

    PANEL_FRAME_RESULT LatestFrameStore::ReadFrame(
        ULONGLONG Generation,
        ULONGLONG Sequence,
        PANEL_FRAME_OUTPUT& Output,
        std::shared_ptr<const LatestFrameSnapshot>& Snapshot) const
    {
        AcquireSRWLockShared(&m_Lock);
        const auto latest = m_Latest;
        const ULONGLONG currentGeneration = m_CurrentGeneration;
        ReleaseSRWLockShared(&m_Lock);

        PANEL_FRAME_RESULT result = PanelFrameResultOk;
        if (!latest)
        {
            result = PanelFrameResultNoFrame;
        }
        else if (latest->Generation != Generation || latest->Sequence != Sequence)
        {
            result = PanelFrameResultFrameReplaced;
        }

        FillOutput(latest.get(), currentGeneration, result, Output);
        Snapshot = result == PanelFrameResultOk ? latest : nullptr;
        return result;
    }

    ULONGLONG LatestFrameStore::CurrentGeneration() const
    {
        AcquireSRWLockShared(&m_Lock);
        const ULONGLONG generation = m_CurrentGeneration;
        ReleaseSRWLockShared(&m_Lock);
        return generation;
    }

    ULONGLONG LatestFrameStore::DroppedPublications() const
    {
        return static_cast<ULONGLONG>(InterlockedCompareExchange64(
            const_cast<volatile LONG64*>(&m_DroppedPublications), 0, 0));
    }
}
