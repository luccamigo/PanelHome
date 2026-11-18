/*++

Module Name:

    CommandLine.h

Abstract:

    Command line parsing for PanelDisplayCtl.

    Kept separate from main.cpp and from any device I/O so that the parsing rules
    can be unit tested without a driver installed - see
    tests/PanelDisplayDriver.Tests.

--*/

#pragma once

#include <windows.h>
#include <winioctl.h>

#include "../../driver/Public.h"

namespace PanelOS
{
    enum class CtlCommand
    {
        None,
        Status,
        Connect,
        Disconnect,
        Help,
    };

    struct ParsedCommandLine
    {
        CtlCommand Command;

        //
        // Set by "connect --mode WIDTHxHEIGHT@HZ". This is the seam PanelHost
        // will use to pass the tablet's real geometry; for now it is a manual
        // override for testing mode validation end to end.
        //
        bool               HasPreferredMode;
        PANEL_DISPLAY_MODE PreferredMode;

        //
        // Set by "status --verbose". Plain "status" prints exactly the two
        // documented lines, which keeps it easy to parse from a script.
        //
        bool Verbose;

        //
        // Non-null when parsing failed. Points at a static string.
        //
        const wchar_t* Error;
    };

    //
    // Parses "WIDTHxHEIGHT@HZ" (for example "1920x1200@60"). Returns false if the
    // text is malformed or any component overflows. Does not check the mode
    // against the driver's limits - that is IsModeValid()'s job.
    //
    bool ParseModeSpec(_In_z_ const wchar_t* Text, _Out_ PANEL_DISPLAY_MODE& Mode);

    //
    // argv[0] is the program name and is ignored, matching wmain.
    //
    ParsedCommandLine ParseCommandLine(_In_ int Argc, _In_reads_(Argc) const wchar_t* const* Argv);
}
