/*++

Module Name:

    DriverClient.h

Abstract:

    Thin wrapper over the PanelDisplayDriver control interface.

    This is the piece PanelHost will reuse: it locates the device by interface
    GUID and issues the three control IOCTLs. Nothing above it needs to know how
    the driver is reached.

--*/

#pragma once

#include <windows.h>
#include <winioctl.h>

#include <string>

#include "../../driver/Public.h"

namespace PanelOS
{
    class DriverClient
    {
    public:
        DriverClient();
        ~DriverClient();

        DriverClient(const DriverClient&) = delete;
        DriverClient& operator=(const DriverClient&) = delete;

        //
        // Access needed for each command, derived from the access bits of the
        // IOCTL it issues (see Public.h). Opening with only what the command
        // needs means a status query cannot be escalated into a connect.
        //
        static constexpr DWORD AccessForStatus = GENERIC_READ;   // FILE_READ_ACCESS
        static constexpr DWORD AccessForControl = GENERIC_WRITE; // FILE_WRITE_ACCESS

        //
        // Locates the single PanelDisplayDriver device interface and opens it
        // with exactly DesiredAccess.
        //
        // Returns:
        //   S_OK                                  device opened
        //   HRESULT_FROM_WIN32(ERROR_NOT_FOUND)   driver not installed, or the
        //                                         device node does not exist
        //   otherwise                             the Win32 error from CreateFile
        //
        HRESULT Open(_In_ DWORD DesiredAccess);

        //
        // The interface path the last Open() attempt used, or an empty string if
        // no interface was found. Populated even when the open itself failed, so
        // a failure can be reported against a concrete path.
        //
        const wchar_t* InterfacePath() const;

        HRESULT Connect(_In_opt_ const PANEL_CONNECT_INPUT* Input, _Out_ PANEL_COMMAND_OUTPUT& Output);
        HRESULT Disconnect(_Out_ PANEL_COMMAND_OUTPUT& Output);
        HRESULT GetStatus(_Out_ PANEL_STATUS_OUTPUT& Output);

    private:
        HRESULT SendIoctl(_In_ DWORD IoControlCode,
                          _In_reads_bytes_opt_(InputSize) const void* Input, _In_ DWORD InputSize,
                          _Out_writes_bytes_(OutputSize) void* Output, _In_ DWORD OutputSize);

        HANDLE       m_Device;
        std::wstring m_InterfacePath;
    };
}
