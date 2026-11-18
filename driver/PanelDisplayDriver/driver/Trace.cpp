/*++

Module Name:

    Trace.cpp

Abstract:

    TraceLogging provider definition and debug-build mirroring. See Trace.h.

--*/

#include "Trace.h"

#include <stdio.h>
#include <stdarg.h>

TRACELOGGING_DEFINE_PROVIDER(
    g_PanelTraceProvider,
    "PanelOS.PanelDisplayDriver",
    // {7FD77C51-84BD-4C77-B668-9A7C64C893BD}
    (0x7fd77c51, 0x84bd, 0x4c77, 0xb6, 0x68, 0x9a, 0x7c, 0x64, 0xc8, 0x93, 0xbd));

void PanelTraceRegister()
{
    TraceLoggingRegister(g_PanelTraceProvider);
}

void PanelTraceUnregister()
{
    TraceLoggingUnregister(g_PanelTraceProvider);
}

#if DBG
void PanelDebugPrint(_In_z_ _Printf_format_string_ const char* Format, ...)
{
    char buffer[512];

    va_list args;
    va_start(args, Format);
    const int written = _vsnprintf_s(buffer, sizeof(buffer), _TRUNCATE, Format, args);
    va_end(args);

    if (written < 0)
    {
        //
        // Truncation already produced a terminated string; only a hard encoding
        // failure lands here, and there is nothing useful to print.
        //
        return;
    }

    OutputDebugStringA(buffer);
}
#endif
