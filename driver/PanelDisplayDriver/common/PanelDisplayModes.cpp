/*++

Module Name:

    PanelDisplayModes.cpp

Abstract:

    Implementation of the advertised mode list and mode validation.
    See PanelDisplayModes.h. No WDF/IddCx/D3D dependencies.

--*/

#include "PanelDisplayModes.h"

namespace PanelOS
{
    namespace
    {
        //
        // Ordered least to most preferred; the last entry is the preferred mode.
        //
        const PANEL_DISPLAY_MODE g_DefaultModes[DefaultModeCount] = {
            { 1920, 1080, 60 },
            { 1920, 1200, 60 },
            { 1280,  800, 60 },
        };
    }

    bool IsModeValid(const PANEL_DISPLAY_MODE& mode)
    {
        if (mode.Width < ModeLimits::MinWidth || mode.Width > ModeLimits::MaxWidth)
        {
            return false;
        }

        if (mode.Height < ModeLimits::MinHeight || mode.Height > ModeLimits::MaxHeight)
        {
            return false;
        }

        if (mode.RefreshHz < ModeLimits::MinRefreshHz || mode.RefreshHz > ModeLimits::MaxRefreshHz)
        {
            return false;
        }

        if ((mode.Width % ModeLimits::DimensionAlignment) != 0 ||
            (mode.Height % ModeLimits::DimensionAlignment) != 0)
        {
            return false;
        }

        return true;
    }

    bool AreModesEqual(const PANEL_DISPLAY_MODE& left, const PANEL_DISPLAY_MODE& right)
    {
        return left.Width == right.Width &&
               left.Height == right.Height &&
               left.RefreshHz == right.RefreshHz;
    }

    const PANEL_DISPLAY_MODE* DefaultModes()
    {
        return g_DefaultModes;
    }

    ULONG DefaultPreferredModeIndex()
    {
        return DefaultModeCount - 1;
    }

    ModeTable::ModeTable()
    {
        Reset();
    }

    void ModeTable::Reset()
    {
        for (ULONG i = 0; i < DefaultModeCount; ++i)
        {
            m_Modes[i] = g_DefaultModes[i];
        }

        for (ULONG i = DefaultModeCount; i < PANEL_MAX_REPORTED_MODES; ++i)
        {
            m_Modes[i].Width = 0;
            m_Modes[i].Height = 0;
            m_Modes[i].RefreshHz = 0;
        }

        m_Count = DefaultModeCount;
        m_PreferredIndex = DefaultPreferredModeIndex();
    }

    bool ModeTable::SetPreferredMode(const PANEL_DISPLAY_MODE& mode)
    {
        if (!IsModeValid(mode))
        {
            return false;
        }

        for (ULONG i = 0; i < m_Count; ++i)
        {
            if (AreModesEqual(m_Modes[i], mode))
            {
                m_PreferredIndex = i;
                return true;
            }
        }

        if (m_Count < PANEL_MAX_REPORTED_MODES)
        {
            m_Modes[m_Count] = mode;
            m_PreferredIndex = m_Count;
            ++m_Count;
            return true;
        }

        //
        // Table is full. Overwrite the current preferred entry rather than
        // silently ignoring the request, so the host-supplied mode always wins.
        //
        m_Modes[m_PreferredIndex] = mode;
        return true;
    }

    const PANEL_DISPLAY_MODE& ModeTable::At(ULONG index) const
    {
        //
        // Callers are internal and always bounded by Count(); clamping keeps a
        // programming error from reading past the array in a driver process.
        //
        if (index >= m_Count)
        {
            index = m_Count - 1;
        }

        return m_Modes[index];
    }

    bool ModeTable::Contains(const PANEL_DISPLAY_MODE& mode) const
    {
        for (ULONG i = 0; i < m_Count; ++i)
        {
            if (AreModesEqual(m_Modes[i], mode))
            {
                return true;
            }
        }

        return false;
    }

    bool ModeTable::ContainsResolution(ULONG width, ULONG height) const
    {
        for (ULONG i = 0; i < m_Count; ++i)
        {
            if (m_Modes[i].Width == width && m_Modes[i].Height == height)
            {
                return true;
            }
        }

        return false;
    }
}
