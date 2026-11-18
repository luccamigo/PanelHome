/*++

Module Name:

    PanelMonitorState.h

Abstract:

    The CONNECT/DISCONNECT state machine for the single PanelOS virtual monitor.

    The point of this class is that it decides *what should happen* without
    touching IddCx. Device.cpp asks it whether a monitor must actually be created
    or destroyed, performs the IddCx work, then reports the outcome back. That
    split is what makes the connect/disconnect rules - including idempotency and
    the "never more than one monitor" rule - unit-testable without a driver.

    Not thread safe by design: the driver serialises every command under one WDF
    wait lock, which covers this object and the ModeTable together.

--*/

#pragma once

#include "../driver/Public.h"

namespace PanelOS
{
    class MonitorStateMachine
    {
    public:
        MonitorStateMachine();

        //
        // Driver lifecycle. The adapter is created in D0Entry but IddCx finishes
        // initialising it asynchronously; until EvtIddCxAdapterInitFinished fires
        // we must refuse to create a monitor.
        //
        void OnAdapterReady();
        void OnDeviceStopped();

        PANEL_DRIVER_STATE  DriverState() const  { return m_DriverState; }
        PANEL_MONITOR_STATE MonitorState() const { return m_MonitorState; }
        ULONG               ConnectCount() const { return m_ConnectCount; }

        //
        // Decide whether IOCTL_PANEL_CONNECT_DISPLAY should create a monitor.
        //
        // Returns true when the caller must go create the IDDCX_MONITOR; in that
        // case the caller MUST later call CompleteConnect(). Returns false when
        // there is nothing to do, and 'result' explains why:
        //
        //   AdapterNotReady    - adapter has not finished initialising
        //   AlreadyConnected   - a monitor already exists (idempotent success)
        //
        bool BeginConnect(PANEL_COMMAND_RESULT& result);

        //
        // Report the outcome of the IddCx monitor creation started by a
        // BeginConnect() that returned true.
        //
        void CompleteConnect(bool succeeded, PANEL_COMMAND_RESULT& result);

        //
        // Decide whether IOCTL_PANEL_DISCONNECT_DISPLAY should tear a monitor
        // down. Returns true when the caller must signal departure and release
        // resources, and the caller MUST later call CompleteDisconnect().
        // Returns false with result = AlreadyDisconnected otherwise.
        //
        bool BeginDisconnect(PANEL_COMMAND_RESULT& result);

        //
        // Report completion of the teardown started by BeginDisconnect(). The
        // handle is never reused after a departure attempt, so the logical state
        // becomes Disconnected even when IddCx reports an error; result preserves
        // that error and the driver becomes Stopped so a second monitor cannot
        // be created until PnP supplies a fresh adapter.
        //
        void CompleteDisconnect(bool succeeded, PANEL_COMMAND_RESULT& result);

        //
        // True while a connect or disconnect is in flight. Used only by tests and
        // diagnostics; the wait lock means user mode never observes this state.
        //
        bool IsTransitioning() const { return m_Transition != Transition::None; }

    private:
        enum class Transition
        {
            None,
            Connecting,
            Disconnecting,
        };

        PANEL_DRIVER_STATE  m_DriverState;
        PANEL_MONITOR_STATE m_MonitorState;
        Transition          m_Transition;
        ULONG               m_ConnectCount;
    };
}
