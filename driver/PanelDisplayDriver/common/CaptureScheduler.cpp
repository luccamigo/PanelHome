/*++

Module Name:

    CaptureScheduler.cpp

Abstract:

    Accumulated-deadline capture scheduler implementation.

--*/

#include "CaptureScheduler.h"

#include <limits>

namespace PanelOS
{
    CaptureScheduler::CaptureScheduler()
        : m_TimestampFrequency(0)
        , m_WholePeriod(0)
        , m_NextDeadline(0)
        , m_TargetFramesPerSecond(0)
        , m_PeriodRemainder(0)
        , m_AccumulatedRemainder(0)
        , m_Initialized(false)
    {
    }

    CaptureScheduler::CaptureScheduler(
        std::uint64_t TimestampFrequency,
        std::uint32_t TargetFramesPerSecond)
        : CaptureScheduler()
    {
        Reset(TimestampFrequency, TargetFramesPerSecond);
    }

    void CaptureScheduler::Reset(
        std::uint64_t TimestampFrequency,
        std::uint32_t TargetFramesPerSecond)
    {
        const auto maximum = (std::numeric_limits<std::uint64_t>::max)();
        if (TargetFramesPerSecond == 0 ||
            TimestampFrequency < TargetFramesPerSecond ||
            TimestampFrequency > maximum / TargetFramesPerSecond)
        {
            TimestampFrequency = 0;
            TargetFramesPerSecond = 0;
        }

        m_TimestampFrequency = TimestampFrequency;
        m_TargetFramesPerSecond = TargetFramesPerSecond;
        m_WholePeriod = TargetFramesPerSecond == 0
            ? 0
            : TimestampFrequency / TargetFramesPerSecond;
        m_PeriodRemainder = TargetFramesPerSecond == 0
            ? 0
            : TimestampFrequency % TargetFramesPerSecond;
        m_NextDeadline = 0;
        m_AccumulatedRemainder = 0;
        m_Initialized = false;
    }

    CaptureScheduleDecision CaptureScheduler::Evaluate(std::uint64_t Timestamp)
    {
        CaptureScheduleDecision decision = {};
        if (m_TimestampFrequency == 0 ||
            m_TargetFramesPerSecond == 0 ||
            m_WholePeriod == 0)
        {
            return decision;
        }

        if (!m_Initialized)
        {
            m_Initialized = true;
            m_NextDeadline = Timestamp;
            decision.ShouldCapture = true;
            decision.ScheduledDeadline = Timestamp;
            decision.ElapsedDeadlines = 1;
            AdvanceDeadline();
            return decision;
        }

        if (Timestamp < m_NextDeadline)
        {
            return decision;
        }

        // Jump over every missed period in one calculation. This preserves the
        // accumulated phase after a long pause, while returning true only once
        // for the newest available frame (there is no catch-up burst).
        const std::uint64_t overdue = Timestamp - m_NextDeadline;
        std::uint64_t periods =
            (overdue / m_TimestampFrequency) * m_TargetFramesPerSecond;
        periods +=
            ((overdue % m_TimestampFrequency) * m_TargetFramesPerSecond) /
            m_TimestampFrequency;
        ++periods;
        const CaptureScheduler original = *this;
        AdvanceDeadlines(periods);
        while (m_TimestampFrequency != 0 && m_NextDeadline <= Timestamp)
        {
            AdvanceDeadline();
            ++periods;
        }

        CaptureScheduler selectedDeadline = original;
        selectedDeadline.AdvanceDeadlines(periods - 1u);
        if (m_TimestampFrequency == 0 ||
            selectedDeadline.m_TimestampFrequency == 0 ||
            selectedDeadline.m_NextDeadline > Timestamp)
        {
            return decision;
        }

        decision.ShouldCapture = true;
        decision.ScheduledDeadline = selectedDeadline.m_NextDeadline;
        decision.DeadlineLateness =
            Timestamp - decision.ScheduledDeadline;
        decision.ElapsedDeadlines = periods;
        return decision;
    }

    bool CaptureScheduler::ShouldCapture(std::uint64_t Timestamp)
    {
        return Evaluate(Timestamp).ShouldCapture;
    }

    bool CaptureScheduler::TryGetNextDeadline(
        std::uint64_t& NextDeadline) const
    {
        NextDeadline = 0;
        if (!m_Initialized || m_TimestampFrequency == 0)
        {
            return false;
        }

        NextDeadline = m_NextDeadline;
        return true;
    }

    void CaptureScheduler::AdvanceDeadline()
    {
        AdvanceDeadlines(1u);
    }

    void CaptureScheduler::AdvanceDeadlines(std::uint64_t Count)
    {
        const auto maximum = (std::numeric_limits<std::uint64_t>::max)();
        if (Count == 0 || m_TimestampFrequency == 0)
        {
            return;
        }

        if (Count > maximum / m_WholePeriod)
        {
            m_TimestampFrequency = 0;
            m_NextDeadline = maximum;
            return;
        }

        const std::uint64_t wholeDelta = Count * m_WholePeriod;
        const std::uint64_t completeGroups =
            Count / m_TargetFramesPerSecond;
        const std::uint64_t remainingPeriods =
            Count % m_TargetFramesPerSecond;
        const std::uint64_t fractionalTotal =
            m_AccumulatedRemainder + remainingPeriods * m_PeriodRemainder;
        const std::uint64_t remainderDelta =
            completeGroups * m_PeriodRemainder +
            fractionalTotal / m_TargetFramesPerSecond;

        if (wholeDelta > maximum - remainderDelta ||
            m_NextDeadline > maximum - (wholeDelta + remainderDelta))
        {
            // QueryPerformanceCounter cannot reach this during a process
            // lifetime, but fail closed if a synthetic clock wraps.
            m_TimestampFrequency = 0;
            m_NextDeadline = maximum;
            return;
        }

        m_NextDeadline += wholeDelta + remainderDelta;
        m_AccumulatedRemainder =
            fractionalTotal % m_TargetFramesPerSecond;
    }
}
