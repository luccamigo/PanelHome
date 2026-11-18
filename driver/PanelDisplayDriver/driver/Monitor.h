/*++

Module Name:

    Monitor.h

Abstract:

    PanelMonitorContext - the per-monitor state of the single PanelOS virtual
    monitor, plus the helpers that translate between PANEL_DISPLAY_MODE and the
    DISPLAYCONFIG structures IddCx works in.

    The mode table here is a snapshot taken when the monitor was created. Making
    it immutable for the monitor's lifetime is what lets the IddCx mode callbacks
    run without taking the device wait lock - see the locking note in Device.h.

--*/

#pragma once

#include "Driver.h"
#include "../common/PanelDisplayModes.h"
#include "../common/LatestFrameStore.h"

class SwapChainProcessor;

//
// Fills a DISPLAYCONFIG_VIDEO_SIGNAL_INFO for a simple progressive mode.
//
// bIsMonitorMode selects the vSyncFreqDivider convention IddCx expects: monitor
// modes use 0, target modes use 1. This mirrors the official IddSample; see
// "CCD data structures" in the Microsoft display driver documentation.
//
void PanelFillSignalInfo(_Out_ DISPLAYCONFIG_VIDEO_SIGNAL_INFO& SignalInfo,
                         _In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync,
                         _In_ bool bIsMonitorMode);

IDDCX_MONITOR_MODE PanelCreateMonitorMode(_In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync,
                                          _In_ IDDCX_MONITOR_MODE_ORIGIN Origin);

IDDCX_TARGET_MODE PanelCreateTargetMode(_In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync);

//
// Extracts width/height/refresh from a committed signal info so it can be
// reported through IOCTL_PANEL_GET_STATUS.
//
PANEL_DISPLAY_MODE PanelSignalInfoToMode(_In_ const DISPLAYCONFIG_VIDEO_SIGNAL_INFO& SignalInfo);

class PanelMonitorContext
{
public:
    PanelMonitorContext(_In_ IDDCX_MONITOR MonitorObject,
                        _In_ const PanelOS::ModeTable& Modes,
                        _In_ PanelOS::LatestFrameStore* FrameStore,
                        _In_ ULONG TargetFramesPerSecond);
    ~PanelMonitorContext();

    //
    // EvtIddCxMonitorAssignSwapChain / UnassignSwapChain.
    //
    NTSTATUS AssignSwapChain(_In_ IDDCX_SWAPCHAIN SwapChain,
                             _In_ LUID RenderAdapter,
                             _In_ HANDLE NewFrameEvent);
    void UnassignSwapChain();

    // Permanently closes swapchain assignment for this monitor and joins any
    // current processor before IddCxMonitorDeparture is called.
    void BeginDeparture();

    //
    // Recorded from EvtIddCxAdapterCommitModes so status can report what Windows
    // actually activated.
    //
    void SetActiveMode(_In_ const PANEL_DISPLAY_MODE& Mode, _In_ bool Active);
    PANEL_DISPLAY_MODE ActiveMode() const;

    //
    // Cumulative frames handed to the driver by the current swap chain.
    //
    ULONGLONG FramesProcessed() const;
    void SetCaptureTargetFps(_In_ ULONG TargetFramesPerSecond);
    void GetCaptureMetrics(_Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output) const;
    void GetCapturePacingMetrics(
        _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output) const;

    const PanelOS::ModeTable& Modes() const { return m_Modes; }
    IDDCX_MONITOR Monitor() const { return m_Monitor; }

private:
    void FinishSwapChainAssignment();

    IDDCX_MONITOR                       m_Monitor;
    PanelOS::ModeTable                  m_Modes;
    PanelOS::LatestFrameStore*          m_FrameStore;

    //
    // The swap chain can be torn down from two directions: IddCx calling
    // EvtIddCxMonitorUnassignSwapChain, and the disconnect IOCTL stopping
    // processing before it reports the monitor's departure. Those are different
    // threads, so the pointer needs its own lock - std::unique_ptr::reset() is
    // not safe to race.
    //
    // Kept separate from m_ModeLock on purpose: teardown joins the processing
    // thread while holding ownership, and a status query reading the active mode
    // should not have to wait behind that.
    //
    mutable CRITICAL_SECTION            m_SwapChainLock;
    CONDITION_VARIABLE                  m_SwapChainCondition;
    std::unique_ptr<SwapChainProcessor> m_SwapChain;
    bool                                m_Departing;
    ULONG                               m_AssignmentsInFlight;
    volatile LONG                       m_TargetFramesPerSecond;

    //
    // Written by CommitModes, read by the status IOCTL on another thread. The
    // three fields are updated together under this critical section rather than
    // interlocked individually so a reader never sees a torn mode.
    //
    mutable CRITICAL_SECTION m_ModeLock;
    PANEL_DISPLAY_MODE       m_ActiveMode;
};

struct PanelMonitorContextWrapper
{
    PanelMonitorContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(PanelMonitorContextWrapper, WdfObjectGet_PanelMonitorContextWrapper);
