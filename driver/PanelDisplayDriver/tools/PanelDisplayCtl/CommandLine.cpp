/*++

Module Name:

    CommandLine.cpp

Abstract:

    Implementation of PanelDisplayCtl command line parsing. See CommandLine.h.

--*/

#include "CommandLine.h"

namespace PanelOS
{
    namespace
    {
        bool EqualsIgnoreCase(const wchar_t* left, const wchar_t* right)
        {
            return _wcsicmp(left, right) == 0;
        }

        //
        // Reads a decimal number, advancing 'cursor'. Returns false on no digits
        // or on overflow past a sane display dimension.
        //
        bool ReadNumber(const wchar_t*& cursor, ULONG& value)
        {
            if (*cursor < L'0' || *cursor > L'9')
            {
                return false;
            }

            ULONG result = 0;

            while (*cursor >= L'0' && *cursor <= L'9')
            {
                const ULONG digit = static_cast<ULONG>(*cursor - L'0');

                //
                // 100000 is far above any real display dimension or refresh rate;
                // stopping here keeps the multiply below from overflowing.
                //
                if (result > 100000)
                {
                    return false;
                }

                result = (result * 10) + digit;
                ++cursor;
            }

            value = result;
            return true;
        }
    }

    bool ParseModeSpec(_In_z_ const wchar_t* Text, _Out_ PANEL_DISPLAY_MODE& Mode)
    {
        Mode = PANEL_DISPLAY_MODE{};

        if (Text == nullptr)
        {
            return false;
        }

        const wchar_t* cursor = Text;

        ULONG width = 0;
        if (!ReadNumber(cursor, width))
        {
            return false;
        }

        if (*cursor != L'x' && *cursor != L'X')
        {
            return false;
        }
        ++cursor;

        ULONG height = 0;
        if (!ReadNumber(cursor, height))
        {
            return false;
        }

        if (*cursor != L'@')
        {
            return false;
        }
        ++cursor;

        ULONG refresh = 0;
        if (!ReadNumber(cursor, refresh))
        {
            return false;
        }

        //
        // Trailing characters mean the caller mistyped something; better to
        // reject than to silently use a prefix.
        //
        if (*cursor != L'\0')
        {
            return false;
        }

        Mode.Width = width;
        Mode.Height = height;
        Mode.RefreshHz = refresh;

        return true;
    }

    ParsedCommandLine ParseCommandLine(_In_ int Argc, _In_reads_(Argc) const wchar_t* const* Argv)
    {
        ParsedCommandLine parsed = {};
        parsed.Command = CtlCommand::None;

        if (Argc < 2)
        {
            parsed.Command = CtlCommand::Help;
            return parsed;
        }

        const wchar_t* verb = Argv[1];

        if (EqualsIgnoreCase(verb, L"status"))
        {
            parsed.Command = CtlCommand::Status;
        }
        else if (EqualsIgnoreCase(verb, L"connect"))
        {
            parsed.Command = CtlCommand::Connect;
        }
        else if (EqualsIgnoreCase(verb, L"disconnect"))
        {
            parsed.Command = CtlCommand::Disconnect;
        }
        else if (EqualsIgnoreCase(verb, L"help") ||
                 EqualsIgnoreCase(verb, L"-h") ||
                 EqualsIgnoreCase(verb, L"--help") ||
                 EqualsIgnoreCase(verb, L"/?"))
        {
            parsed.Command = CtlCommand::Help;
            return parsed;
        }
        else
        {
            parsed.Error = L"Unknown command.";
            return parsed;
        }

        for (int i = 2; i < Argc; ++i)
        {
            const wchar_t* option = Argv[i];

            if (EqualsIgnoreCase(option, L"--mode"))
            {
                if (parsed.Command != CtlCommand::Connect)
                {
                    parsed.Error = L"--mode is only valid with 'connect'.";
                    return parsed;
                }

                if (i + 1 >= Argc)
                {
                    parsed.Error = L"--mode requires a value, for example 1920x1200@60.";
                    return parsed;
                }

                if (!ParseModeSpec(Argv[i + 1], parsed.PreferredMode))
                {
                    parsed.Error = L"Invalid mode. Expected WIDTHxHEIGHT@HZ, for example 1920x1200@60.";
                    return parsed;
                }

                parsed.HasPreferredMode = true;
                ++i;
            }
            else if (EqualsIgnoreCase(option, L"--verbose") || EqualsIgnoreCase(option, L"-v"))
            {
                if (parsed.Command != CtlCommand::Status)
                {
                    parsed.Error = L"--verbose is only valid with 'status'.";
                    return parsed;
                }

                parsed.Verbose = true;
            }
            else
            {
                parsed.Error = L"Unknown option.";
                return parsed;
            }
        }

        return parsed;
    }
}
