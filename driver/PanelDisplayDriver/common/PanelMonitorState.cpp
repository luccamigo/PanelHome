/*++

Module Name:

    PanelMonitorState.cpp

Abstract:

    Implementation of the virtual monitor connect/disconnect state machine.
    See PanelMonitorState.h. No WDF/IddCx/D3D dependencies.

--*/

#include "PanelMonitorState.h"

namespace PanelOS
{
    MonitorStateMachine::MonitorStateMachine()
        : m_DriverState(PanelDriverStateInitializing)
        , m_MonitorState(PanelMonitorStateDisconnected)
        , m_Transition(Transition::None)
        , m_ConnectCount(0)
    {
    }

    void MonitorStateMachine::OnAdapterReady()
    {
        m_DriverState = PanelDriverStateReady;
    }

    void MonitorStateMachine::OnDeviceStopped()
    {
        //
        // The device is going away. IddCx tears the monitor down with the
        // adapter, so drop straight back to Disconnected without pretending a
        // departure was signalled by us.
        //
        m_DriverState = PanelDriverStateStopped;
        m_MonitorState = PanelMonitorStateDisconnected;
        m_Transition = Transition::None;
    }

    bool MonitorStateMachine::BeginConnect(PANEL_COMMAND_RESULT& result)
    {
        if (m_DriverState != PanelDriverStateReady)
        {
            result = PanelCommandResultAdapterNotReady;
            return false;
        }

        //
        // A transition in flight can only be observed by a caller that bypassed
        // the wait lock. Treat it as "the other command wins" rather than
        // starting a second, overlapping transition.
        //
        if (m_Transition != Transition::None)
        {
            result = PanelCommandResultFailed;
            return false;
        }

        if (m_MonitorState == PanelMonitorStateConnected)
        {
            result = PanelCommandResultAlreadyConnected;
            return false;
        }

        m_Transition = Transition::Connecting;
        return true;
    }

    void MonitorStateMachine::CompleteConnect(bool succeeded, PANEL_COMMAND_RESULT& result)
    {
        if (m_Transition != Transition::Connecting)
        {
            result = PanelCommandResultFailed;
            return;
        }

        m_Transition = Transition::None;

        if (succeeded)
        {
            m_MonitorState = PanelMonitorStateConnected;
            ++m_ConnectCount;
            result = PanelCommandResultOk;
        }
        else
        {
            //
            // Creation failed; stay disconnected so a retry is possible.
            //
            m_MonitorState = PanelMonitorStateDisconnected;
            result = PanelCommandResultFailed;
        }
    }

    bool MonitorStateMachine::BeginDisconnect(PANEL_COMMAND_RESULT& result)
    {
        if (m_Transition != Transition::None)
        {
            result = PanelCommandResultFailed;
            return false;
        }

        if (m_MonitorState == PanelMonitorStateDisconnected)
        {
            result = PanelCommandResultAlreadyDisconnected;
            return false;
        }

        m_Transition = Transition::Disconnecting;
        return true;
    }

    void MonitorStateMachine::CompleteDisconnect(bool succeeded, PANEL_COMMAND_RESULT& result)
    {
        if (m_Transition != Transition::Disconnecting)
        {
            result = PanelCommandResultFailed;
            return;
        }

        m_Transition = Transition::None;
        m_MonitorState = PanelMonitorStateDisconnected;
        result = succeeded ? PanelCommandResultOk : PanelCommandResultFailed;

        if (!succeeded)
        {
            // The departure contract does not make the old handle safe to
            // reuse after an error. Block another connect until PnP restarts
            // the device and creates a fresh adapter.
            m_DriverState = PanelDriverStateStopped;
        }
    }
}
