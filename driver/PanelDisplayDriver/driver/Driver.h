/*++

Module Name:

    Driver.h

Abstract:

    Common includes for the PanelDisplayDriver UMDF 2 / IddCx driver, plus the
    declarations of the WDF and IddCx entry points implemented in Driver.cpp,
    Device.cpp and Monitor.cpp.

    Include order matters: <wudfwdm.h> must precede <wdf.h>, and <iddcx.h> must
    follow both. This mirrors the official Microsoft IddSample.

--*/

#pragma once

#define NOMINMAX

#include <windows.h>
#include <bugcodes.h>
#include <wudfwdm.h>
#include <wdf.h>
#include <iddcx.h>

#include <dxgi1_5.h>
#include <d3d11_2.h>
#include <avrt.h>
#include <wrl.h>
// HandleT / Event live here; wrl.h does not reliably pull them in.
#include <wrl/wrappers/corewrappers.h>

#include <memory>
#include <new>

#include "Trace.h"
#include "Public.h"

namespace Microsoft
{
    namespace WRL
    {
        namespace Wrappers
        {
            typedef HandleT<HandleTraits::HANDLENullTraits> Thread;
        }
    }
}

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD  PanelDisplayDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY    PanelDisplayDeviceD0Entry;
EVT_WDF_DEVICE_D0_EXIT     PanelDisplayDeviceD0Exit;

//
// IddCx callbacks. Adapter and control-path callbacks are implemented in
// Device.cpp; monitor and mode callbacks in Monitor.cpp.
//
EVT_IDD_CX_ADAPTER_INIT_FINISHED                 PanelDisplayAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES                  PanelDisplayAdapterCommitModes;
EVT_IDD_CX_DEVICE_IO_CONTROL                     PanelDisplayDeviceIoControl;

EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION             PanelDisplayParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES PanelDisplayMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES            PanelDisplayMonitorQueryTargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN              PanelDisplayMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN            PanelDisplayMonitorUnassignSwapChain;
