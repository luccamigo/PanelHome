/*++

Module Name:

    main.cpp

Abstract:

    PanelDisplayCtl - temporary console front end for the PanelDisplayDriver
    control interface.

        PanelDisplayCtl status [--verbose]
        PanelDisplayCtl connect [--mode WIDTHxHEIGHT@HZ]
        PanelDisplayCtl disconnect

    This exists so connect/disconnect can be exercised by hand. PanelHost will
    later drive the same interface directly through DriverClient, and this tool
    can go away.

    Exit codes:
        0  command succeeded (including the idempotent "already ..." cases)
        1  usage error
        2  driver not reachable
        3  the driver reported a failure

--*/

#include <windows.h>
#include <winioctl.h>
#include <stdio.h>

#include "CommandLine.h"
#include "DriverClient.h"
#include "../../common/PanelDisplayModes.h"

namespace
{
    constexpr int ExitOk = 0;
    constexpr int ExitUsage = 1;
    constexpr int ExitDriverUnavailable = 2;
    constexpr int ExitCommandFailed = 3;

    void PrintUsage()
    {
        wprintf(L"PanelDisplayCtl - control the PanelOS virtual display\n\n");
        wprintf(L"Usage:\n");
        wprintf(L"  PanelDisplayCtl status [--verbose]\n");
        wprintf(L"  PanelDisplayCtl connect [--mode WIDTHxHEIGHT@HZ]\n");
        wprintf(L"  PanelDisplayCtl disconnect\n\n");
        wprintf(L"Examples:\n");
        wprintf(L"  PanelDisplayCtl connect\n");
        wprintf(L"  PanelDisplayCtl connect --mode 1920x1200@60\n");
    }

    const wchar_t* DriverStateText(PANEL_DRIVER_STATE state)
    {
        switch (state)
        {
        case PanelDriverStateInitializing: return L"Initializing";
        case PanelDriverStateReady:        return L"Ready";
        case PanelDriverStateStopped:      return L"Stopped";
        default:                           return L"Unknown";
        }
    }

    const wchar_t* MonitorStateText(PANEL_MONITOR_STATE state)
    {
        switch (state)
        {
        case PanelMonitorStateDisconnected: return L"Disconnected";
        case PanelMonitorStateConnected:    return L"Connected";
        default:                            return L"Unknown";
        }
    }

    bool IsProcessElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
        {
            return false;
        }

        TOKEN_ELEVATION elevation = {};
        DWORD returned = 0;
        const BOOL ok = GetTokenInformation(token, TokenElevation, &elevation, sizeof(elevation), &returned);
        CloseHandle(token);

        return ok && elevation.TokenIsElevated != 0;
    }

    //
    // Symbolic name for the codes worth recognising here. Anything else is
    // reported numerically rather than guessed at.
    //
    const wchar_t* Win32ErrorName(DWORD error)
    {
        switch (error)
        {
        case ERROR_FILE_NOT_FOUND:    return L"ERROR_FILE_NOT_FOUND";
        case ERROR_PATH_NOT_FOUND:    return L"ERROR_PATH_NOT_FOUND";
        case ERROR_ACCESS_DENIED:     return L"ERROR_ACCESS_DENIED";
        case ERROR_INVALID_HANDLE:    return L"ERROR_INVALID_HANDLE";
        case ERROR_GEN_FAILURE:       return L"ERROR_GEN_FAILURE";
        case ERROR_SHARING_VIOLATION: return L"ERROR_SHARING_VIOLATION";
        case ERROR_NOT_FOUND:         return L"ERROR_NOT_FOUND";
        default:                      return nullptr;
        }
    }

    void PrintSystemErrorText(DWORD error)
    {
        LPWSTR text = nullptr;
        const DWORD length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr, error, 0, reinterpret_cast<LPWSTR>(&text), 0, nullptr);

        if (length != 0 && text != nullptr)
        {
            fwprintf(stderr, L"             %s", text);
            LocalFree(text);
        }
    }

    void ReportOpenFailure(const PanelOS::DriverClient& client, HRESULT hr, DWORD desiredAccess)
    {
        const bool elevated = IsProcessElevated();

        //
        // "Interface not found" is a genuinely different situation from "the
        // interface is there and refused us", so it keeps its own message.
        //
        if (hr == HRESULT_FROM_WIN32(ERROR_NOT_FOUND) && client.InterfacePath()[0] == L'\0')
        {
            fwprintf(stderr, L"No PanelDisplayDriver device interface is present.\n");
            fwprintf(stderr, L"The driver is not installed, or its device node does not exist.\n");
            fwprintf(stderr, L"See README.md, \"Instalacao\".\n");
            return;
        }

        const DWORD error = HRESULT_CODE(hr);

        fwprintf(stderr, L"Failed to open PanelDisplayDriver device interface.\n");

        const wchar_t* name = Win32ErrorName(error);
        if (name != nullptr)
        {
            fwprintf(stderr, L"Win32 error: %lu (%s)\n", error, name);
        }
        else
        {
            fwprintf(stderr, L"Win32 error: %lu\n", error);
        }

        PrintSystemErrorText(error);

        fwprintf(stderr, L"Interface: %s\n", client.InterfacePath());
        fwprintf(stderr, L"Requested access: 0x%08lX%s\n", desiredAccess,
                 desiredAccess == GENERIC_READ ? L" (GENERIC_READ)" :
                 desiredAccess == GENERIC_WRITE ? L" (GENERIC_WRITE)" : L"");
        fwprintf(stderr, L"Process elevated: %s\n", elevated ? L"yes" : L"no");

        if (error != ERROR_ACCESS_DENIED)
        {
            return;
        }

        //
        // Only suggest elevation when elevation is actually missing. Telling an
        // already-elevated user to elevate sends them chasing the wrong problem -
        // which is exactly what the previous version of this message did.
        //
        if (!elevated)
        {
            //
            // Elevation is the thing to try first, but do not assert it is the
            // cause - the ACL is not readable from here, and claiming a cause we
            // have not confirmed is how the old message misled people.
            //
            fwprintf(stderr, L"\nThis interface is meant to grant access to Administrators only,\n");
            fwprintf(stderr, L"so try again from an elevated prompt.\n");
            fwprintf(stderr, L"If it still fails when elevated, elevation was not the problem:\n");
            fwprintf(stderr, L"see README.md, \"Troubleshooting\".\n");
        }
        else
        {
            fwprintf(stderr, L"\nThe process IS elevated, so this is not an elevation problem.\n");
            fwprintf(stderr, L"The device stack is refusing the open, which means the installed\n");
            fwprintf(stderr, L"package has no security descriptor granting Administrators access.\n");
            fwprintf(stderr, L"Reinstall the driver package: the security descriptor is applied\n");
            fwprintf(stderr, L"from the INF when the device node is created, so an in-place driver\n");
            fwprintf(stderr, L"update alone does not pick it up. See README.md, \"Troubleshooting\".\n");
        }
    }

    int RunStatus(PanelOS::DriverClient& client, bool verbose)
    {
        PANEL_STATUS_OUTPUT status = {};

        const HRESULT hr = client.GetStatus(status);
        if (FAILED(hr))
        {
            fwprintf(stderr, L"Failed to read status (0x%08X).\n", static_cast<unsigned>(hr));
            return ExitCommandFailed;
        }

        wprintf(L"PanelDisplayDriver: %s\n", DriverStateText(status.DriverState));
        wprintf(L"Virtual monitor: %s\n", MonitorStateText(status.MonitorState));

        if (!verbose)
        {
            return ExitOk;
        }

        wprintf(L"\n");
        wprintf(L"Protocol version: %lu (tool built for %u)\n",
                status.ProtocolVersion, PANEL_DISPLAY_PROTOCOL_VERSION);
        wprintf(L"Connects since load: %lu\n", status.ConnectCount);

        if (status.MonitorState == PanelMonitorStateConnected)
        {
            if (status.ActiveMode.Width != 0)
            {
                wprintf(L"Active mode: %lux%lu @ %lu Hz\n",
                        status.ActiveMode.Width, status.ActiveMode.Height, status.ActiveMode.RefreshHz);
            }
            else
            {
                wprintf(L"Active mode: none (monitor present but not activated by Windows)\n");
            }

            wprintf(L"Frames processed: %llu\n", status.FramesProcessed);
        }

        wprintf(L"Advertised modes:\n");
        for (ULONG i = 0; i < status.ModeCount && i < PANEL_MAX_REPORTED_MODES; ++i)
        {
            wprintf(L"  %lux%lu @ %lu Hz\n",
                    status.Modes[i].Width, status.Modes[i].Height, status.Modes[i].RefreshHz);
        }

        return ExitOk;
    }

    int RunConnect(PanelOS::DriverClient& client, const PanelOS::ParsedCommandLine& parsed)
    {
        PANEL_CONNECT_INPUT input = {};
        const PANEL_CONNECT_INPUT* inputPtr = nullptr;

        if (parsed.HasPreferredMode)
        {
            input.Size = sizeof(input);
            input.Flags = PANEL_CONNECT_FLAG_USE_PREFERRED_MODE;
            input.PreferredMode = parsed.PreferredMode;
            inputPtr = &input;
        }

        PANEL_COMMAND_OUTPUT output = {};

        const HRESULT hr = client.Connect(inputPtr, output);
        if (FAILED(hr))
        {
            fwprintf(stderr, L"Connect failed (0x%08X).\n", static_cast<unsigned>(hr));
            return ExitCommandFailed;
        }

        switch (output.Result)
        {
        case PanelCommandResultOk:
            wprintf(L"Virtual monitor connected.\n");
            return ExitOk;

        case PanelCommandResultAlreadyConnected:
            wprintf(L"Virtual monitor already connected.\n");
            return ExitOk;

        case PanelCommandResultAdapterNotReady:
            fwprintf(stderr, L"Driver is still initializing; try again in a moment.\n");
            return ExitCommandFailed;

        case PanelCommandResultInvalidMode:
            fwprintf(stderr, L"The driver rejected the requested mode.\n");
            return ExitCommandFailed;

        default:
            fwprintf(stderr, L"Connect failed (driver result %d).\n", static_cast<int>(output.Result));
            return ExitCommandFailed;
        }
    }

    int RunDisconnect(PanelOS::DriverClient& client)
    {
        PANEL_COMMAND_OUTPUT output = {};

        const HRESULT hr = client.Disconnect(output);
        if (FAILED(hr))
        {
            fwprintf(stderr, L"Disconnect failed (0x%08X).\n", static_cast<unsigned>(hr));
            return ExitCommandFailed;
        }

        switch (output.Result)
        {
        case PanelCommandResultOk:
            wprintf(L"Virtual monitor disconnected.\n");
            return ExitOk;

        case PanelCommandResultAlreadyDisconnected:
            wprintf(L"Virtual monitor already disconnected.\n");
            return ExitOk;

        default:
            fwprintf(stderr, L"Disconnect failed (driver result %d).\n", static_cast<int>(output.Result));
            return ExitCommandFailed;
        }
    }
}

int wmain(int argc, wchar_t* argv[])
{
    const PanelOS::ParsedCommandLine parsed = PanelOS::ParseCommandLine(argc, argv);

    if (parsed.Error != nullptr)
    {
        fwprintf(stderr, L"%s\n\n", parsed.Error);
        PrintUsage();
        return ExitUsage;
    }

    if (parsed.Command == PanelOS::CtlCommand::Help)
    {
        PrintUsage();
        return ExitOk;
    }

    //
    // Validate a requested mode before touching the device, so a typo is
    // reported as a typo even when the driver is not installed. The driver
    // validates independently - this is a usability check, not the gate.
    //
    if (parsed.HasPreferredMode && !PanelOS::IsModeValid(parsed.PreferredMode))
    {
        fwprintf(stderr, L"Mode %lux%lu@%lu is outside the supported range "
                         L"(%lu-%lu x %lu-%lu at %lu-%lu Hz, even dimensions).\n",
                 parsed.PreferredMode.Width, parsed.PreferredMode.Height, parsed.PreferredMode.RefreshHz,
                 PanelOS::ModeLimits::MinWidth, PanelOS::ModeLimits::MaxWidth,
                 PanelOS::ModeLimits::MinHeight, PanelOS::ModeLimits::MaxHeight,
                 PanelOS::ModeLimits::MinRefreshHz, PanelOS::ModeLimits::MaxRefreshHz);
        return ExitUsage;
    }

    //
    // Open with only the access the chosen command needs; see Public.h for how
    // that maps onto each IOCTL's access bits.
    //
    const DWORD desiredAccess = (parsed.Command == PanelOS::CtlCommand::Status)
        ? PanelOS::DriverClient::AccessForStatus
        : PanelOS::DriverClient::AccessForControl;

    PanelOS::DriverClient client;

    const HRESULT hr = client.Open(desiredAccess);
    if (FAILED(hr))
    {
        ReportOpenFailure(client, hr, desiredAccess);
        return ExitDriverUnavailable;
    }

    switch (parsed.Command)
    {
    case PanelOS::CtlCommand::Status:
        return RunStatus(client, parsed.Verbose);

    case PanelOS::CtlCommand::Connect:
        return RunConnect(client, parsed);

    case PanelOS::CtlCommand::Disconnect:
        return RunDisconnect(client);

    default:
        PrintUsage();
        return ExitUsage;
    }
}
