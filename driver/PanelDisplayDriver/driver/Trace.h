/*++

Module Name:

    Trace.h

Abstract:

    Logging for PanelDisplayDriver.

    TraceLogging is used rather than WPP because it needs no build-time trace
    preprocessing step, and the events are self-describing: a developer can
    collect them with wpr/tracelog and read them in WPA or tracefmt without
    having to ship a PDB or a TMF file. See README.md, "Collecting logs".

    Continuous per-frame logging is forbidden. For First Pixels diagnostics,
    each swapchain may emit details for only its first published frame;
    teardown records one cumulative capture summary.

--*/

#pragma once

#include <windows.h>
#include <TraceLoggingProvider.h>
#include <evntrace.h>

//
// Provider GUID: {7FD77C51-84BD-4C77-B668-9A7C64C893BD}
// Friendly name: PanelOS.PanelDisplayDriver
//
TRACELOGGING_DECLARE_PROVIDER(g_PanelTraceProvider);

void PanelTraceRegister();
void PanelTraceUnregister();

//
// In debug builds every event is mirrored to the debugger output so that
// DebugView / windbg is enough during bring-up, with no ETW session needed.
//
#if DBG
void PanelDebugPrint(_In_z_ _Printf_format_string_ const char* Format, ...);
#define PANEL_DEBUG_PRINT(...) PanelDebugPrint(__VA_ARGS__)
#else
#define PANEL_DEBUG_PRINT(...) ((void)0)
#endif

#define PANEL_TRACE_INFO(EventName, ...)                    \
    TraceLoggingWrite(g_PanelTraceProvider, EventName,      \
        TraceLoggingLevel(TRACE_LEVEL_INFORMATION),         \
        __VA_ARGS__)

#define PANEL_TRACE_INFO0(EventName)                        \
    TraceLoggingWrite(g_PanelTraceProvider, EventName,      \
        TraceLoggingLevel(TRACE_LEVEL_INFORMATION))

#define PANEL_TRACE_ERROR(EventName, ...)                   \
    TraceLoggingWrite(g_PanelTraceProvider, EventName,      \
        TraceLoggingLevel(TRACE_LEVEL_ERROR),               \
        __VA_ARGS__)

#define PANEL_TRACE_VERBOSE(EventName, ...)                 \
    TraceLoggingWrite(g_PanelTraceProvider, EventName,      \
        TraceLoggingLevel(TRACE_LEVEL_VERBOSE),             \
        __VA_ARGS__)
