/*++

Module Name:

    Monitor.cpp

Abstract:

    PanelMonitorContext plus the IddCx monitor and mode callbacks.

    Mode reporting works in three stages, all driven by IddCx:

      1. ParseMonitorDescription - convert the EDID we published into the list of
         monitor modes. Answered from the built-in default list, which is exactly
         what tools/Generate-Edid.ps1 encoded into the EDID.

      2. GetDefaultDescriptionModes - fallback used only if a monitor is created
         without a description. Kept for robustness.

      3. QueryTargetModes - the target (signal) modes the driver can drive.
         Answered from the monitor's own mode table snapshot, so a mode supplied
         later by PanelHost surfaces here.

--*/

#include "Monitor.h"
#include "SwapChainProcessor.h"

using namespace Microsoft::WRL;

void PanelFillSignalInfo(_Out_ DISPLAYCONFIG_VIDEO_SIGNAL_INFO& SignalInfo,
                         _In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync,
                         _In_ bool bIsMonitorMode)
{
    SignalInfo = {};

    SignalInfo.totalSize.cx = SignalInfo.activeSize.cx = Width;
    SignalInfo.totalSize.cy = SignalInfo.activeSize.cy = Height;

    //
    // Monitor modes use a vSyncFreqDivider of 0, target modes 1. videoStandard
    // 255 means "other/unknown", which is correct for a synthesised signal.
    //
    SignalInfo.AdditionalSignalInfo.vSyncFreqDivider = bIsMonitorMode ? 0 : 1;
    SignalInfo.AdditionalSignalInfo.videoStandard = 255;

    SignalInfo.vSyncFreq.Numerator = VSync;
    SignalInfo.vSyncFreq.Denominator = 1;
    SignalInfo.hSyncFreq.Numerator = VSync * Height;
    SignalInfo.hSyncFreq.Denominator = 1;

    SignalInfo.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;

    //
    // No blanking is modelled: there is no real cable to drive, so the pixel
    // rate is simply the active pixel count per second.
    //
    SignalInfo.pixelRate = static_cast<UINT64>(VSync) * static_cast<UINT64>(Width) * static_cast<UINT64>(Height);
}

IDDCX_MONITOR_MODE PanelCreateMonitorMode(_In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync,
                                          _In_ IDDCX_MONITOR_MODE_ORIGIN Origin)
{
    IDDCX_MONITOR_MODE mode = {};

    mode.Size = sizeof(mode);
    mode.Origin = Origin;
    PanelFillSignalInfo(mode.MonitorVideoSignalInfo, Width, Height, VSync, true);

    return mode;
}

IDDCX_TARGET_MODE PanelCreateTargetMode(_In_ DWORD Width, _In_ DWORD Height, _In_ DWORD VSync)
{
    IDDCX_TARGET_MODE mode = {};

    mode.Size = sizeof(mode);
    PanelFillSignalInfo(mode.TargetVideoSignalInfo.targetVideoSignalInfo, Width, Height, VSync, false);

    return mode;
}

PANEL_DISPLAY_MODE PanelSignalInfoToMode(_In_ const DISPLAYCONFIG_VIDEO_SIGNAL_INFO& SignalInfo)
{
    PANEL_DISPLAY_MODE mode = {};

    mode.Width = static_cast<ULONG>(SignalInfo.activeSize.cx);
    mode.Height = static_cast<ULONG>(SignalInfo.activeSize.cy);

    if (SignalInfo.vSyncFreq.Denominator != 0)
    {
        //
        // Round to nearest so 59.94 Hz style rationals report as 60.
        //
        mode.RefreshHz = static_cast<ULONG>(
            (SignalInfo.vSyncFreq.Numerator + (SignalInfo.vSyncFreq.Denominator / 2)) /
            SignalInfo.vSyncFreq.Denominator);
    }

    return mode;
}

PanelMonitorContext::PanelMonitorContext(_In_ IDDCX_MONITOR MonitorObject,
                                         _In_ const PanelOS::ModeTable& Modes,
                                         _In_ PanelOS::LatestFrameStore* FrameStore,
                                         _In_ ULONG TargetFramesPerSecond)
    : m_Monitor(MonitorObject)
    , m_Modes(Modes)
    , m_FrameStore(FrameStore)
    , m_Departing(false)
    , m_AssignmentsInFlight(0)
    , m_TargetFramesPerSecond(static_cast<LONG>(TargetFramesPerSecond))
    , m_ActiveMode{}
{
    InitializeCriticalSection(&m_ModeLock);
    InitializeCriticalSection(&m_SwapChainLock);
    InitializeConditionVariable(&m_SwapChainCondition);
    PANEL_TRACE_INFO("VideoTarget.MonitorCreated",
        TraceLoggingUInt32(TargetFramesPerSecond, "targetFps"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoTarget] Monitor created target=%lu\n",
        TargetFramesPerSecond);
}

PanelMonitorContext::~PanelMonitorContext()
{
    //
    // Destroying the processor joins its thread, so no frame can be in flight
    // once this returns. By the time the disconnect path gets here the swap
    // chain has normally already been stopped; this covers the paths that did
    // not go through RemoveMonitor, such as the device being torn down.
    //
    BeginDeparture();

    DeleteCriticalSection(&m_SwapChainLock);
    DeleteCriticalSection(&m_ModeLock);
}

NTSTATUS PanelMonitorContext::AssignSwapChain(_In_ IDDCX_SWAPCHAIN SwapChain,
                                              _In_ LUID RenderAdapter,
                                              _In_ HANDLE NewFrameEvent)
{
    // Assignment can race the explicit disconnect path. Check before doing
    // expensive setup, then check again while publishing: BeginDeparture may
    // run in between these two points.
    EnterCriticalSection(&m_SwapChainLock);
    const bool alreadyDeparting = m_Departing;
    if (!alreadyDeparting)
    {
        ++m_AssignmentsInFlight;
    }
    LeaveCriticalSection(&m_SwapChainLock);

    if (alreadyDeparting)
    {
        PANEL_TRACE_INFO0("SwapChain.AssignmentRejectedDuringDeparture");
        // Returning ABANDON means ownership remains with IddCx. Deleting here
        // would race the parent monitor's departure and could double-delete the
        // child swapchain.
        return STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
    }

    NTSTATUS assignmentStatus = STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
    ULONGLONG generation = 0;

    try
    {
        //
        // Drop any previous swap chain first; IddCx guarantees only one at a
        // time. This callback is registered as in-flight, so BeginDeparture
        // waits for every object touched below.
        //
        UnassignSwapChain();

        auto device = std::make_shared<Direct3DDevice>(RenderAdapter);
        const HRESULT hr = device->Init();

        if (FAILED(hr))
        {
            // Return ABANDON without touching hSwapChain. IddCx retains
            // ownership and can retry on a stable render adapter.
            PANEL_TRACE_ERROR("Monitor.SwapChainDeviceInitFailed", TraceLoggingHResult(hr, "hr"));
        }
        else
        {
            generation = m_FrameStore->BeginGeneration();
            const ULONG targetFramesPerSecond = static_cast<ULONG>(
                InterlockedCompareExchange(&m_TargetFramesPerSecond, 0, 0));
            PANEL_TRACE_INFO("VideoTarget.SwapChainProcessorCreating",
                TraceLoggingUInt32(targetFramesPerSecond, "targetFps"),
                TraceLoggingUInt64(generation, "generation"));
            PANEL_DEBUG_PRINT(
                "[PanelDisplayDriver][VideoTarget] SwapChainProcessor created target=%lu "
                "generation=%llu\n",
                targetFramesPerSecond,
                static_cast<unsigned long long>(generation));
            auto processor = std::make_unique<SwapChainProcessor>(
                SwapChain, device, NewFrameEvent, m_FrameStore, generation,
                targetFramesPerSecond);

            if (!processor->Start())
            {
                PANEL_TRACE_ERROR("Monitor.SwapChainProcessorStartFailed",
                    TraceLoggingPointer(SwapChain, "swapChain"));
            }
            else
            {
                std::unique_ptr<SwapChainProcessor> replacedProcessor;

                EnterCriticalSection(&m_SwapChainLock);
                // This assignment entered before m_Departing was set. Publish
                // it even if teardown began during setup; BeginDeparture waits
                // for the in-flight count, then moves and joins this processor.
                replacedProcessor = std::move(m_SwapChain);
                processor->SetCaptureTargetFps(static_cast<ULONG>(
                    InterlockedCompareExchange(
                        &m_TargetFramesPerSecond, 0, 0)));
                m_SwapChain = std::move(processor);
                LeaveCriticalSection(&m_SwapChainLock);

                replacedProcessor.reset();
                assignmentStatus = STATUS_SUCCESS;
            }
        }
    }
    catch (...)
    {
        // The project uses /EHsc and STL allocation can throw. Never let an
        // allocation failure escape a UMDF callback or strand the departure
        // barrier. No success was returned, so IddCx still owns hSwapChain.
        PANEL_TRACE_ERROR("Monitor.SwapChainAssignmentException",
            TraceLoggingNTStatus(STATUS_INSUFFICIENT_RESOURCES, "status"));
        assignmentStatus = STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
    }

    if (!NT_SUCCESS(assignmentStatus) && generation != 0)
    {
        m_FrameStore->InvalidateGeneration(generation);
    }

    // Must be the final access to this monitor context. BeginDeparture can wake
    // and call IddCxMonitorDeparture as soon as the counter reaches zero.
    FinishSwapChainAssignment();
    return assignmentStatus;
}

void PanelMonitorContext::FinishSwapChainAssignment()
{
    EnterCriticalSection(&m_SwapChainLock);
    if (m_AssignmentsInFlight == 0)
    {
        LeaveCriticalSection(&m_SwapChainLock);
        PANEL_TRACE_ERROR("SwapChain.AssignmentCounterUnderflowPrevented",
            TraceLoggingUInt32(0, "assignmentsInFlight"));
        return;
    }

    --m_AssignmentsInFlight;
    if (m_AssignmentsInFlight == 0)
    {
        WakeAllConditionVariable(&m_SwapChainCondition);
    }
    LeaveCriticalSection(&m_SwapChainLock);
}

void PanelMonitorContext::UnassignSwapChain()
{
    PANEL_TRACE_INFO0("SwapChain.ShutdownBegin");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] swapchain shutdown begin\n");

    //
    // Take ownership under the lock, then destroy outside it. The destructor
    // joins the processing thread, and holding a lock across a thread join is
    // how avoidable deadlocks get introduced.
    //
    // Safe to call more than once: the disconnect path stops the swap chain
    // before reporting departure, and IddCx may also unassign it as part of the
    // same teardown. The second call finds nothing to do.
    //
    std::unique_ptr<SwapChainProcessor> processor;

    EnterCriticalSection(&m_SwapChainLock);
    processor = std::move(m_SwapChain);
    LeaveCriticalSection(&m_SwapChainLock);

    if (processor)
    {
        m_FrameStore->InvalidateGeneration(processor->Generation());
    }
    processor.reset();

    PANEL_TRACE_INFO0("SwapChain.ShutdownComplete");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] swapchain shutdown complete\n");
}

void PanelMonitorContext::BeginDeparture()
{
    PANEL_TRACE_INFO0("SwapChain.DepartureShutdownBegin");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] swapchain departure shutdown begin\n");

    std::unique_ptr<SwapChainProcessor> processor;

    EnterCriticalSection(&m_SwapChainLock);
    m_Departing = true;

    // An Assign callback can already be preparing a newly supplied swapchain
    // outside this lock. It entered before m_Departing, so it commits ownership,
    // publishes its processor, and signals here. Do not report departure until
    // every such owner has finished.
    while (m_AssignmentsInFlight != 0)
    {
        (void)SleepConditionVariableCS(&m_SwapChainCondition,
                                       &m_SwapChainLock,
                                       INFINITE);
    }

    // Any assignment that entered before m_Departing published its committed
    // processor before decrementing the barrier. Move it only after the wait.
    processor = std::move(m_SwapChain);
    LeaveCriticalSection(&m_SwapChainLock);

    if (processor)
    {
        m_FrameStore->InvalidateGeneration(processor->Generation());
    }
    processor.reset();

    PANEL_TRACE_INFO0("SwapChain.DepartureShutdownComplete");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] swapchain departure shutdown complete\n");
}

void PanelMonitorContext::SetActiveMode(_In_ const PANEL_DISPLAY_MODE& Mode, _In_ bool Active)
{
    EnterCriticalSection(&m_ModeLock);

    if (Active)
    {
        m_ActiveMode = Mode;
    }
    else
    {
        m_ActiveMode = PANEL_DISPLAY_MODE{};
    }

    LeaveCriticalSection(&m_ModeLock);
}

PANEL_DISPLAY_MODE PanelMonitorContext::ActiveMode() const
{
    EnterCriticalSection(&m_ModeLock);
    const PANEL_DISPLAY_MODE mode = m_ActiveMode;
    LeaveCriticalSection(&m_ModeLock);

    return mode;
}

ULONGLONG PanelMonitorContext::FramesProcessed() const
{
    //
    // Held across the read so the processor cannot be destroyed underneath us by
    // a concurrent teardown; the read itself is a single interlocked load.
    //
    EnterCriticalSection(&m_SwapChainLock);
    const ULONGLONG frames = m_SwapChain ? m_SwapChain->FramesProcessed() : 0;
    LeaveCriticalSection(&m_SwapChainLock);

    return frames;
}

void PanelMonitorContext::SetCaptureTargetFps(
    _In_ ULONG TargetFramesPerSecond)
{
    const LONG previous = InterlockedExchange(
        &m_TargetFramesPerSecond,
        static_cast<LONG>(TargetFramesPerSecond));

    PANEL_TRACE_INFO("VideoTarget.MonitorUpdated",
        TraceLoggingUInt32(static_cast<ULONG>(previous), "previousTargetFps"),
        TraceLoggingUInt32(TargetFramesPerSecond, "targetFps"));

    EnterCriticalSection(&m_SwapChainLock);
    if (m_SwapChain)
    {
        m_SwapChain->SetCaptureTargetFps(TargetFramesPerSecond);
    }
    LeaveCriticalSection(&m_SwapChainLock);
}

void PanelMonitorContext::GetCaptureMetrics(
    _Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output) const
{
    EnterCriticalSection(&m_SwapChainLock);
    if (m_SwapChain)
    {
        m_SwapChain->GetCaptureMetrics(Output);
    }
    else
    {
        Output = {};
        Output.Size = sizeof(Output);
        Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;
    }
    Output.TargetFramesPerSecond = static_cast<ULONG>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&m_TargetFramesPerSecond), 0, 0));
    LeaveCriticalSection(&m_SwapChainLock);
}

void PanelMonitorContext::GetCapturePacingMetrics(
    _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output) const
{
    EnterCriticalSection(&m_SwapChainLock);
    if (m_SwapChain)
    {
        m_SwapChain->GetCapturePacingMetrics(Output);
    }
    else
    {
        Output = {};
        Output.Size = sizeof(Output);
        Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;
        Output.HistogramBucketCount =
            PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS;
    }
    Output.TargetFramesPerSecond = static_cast<ULONG>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&m_TargetFramesPerSecond), 0, 0));
    LeaveCriticalSection(&m_SwapChainLock);
}

// ---------------------------------------------------------------------------
// IddCx callbacks
// ---------------------------------------------------------------------------

_Use_decl_annotations_
NTSTATUS PanelDisplayParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs,
                                             IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    //
    // This callback is global - IddCx does not pass a monitor or adapter object -
    // so it is answered from the built-in default mode list rather than from a
    // particular monitor's table. That is correct as long as the EDID in Edid.h
    // and the defaults in PanelDisplayModes.cpp describe the same modes, which
    // tools/Generate-Edid.ps1 is responsible for keeping true.
    //
    pOutArgs->MonitorModeBufferOutputCount = PanelOS::DefaultModeCount;

    if (pInArgs->MonitorModeBufferInputCount == 0)
    {
        //
        // Size query: IddCx is asking how many modes we will report.
        //
        return STATUS_SUCCESS;
    }

    if (pInArgs->MonitorModeBufferInputCount < PanelOS::DefaultModeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    const PANEL_DISPLAY_MODE* modes = PanelOS::DefaultModes();

    for (ULONG i = 0; i < PanelOS::DefaultModeCount; ++i)
    {
        pInArgs->pMonitorModes[i] = PanelCreateMonitorMode(
            modes[i].Width, modes[i].Height, modes[i].RefreshHz,
            IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    }

    pOutArgs->PreferredMonitorModeIdx = PanelOS::DefaultPreferredModeIndex();

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject,
                                            const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs,
                                            IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    //
    // Only reached when a monitor is created without a description. This driver
    // always supplies an EDID, so this is a safety net; answer with the same
    // default list so behaviour stays consistent either way.
    //
    UNREFERENCED_PARAMETER(MonitorObject);

    pOutArgs->DefaultMonitorModeBufferOutputCount = PanelOS::DefaultModeCount;

    if (pInArgs->DefaultMonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (pInArgs->DefaultMonitorModeBufferInputCount < PanelOS::DefaultModeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    const PANEL_DISPLAY_MODE* modes = PanelOS::DefaultModes();

    for (ULONG i = 0; i < PanelOS::DefaultModeCount; ++i)
    {
        pInArgs->pDefaultMonitorModes[i] = PanelCreateMonitorMode(
            modes[i].Width, modes[i].Height, modes[i].RefreshHz,
            IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    }

    pOutArgs->PreferredMonitorModeIdx = PanelOS::DefaultPreferredModeIndex();

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayMonitorQueryTargetModes(IDDCX_MONITOR MonitorObject,
                                             const IDARG_IN_QUERYTARGETMODES* pInArgs,
                                             IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(MonitorObject);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    const PanelOS::ModeTable& modes = wrapper->pContext->Modes();
    const ULONG modeCount = modes.Count();

    pOutArgs->TargetModeBufferOutputCount = modeCount;

    if (pInArgs->TargetModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }

    if (pInArgs->TargetModeBufferInputCount < modeCount)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    for (ULONG i = 0; i < modeCount; ++i)
    {
        const PANEL_DISPLAY_MODE& mode = modes.At(i);
        pInArgs->pTargetModes[i] = PanelCreateTargetMode(mode.Width, mode.Height, mode.RefreshHz);
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject,
                                            const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(MonitorObject);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        // IddCx treats any failed Assign status other than ABANDON as fatal on
        // current Windows versions. No ownership was accepted on this path.
        return STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN;
    }

    PANEL_TRACE_INFO("Monitor.AssignSwapChain",
        TraceLoggingPointer(MonitorObject, "monitor"),
        TraceLoggingUInt32(pInArgs->RenderAdapterLuid.LowPart, "renderAdapterLuidLow"));

    return wrapper->pContext->AssignSwapChain(pInArgs->hSwapChain,
                                              pInArgs->RenderAdapterLuid,
                                              pInArgs->hNextSurfaceAvailable);
}

_Use_decl_annotations_
NTSTATUS PanelDisplayMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
    auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(MonitorObject);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    PANEL_TRACE_INFO("Monitor.UnassignSwapChain", TraceLoggingPointer(MonitorObject, "monitor"));

    wrapper->pContext->UnassignSwapChain();

    return STATUS_SUCCESS;
}
