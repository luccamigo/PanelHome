/*++

Module Name:

    PanelDisplayModes.h

Abstract:

    The set of video modes PanelDisplayDriver advertises, plus validation of
    modes supplied from outside the driver.

    This file is deliberately free of WDF, IddCx and D3D so that it can be built
    into the driver, into PanelDisplayCtl and into the unit test host unchanged.

--*/

#pragma once

#include "../driver/Public.h"

namespace PanelOS
{
    //
    // Bounds a mode has to satisfy before the driver will advertise it.
    //
    // These are intentionally conservative for v0.1. The upper bounds exist so a
    // malformed request from user mode cannot make the driver publish a mode the
    // swap chain cannot service; the lower bounds keep Windows from offering a
    // resolution the PanelOS tablet could not usefully render.
    //
    namespace ModeLimits
    {
        constexpr ULONG MinWidth      = 640;
        constexpr ULONG MaxWidth      = 3840;
        constexpr ULONG MinHeight     = 480;
        constexpr ULONG MaxHeight     = 2160;
        constexpr ULONG MinRefreshHz  = 24;
        constexpr ULONG MaxRefreshHz  = 144;

        //
        // Width and height must be even. Every practical video encoder the
        // PanelHost pipeline will use downstream (H.264/HEVC/AV1 with 4:2:0
        // chroma) requires even dimensions, so rejecting odd sizes here avoids
        // publishing a mode we would have to refuse later.
        //
        constexpr ULONG DimensionAlignment = 2;
    }

    //
    // True when Width/Height/RefreshHz are inside ModeLimits and correctly
    // aligned. Rejects the all-zero mode.
    //
    bool IsModeValid(const PANEL_DISPLAY_MODE& mode);

    //
    // Equality by value.
    //
    bool AreModesEqual(const PANEL_DISPLAY_MODE& left, const PANEL_DISPLAY_MODE& right);

    //
    // The built-in mode list for v0.1.
    //
    //   1920x1080 @ 60 Hz   - matches most host desktops 1:1
    //   1920x1200 @ 60 Hz   - high-resolution 16:10 fallback
    //   1280x800  @ 60 Hz   - preferred native tablet/stream mode
    //
    // The preferred mode is the last one, which is also the mode described by the
    // preferred detailed timing in the EDID block (see tools/Generate-Edid.ps1).
    //
    constexpr ULONG DefaultModeCount = 3;
    const PANEL_DISPLAY_MODE* DefaultModes();
    ULONG DefaultPreferredModeIndex();

    //
    // The mode list a connected monitor advertises.
    //
    // Constructed with the built-in defaults. PanelHost will later call
    // SetPreferredMode() with the tablet's real geometry before connecting; that
    // mode is inserted as the preferred entry and the defaults are kept as
    // fallbacks so the user can still pick something if the tablet mode fails.
    //
    // Not thread safe. The driver serialises all access under a single WDF wait
    // lock, which also covers the monitor state machine.
    //
    class ModeTable
    {
    public:
        ModeTable();

        //
        // Restores the built-in default list and preferred index.
        //
        void Reset();

        //
        // Makes 'mode' the preferred mode. Returns false and changes nothing if
        // the mode fails IsModeValid(). If the mode is already present it is
        // simply promoted to preferred rather than duplicated.
        //
        bool SetPreferredMode(const PANEL_DISPLAY_MODE& mode);

        ULONG Count() const { return m_Count; }
        ULONG PreferredIndex() const { return m_PreferredIndex; }
        const PANEL_DISPLAY_MODE& At(ULONG index) const;
        const PANEL_DISPLAY_MODE& Preferred() const { return At(m_PreferredIndex); }

        //
        // True when an exactly matching mode is advertised.
        //
        bool Contains(const PANEL_DISPLAY_MODE& mode) const;

        //
        // True when any advertised mode has this resolution, at any refresh rate.
        // Used when answering EvtIddCxMonitorQueryTargetModes, which asks about a
        // monitor mode and expects the matching target modes back.
        //
        bool ContainsResolution(ULONG width, ULONG height) const;

    private:
        PANEL_DISPLAY_MODE m_Modes[PANEL_MAX_REPORTED_MODES];
        ULONG              m_Count;
        ULONG              m_PreferredIndex;
    };
}
