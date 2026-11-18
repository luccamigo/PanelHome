/*++

Module Name:

    DriverClient.cpp

Abstract:

    Device interface discovery and IOCTL plumbing for PanelDisplayCtl.
    See DriverClient.h.

--*/

//
// initguid.h must come before Public.h so this translation unit defines
// GUID_DEVINTERFACE_PANEL_DISPLAY.
//
#include <initguid.h>

#include "DriverClient.h"

#include <cfgmgr32.h>
#include <vector>

namespace PanelOS
{
    namespace
    {
        //
        // Returns the multi-sz list of present interfaces for our GUID.
        //
        // CM_Get_Device_Interface_List_Size / CM_Get_Device_Interface_List is a
        // documented two-call pattern; CR_BUFFER_SMALL means the list grew
        // between the two calls, so it is retried a bounded number of times.
        //
        CONFIGRET GetInterfaceList(std::vector<wchar_t>& list)
        {
            GUID interfaceGuid = GUID_DEVINTERFACE_PANEL_DISPLAY;

            for (int attempt = 0; attempt < 4; ++attempt)
            {
                ULONG length = 0;

                CONFIGRET result = CM_Get_Device_Interface_List_SizeW(
                    &length, &interfaceGuid, nullptr, CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

                if (result != CR_SUCCESS)
                {
                    return result;
                }

                if (length < 2)
                {
                    //
                    // An empty multi-sz: the interface GUID is known but no
                    // device currently exposes it.
                    //
                    return CR_NO_SUCH_VALUE;
                }

                list.assign(length, L'\0');

                result = CM_Get_Device_Interface_ListW(
                    &interfaceGuid, nullptr, list.data(), length,
                    CM_GET_DEVICE_INTERFACE_LIST_PRESENT);

                if (result != CR_BUFFER_SMALL)
                {
                    return result;
                }
            }

            return CR_BUFFER_SMALL;
        }
    }

    DriverClient::DriverClient()
        : m_Device(INVALID_HANDLE_VALUE)
    {
    }

    DriverClient::~DriverClient()
    {
        if (m_Device != INVALID_HANDLE_VALUE)
        {
            CloseHandle(m_Device);
            m_Device = INVALID_HANDLE_VALUE;
        }
    }

    const wchar_t* DriverClient::InterfacePath() const
    {
        return m_InterfacePath.c_str();
    }

    HRESULT DriverClient::Open(_In_ DWORD DesiredAccess)
    {
        m_InterfacePath.clear();

        std::vector<wchar_t> list;

        const CONFIGRET result = GetInterfaceList(list);
        if (result != CR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        //
        // The first entry is the only one we expect: the driver supports a single
        // device.
        //
        const wchar_t* devicePath = list.data();
        if (devicePath[0] == L'\0')
        {
            return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        }

        //
        // Recorded before the open attempt so a failure can name the exact path
        // that was refused.
        //
        m_InterfacePath = devicePath;

        m_Device = CreateFileW(m_InterfacePath.c_str(),
                               DesiredAccess,
                               FILE_SHARE_READ | FILE_SHARE_WRITE,
                               nullptr,
                               OPEN_EXISTING,
                               0,
                               nullptr);

        if (m_Device == INVALID_HANDLE_VALUE)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        return S_OK;
    }

    HRESULT DriverClient::SendIoctl(_In_ DWORD IoControlCode,
                                    _In_reads_bytes_opt_(InputSize) const void* Input, _In_ DWORD InputSize,
                                    _Out_writes_bytes_(OutputSize) void* Output, _In_ DWORD OutputSize)
    {
        if (m_Device == INVALID_HANDLE_VALUE)
        {
            return HRESULT_FROM_WIN32(ERROR_INVALID_HANDLE);
        }

        DWORD bytesReturned = 0;

        const BOOL ok = DeviceIoControl(m_Device,
                                        IoControlCode,
                                        const_cast<void*>(Input), InputSize,
                                        Output, OutputSize,
                                        &bytesReturned,
                                        nullptr);

        if (!ok)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        if (bytesReturned < OutputSize)
        {
            //
            // A short reply means the driver and this tool disagree about the
            // contract; treat it as a hard error rather than reading stale stack.
            //
            return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        }

        return S_OK;
    }

    HRESULT DriverClient::Connect(_In_opt_ const PANEL_CONNECT_INPUT* Input, _Out_ PANEL_COMMAND_OUTPUT& Output)
    {
        Output = {};

        return SendIoctl(IOCTL_PANEL_CONNECT_DISPLAY,
                         Input, Input ? sizeof(PANEL_CONNECT_INPUT) : 0,
                         &Output, sizeof(Output));
    }

    HRESULT DriverClient::Disconnect(_Out_ PANEL_COMMAND_OUTPUT& Output)
    {
        Output = {};

        return SendIoctl(IOCTL_PANEL_DISCONNECT_DISPLAY, nullptr, 0, &Output, sizeof(Output));
    }

    HRESULT DriverClient::GetStatus(_Out_ PANEL_STATUS_OUTPUT& Output)
    {
        Output = {};

        return SendIoctl(IOCTL_PANEL_GET_STATUS, nullptr, 0, &Output, sizeof(Output));
    }
}
