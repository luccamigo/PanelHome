/*++

Module Name:

    CaptureScheduler.h

Abstract:

    Pure timestamp scheduler used to sample an incoming frame stream at a
    bounded rate without moving the schedule to each accepted arrival.

--*/

#pragma once

#include <cstdint>

namespace PanelOS
{
    struct CaptureScheduleDecision
    {
        bool ShouldCapture;
        std::uint64_t ScheduledDeadline;
        std::uint64_t DeadlineLateness;
        std::uint64_t ElapsedDeadlines;
    };

    class CaptureScheduler
    {
    public:
        CaptureScheduler();
        CaptureScheduler(std::uint64_t TimestampFrequency,
                         std::uint32_t TargetFramesPerSecond);

        void Reset(std::uint64_t TimestampFrequency,
                   std::uint32_t TargetFramesPerSecond);

        // Returns the newest elapsed accumulated deadline. If the producer
        // arrives after multiple deadlines, only one frame is selected and
        // ElapsedDeadlines reports how many target slots passed.
        CaptureScheduleDecision Evaluate(std::uint64_t Timestamp);

        // Compatibility wrapper for callers which need only the decision.
        bool ShouldCapture(std::uint64_t Timestamp);

        bool TryGetNextDeadline(
            std::uint64_t& NextDeadline) const;

    private:
        void AdvanceDeadline();
        void AdvanceDeadlines(std::uint64_t Count);

        std::uint64_t m_TimestampFrequency;
        std::uint64_t m_WholePeriod;
        std::uint64_t m_NextDeadline;
        std::uint32_t m_TargetFramesPerSecond;
        std::uint64_t m_PeriodRemainder;
        std::uint64_t m_AccumulatedRemainder;
        bool m_Initialized;
    };
}
