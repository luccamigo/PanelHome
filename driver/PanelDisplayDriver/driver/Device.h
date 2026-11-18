/*++

Module Name:

    Device.h

Abstract:

    PanelDeviceContext - the per-device state of PanelDisplayDriver.

    It owns the IddCx adapter, the connect/disconnect state machine, the mode
    table that the next connection will use, and the handle to the single virtual
    monitor (when one exists).

    Locking
    -------
    m_Lock (a WDFWAITLOCK) serialises the three control commands against each
    other and against status reads. It is deliberately NOT held across IddCx
    calls such as IddCxMonitorArrival or IddCxMonitorDeparture: those can call
    back into the driver synchronously (QueryTargetModes, UnassignSwapChain,
    CommitModes), and re-entering the same wait lock would deadlock.

    What makes that safe is the transition flag inside MonitorStateMachine: once
    BeginConnect/BeginDisconnect has returned true, no other command can start a
    transition, so the thread performing the transition is the only writer of
    m_Monitor / m_MonitorContext. Readers still take the lock, and the pointers
    are cleared *before* the monitor object is destroyed, so a status read can
    never observe a dangling context.

--*/

#pragma once

#include "Driver.h"
#include "../common/PanelDisplayModes.h"
#include "../common/PanelMonitorState.h"
#include "../common/LatestFrameStore.h"

class PanelMonitorContext;

class PanelDeviceContext
{
public:
    explicit PanelDeviceContext(_In_ WDFDEVICE WdfDevice);
    ~PanelDeviceContext();

    //
    // Creates the wait lock. Called from PanelDisplayDeviceAdd, after the
    // WDFDEVICE exists.
    //
    NTSTATUS Initialize();

    //
    // Creates the IddCx adapter. Called from D0Entry.
    //
    // Note: unlike the Microsoft IddSample, this does NOT go on to create a
    // monitor. The driver stays loaded with zero virtual monitors until user
    // mode sends IOCTL_PANEL_CONNECT_DISPLAY.
    //
    NTSTATUS InitAdapter();

    //
    // EvtIddCxAdapterInitFinished. Marks the driver Ready so connects are
    // allowed, or leaves it Initializing on failure.
    //
    void OnAdapterInitFinished(_In_ IDDCX_ADAPTER AdapterObject, _In_ NTSTATUS Status);

    //
    // D0Exit / device teardown. Drops any live monitor and stops accepting
    // connects.
    //
    void OnDeviceStopped();

    //
    // Control commands. Each acquires m_Lock internally; see the locking note
    // above for what happens around the IddCx calls.
    //
    void HandleConnect(_In_opt_ const PANEL_CONNECT_INPUT* Input, _Out_ PANEL_COMMAND_OUTPUT& Output);
    void HandleDisconnect(_Out_ PANEL_COMMAND_OUTPUT& Output);
    void HandleGetStatus(_Out_ PANEL_STATUS_OUTPUT& Output);
    void HandleGetFrameInfo(_In_ const PANEL_FRAME_QUERY_INPUT& Input,
                            _Out_ PANEL_FRAME_OUTPUT& Output);
    PANEL_FRAME_RESULT HandleReadLatestFrame(
        _In_ const PANEL_FRAME_READ_INPUT& Input,
        _Out_ PANEL_FRAME_OUTPUT& Output,
        _Out_ std::shared_ptr<const PanelOS::LatestFrameSnapshot>& Snapshot);
    void HandleGetCaptureMetrics(_Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output);
    void HandleGetCapturePacingMetrics(
        _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output);
    bool HandleSetCaptureConfig(
        _In_ const PANEL_CAPTURE_CONFIG_INPUT& Input,
        _Out_ PANEL_CAPTURE_CONFIG_OUTPUT& Output);

    IDDCX_ADAPTER Adapter() const { return m_Adapter; }
    WDFDEVICE     WdfDevice() const { return m_WdfDevice; }

private:
    //
    // Performs IddCxMonitorCreate + IddCxMonitorArrival using a snapshot of the
    // mode table. Called without m_Lock held, during a Connecting transition.
    //
    NTSTATUS CreateAndAnnounceMonitor(_In_ const PanelOS::ModeTable& Modes,
                                      _In_ ULONG TargetFramesPerSecond,
                                      _Out_ IDDCX_MONITOR& MonitorObject,
                                      _Out_ PanelMonitorContext*& MonitorContext);

    //
    // Stops the current swapchain and calls IddCxMonitorDeparture. A successful
    // departure destroys the IDDCX_MONITOR; the handle must never be deleted or
    // reused by the driver after that call. Called without m_Lock held, during
    // a Disconnecting transition.
    //
    NTSTATUS RemoveMonitor(_In_ IDDCX_MONITOR MonitorObject);

    WDFDEVICE     m_WdfDevice;
    IDDCX_ADAPTER m_Adapter;
    WDFWAITLOCK   m_Lock;

    //
    // Guarded by m_Lock (see locking note for the transition-writer exception).
    //
    PanelOS::MonitorStateMachine m_State;
    PanelOS::ModeTable           m_Modes;
    PanelOS::LatestFrameStore    m_FrameStore;
    ULONG                        m_CaptureTargetFramesPerSecond;
    IDDCX_MONITOR                m_Monitor;
    PanelMonitorContext*         m_MonitorContext;
};

//
// WDF context wrapper. The same wrapper type is attached to the WDFDEVICE and to
// the IDDCX_ADAPTER, both pointing at the one PanelDeviceContext; only the
// WDFDEVICE owns it, which is why Cleanup() is driven from the device's cleanup
// callback alone.
//
struct PanelDeviceContextWrapper
{
    PanelDeviceContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(PanelDeviceContextWrapper, WdfObjectGet_PanelDeviceContextWrapper);
