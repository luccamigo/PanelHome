/*++

Module Name:

    Device.cpp

Abstract:

    PanelDeviceContext implementation: IddCx adapter lifetime, the CONNECT /
    DISCONNECT / GET_STATUS control path, and mode commit handling.

    The defining behaviour of this driver lives here: InitAdapter() creates the
    adapter but no monitor, so the driver can sit installed and loaded with the
    virtual monitor absent until user mode explicitly asks for it.

--*/

#include "Device.h"
#include "Monitor.h"
#include "Edid.h"

namespace
{
    //
    // Only one virtual monitor is supported in v0.1, on connector index 0.
    //
    constexpr UINT PANEL_MAX_MONITORS = 1;
    constexpr UINT PANEL_CONNECTOR_INDEX = 0;
    constexpr ULONG PANEL_DEFAULT_CAPTURE_TARGET_FPS = 60;

    bool IsSupportedCaptureTargetFps(ULONG TargetFramesPerSecond)
    {
        return TargetFramesPerSecond == 30 ||
               TargetFramesPerSecond == 45 ||
               TargetFramesPerSecond == 60;
    }

    // Stable identity for the one physical/logical PanelOS monitor represented
    // by this driver. A new random container on every reconnect makes Windows
    // retain a different phantom monitor record for every lifecycle cycle.
    constexpr GUID PANEL_MONITOR_CONTAINER_ID =
        { 0x6ef2997b, 0xea58, 0x4cf8, { 0x8e, 0xf0, 0xa2, 0x5a, 0x2f, 0xaa, 0xd9, 0xb7 } };
}

PanelDeviceContext::PanelDeviceContext(_In_ WDFDEVICE WdfDevice)
    : m_WdfDevice(WdfDevice)
    , m_Adapter(nullptr)
    , m_Lock(nullptr)
    , m_CaptureTargetFramesPerSecond(PANEL_DEFAULT_CAPTURE_TARGET_FPS)
    , m_Monitor(nullptr)
    , m_MonitorContext(nullptr)
{
}

PanelDeviceContext::~PanelDeviceContext()
{
    //
    // m_Lock is a child of the WDFDEVICE and is destroyed with it; the monitor
    // object likewise belongs to the adapter. Nothing to free explicitly here.
    //
}

NTSTATUS PanelDeviceContext::Initialize()
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = m_WdfDevice;

    const NTSTATUS status = WdfWaitLockCreate(&attributes, &m_Lock);
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.WaitLockCreateFailed", TraceLoggingNTStatus(status, "status"));
    }

    return status;
}

NTSTATUS PanelDeviceContext::InitAdapter()
{
    IDDCX_ADAPTER_CAPS adapterCaps = {};
    adapterCaps.Size = sizeof(adapterCaps);

    //
    // Exactly one monitor. IddCx enforces this for us, which is a second line of
    // defence behind the state machine's "0 or 1" rule.
    //
    adapterCaps.MaxMonitorsSupported = PANEL_MAX_MONITORS;

    adapterCaps.EndPointDiagnostics.Size = sizeof(adapterCaps.EndPointDiagnostics);
    adapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;

    //
    // The eventual transport is USB to the tablet, but IddCx has no USB
    // transmission type; WIRED_OTHER is the honest choice for a link the OS
    // cannot characterise.
    //
    adapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;

    adapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"PanelOS Virtual Display";
    adapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"PanelOS";
    adapterCaps.EndPointDiagnostics.pEndPointModelName = L"PanelOS Tablet Display";

    IDDCX_ENDPOINT_VERSION version = {};
    version.Size = sizeof(version);
    version.MajorVer = 0;
    version.MinorVer = 1;

    adapterCaps.EndPointDiagnostics.pFirmwareVersion = &version;
    adapterCaps.EndPointDiagnostics.pHardwareVersion = &version;

    //
    // The adapter object carries a second reference to this same context so the
    // adapter-scoped IddCx callbacks can find it. Ownership stays with the
    // WDFDEVICE.
    //
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, PanelDeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT adapterInit = {};
    adapterInit.WdfDevice = m_WdfDevice;
    adapterInit.pCaps = &adapterCaps;
    adapterInit.ObjectAttributes = &attributes;

    IDARG_OUT_ADAPTER_INIT adapterInitOut = {};
    PANEL_TRACE_INFO0("Adapter.InitAsyncRequested");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] IddCxAdapterInitAsync requested\n");
    const NTSTATUS status = IddCxAdapterInitAsync(&adapterInit, &adapterInitOut);

    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Adapter.InitAsyncFailed", TraceLoggingNTStatus(status, "status"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] IddCxAdapterInitAsync failed 0x%08X\n", status);
        return status;
    }

    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(adapterInitOut.AdapterObject);
    wrapper->pContext = this;

    WdfWaitLockAcquire(m_Lock, nullptr);
    m_Adapter = adapterInitOut.AdapterObject;
    WdfWaitLockRelease(m_Lock);

    PANEL_TRACE_INFO("Adapter.InitStarted", TraceLoggingPointer(m_Adapter, "adapter"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] adapter init started (no monitor created)\n");

    return STATUS_SUCCESS;
}

void PanelDeviceContext::OnAdapterInitFinished(_In_ IDDCX_ADAPTER AdapterObject, _In_ NTSTATUS Status)
{
    PANEL_TRACE_INFO("Adapter.InitFinished",
        TraceLoggingPointer(AdapterObject, "adapter"),
        TraceLoggingNTStatus(Status, "status"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] AdapterInitFinished 0x%08X\n", Status);

    WdfWaitLockAcquire(m_Lock, nullptr);

    if (m_Adapter != AdapterObject)
    {
        WdfWaitLockRelease(m_Lock);
        PANEL_TRACE_INFO("Adapter.InitFinishedIgnoredForStoppedAdapter",
            TraceLoggingPointer(AdapterObject, "adapter"),
            TraceLoggingNTStatus(Status, "status"));
        return;
    }

    if (!NT_SUCCESS(Status))
    {
        m_Adapter = nullptr;
        m_State.OnDeviceStopped();
        WdfWaitLockRelease(m_Lock);

        PANEL_TRACE_ERROR("Adapter.InitFinishedFailed", TraceLoggingNTStatus(Status, "status"));

        // Adapter initialization completes asynchronously, after D0Entry has
        // returned. Report that failure to PnP explicitly instead of leaving a
        // started devnode whose control path can never become Ready.
        WdfDeviceSetFailed(m_WdfDevice, WdfDeviceFailedNoRestart);
        PANEL_TRACE_ERROR("Device.SetFailedAfterAdapterInit",
            TraceLoggingNTStatus(Status, "adapterInitStatus"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] adapter init failed; device marked failed\n");
        return;
    }

    m_State.OnAdapterReady();
    WdfWaitLockRelease(m_Lock);

    //
    // Deliberately no monitor is created here. This is the one place the
    // Microsoft IddSample would call FinishInit() and make a monitor appear on
    // its own; PanelDisplayDriver waits for IOCTL_PANEL_CONNECT_DISPLAY instead.
    //
    PANEL_TRACE_INFO0("Adapter.Ready");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] adapter ready, waiting for CONNECT\n");
}

void PanelDeviceContext::OnDeviceStopped()
{
    IDDCX_MONITOR monitor = nullptr;

    // Invalidate before joining the processor. A capture already in flight can
    // finish its CPU copy, but its stale generation can no longer be published.
    m_FrameStore.InvalidateAll();

    WdfWaitLockAcquire(m_Lock, nullptr);
    monitor = m_Monitor;
    m_Monitor = nullptr;
    m_MonitorContext = nullptr;
    m_Adapter = nullptr;
    m_State.OnDeviceStopped();
    WdfWaitLockRelease(m_Lock);

    if (monitor != nullptr)
    {
        PANEL_TRACE_INFO0("Device.StoppedWithMonitorConnected");
        (void)RemoveMonitor(monitor);
    }
}

NTSTATUS PanelDeviceContext::CreateAndAnnounceMonitor(_In_ const PanelOS::ModeTable& Modes,
                                                      _In_ ULONG TargetFramesPerSecond,
                                                      _Out_ IDDCX_MONITOR& MonitorObject,
                                                      _Out_ PanelMonitorContext*& MonitorContext)
{
    MonitorObject = nullptr;
    MonitorContext = nullptr;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, PanelMonitorContextWrapper);
    attributes.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(Object);
        if (wrapper != nullptr)
        {
            wrapper->Cleanup();
        }
    };

    IDDCX_MONITOR_INFO monitorInfo = {};
    monitorInfo.Size = sizeof(monitorInfo);
    monitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL;
    monitorInfo.ConnectorIndex = PANEL_CONNECTOR_INDEX;

    monitorInfo.MonitorDescription.Size = sizeof(monitorInfo.MonitorDescription);
    monitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    monitorInfo.MonitorDescription.DataSize = static_cast<UINT>(PanelOS::EdidBlockSize);
    monitorInfo.MonitorDescription.pData = const_cast<BYTE*>(PanelOS::PanelOsEdid);

    //
    // Reuse a stable container id across departures/arrivals. Microsoft
    // recommends a stable unique identity for a real monitor; it also prevents
    // ten connect cycles from producing ten distinct phantom monitor records.
    //
    monitorInfo.MonitorContainerId = PANEL_MONITOR_CONTAINER_ID;

    IDARG_IN_MONITORCREATE monitorCreate = {};
    monitorCreate.ObjectAttributes = &attributes;
    monitorCreate.pMonitorInfo = &monitorInfo;

    IDARG_OUT_MONITORCREATE monitorCreateOut = {};
    NTSTATUS status = IddCxMonitorCreate(m_Adapter, &monitorCreate, &monitorCreateOut);

    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Monitor.CreateFailed", TraceLoggingNTStatus(status, "status"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] IddCxMonitorCreate failed 0x%08X\n", status);
        return status;
    }

    PANEL_TRACE_INFO("Monitor.Created",
        TraceLoggingPointer(monitorCreateOut.MonitorObject, "monitor"),
        TraceLoggingUInt32(Modes.Count(), "modeCount"));

    auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(monitorCreateOut.MonitorObject);
    wrapper->pContext = new (std::nothrow) PanelMonitorContext(
        monitorCreateOut.MonitorObject, Modes, &m_FrameStore,
        TargetFramesPerSecond);

    if (wrapper->pContext == nullptr)
    {
        //
        // Ownership rule for IDDCX_MONITOR:
        //   created but never arrived -> we own it, destroy with WdfObjectDelete
        //   arrived                   -> IddCxMonitorDeparture destroys it, and
        //                                we must never delete it ourselves
        // This is the first case: arrival has not been attempted yet.
        //
        PANEL_TRACE_ERROR("Monitor.ContextAllocationFailed", TraceLoggingNTStatus(STATUS_INSUFFICIENT_RESOURCES, "status"));
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(monitorCreateOut.MonitorObject));
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    //
    // Announce arrival. Windows can call back synchronously from here
    // (QueryTargetModes, and later CommitModes), which is why the device wait
    // lock is not held across this call.
    //
    IDARG_OUT_MONITORARRIVAL arrivalOut = {};
    status = IddCxMonitorArrival(monitorCreateOut.MonitorObject, &arrivalOut);

    if (!NT_SUCCESS(status))
    {
        //
        // Arrival failed, so the monitor never became visible to the OS and no
        // departure is owed. It is still ours to destroy - see the ownership
        // rule above. A successful arrival is the point where that flips.
        //
        PANEL_TRACE_ERROR("Monitor.ArrivalFailed", TraceLoggingNTStatus(status, "status"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] IddCxMonitorArrival failed 0x%08X\n", status);
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(monitorCreateOut.MonitorObject));
        return status;
    }

    MonitorObject = monitorCreateOut.MonitorObject;
    MonitorContext = wrapper->pContext;

    PANEL_TRACE_INFO("Monitor.Arrived", TraceLoggingPointer(MonitorObject, "monitor"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] monitor arrival signalled\n");

    return STATUS_SUCCESS;
}

NTSTATUS PanelDeviceContext::RemoveMonitor(_In_ IDDCX_MONITOR MonitorObject)
{
    PANEL_TRACE_INFO("Monitor.DepartureBegin", TraceLoggingPointer(MonitorObject, "monitor"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] MonitorDeparture begin\n");

    //
    // Stop swap chain processing BEFORE departure.
    //
    // IDDCX_SWAPCHAIN is a child of IDDCX_MONITOR, and departure destroys the
    // monitor - which would take the swap chain with it while our processing
    // thread might still be inside IddCxSwapChainReleaseAndAcquireBuffer.
    // Tearing the swap chain down first joins that thread while every object it
    // touches is still alive.
    //
    // This is idempotent: if IddCx also calls EvtIddCxMonitorUnassignSwapChain
    // as part of the departure, the second teardown is a no-op.
    //
    auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(MonitorObject);
    if (wrapper != nullptr && wrapper->pContext != nullptr)
    {
        wrapper->pContext->BeginDeparture();
    }

    const NTSTATUS status = IddCxMonitorDeparture(MonitorObject);

    // Do not dereference MonitorObject beyond this point. On success the call
    // destroyed it, and the contract does not grant reusable ownership on an
    // error path either.
    PANEL_TRACE_INFO("Monitor.DepartureReturned", TraceLoggingNTStatus(status, "status"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] MonitorDeparture returned 0x%08X\n", status);

    //
    // MonitorObject is destroyed by the call above - "This call causes the
    // IDDCX_MONITOR object to be destroyed" (IddCx Objects, Microsoft docs).
    // From here on the handle is dead: it must not be dereferenced, passed to
    // WdfObjectDelete, or reused for a later connect.
    //
    // Calling WdfObjectDelete here is what crashed WUDFHost with an access
    // violation inside ntdll (WUDFUnhandledException, c0000005), because it
    // freed an object IddCx had already freed.
    //
    if (!NT_SUCCESS(status))
    {
        //
        // Deliberately no fallback cleanup: whether the object survived a failed
        // departure is not documented, so touching it again risks the same
        // double free. It is parented to the adapter and will be reclaimed when
        // the device stops.
        //
        PANEL_TRACE_ERROR("Monitor.DepartureFailed", TraceLoggingNTStatus(status, "status"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] IddCxMonitorDeparture failed 0x%08X\n", status);
        return status;
    }

    PANEL_TRACE_INFO0("Monitor.Departed");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] monitor removed\n");
    return STATUS_SUCCESS;
}

void PanelDeviceContext::HandleConnect(_In_opt_ const PANEL_CONNECT_INPUT* Input,
                                       _Out_ PANEL_COMMAND_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);

    PANEL_COMMAND_RESULT result = PanelCommandResultFailed;
    PanelOS::ModeTable modesSnapshot;
    ULONG captureTargetFramesPerSecond = PANEL_DEFAULT_CAPTURE_TARGET_FPS;
    bool shouldCreate = false;

    WdfWaitLockAcquire(m_Lock, nullptr);

    //
    // Apply a host-supplied preferred mode before deciding to connect, so an
    // invalid mode is rejected without disturbing the current state.
    //
    if (Input != nullptr && (Input->Flags & PANEL_CONNECT_FLAG_USE_PREFERRED_MODE) != 0)
    {
        if (!m_Modes.SetPreferredMode(Input->PreferredMode))
        {
            PANEL_TRACE_ERROR("Connect.InvalidMode",
                TraceLoggingUInt32(Input->PreferredMode.Width, "width"),
                TraceLoggingUInt32(Input->PreferredMode.Height, "height"),
                TraceLoggingUInt32(Input->PreferredMode.RefreshHz, "refreshHz"));

            Output.Result = PanelCommandResultInvalidMode;
            Output.MonitorState = m_State.MonitorState();
            WdfWaitLockRelease(m_Lock);
            return;
        }
    }

    shouldCreate = m_State.BeginConnect(result);

    if (shouldCreate)
    {
        modesSnapshot = m_Modes;
        captureTargetFramesPerSecond = m_CaptureTargetFramesPerSecond;
    }

    WdfWaitLockRelease(m_Lock);

    if (!shouldCreate)
    {
        //
        // Idempotent path: already connected, or the adapter is not ready.
        //
        PANEL_TRACE_INFO("Connect.NoOp", TraceLoggingInt32(static_cast<INT32>(result), "result"));

        WdfWaitLockAcquire(m_Lock, nullptr);
        Output.Result = result;
        Output.MonitorState = m_State.MonitorState();
        WdfWaitLockRelease(m_Lock);
        return;
    }

    PANEL_TRACE_INFO("Connect.Requested",
        TraceLoggingUInt32(modesSnapshot.Count(), "modeCount"),
        TraceLoggingUInt32(captureTargetFramesPerSecond, "captureTargetFps"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoTarget] CONNECT target=%lu\n",
        captureTargetFramesPerSecond);

    IDDCX_MONITOR monitor = nullptr;
    PanelMonitorContext* monitorContext = nullptr;
    const NTSTATUS status = CreateAndAnnounceMonitor(
        modesSnapshot,
        captureTargetFramesPerSecond,
        monitor,
        monitorContext);

    WdfWaitLockAcquire(m_Lock, nullptr);

    if (NT_SUCCESS(status))
    {
        m_Monitor = monitor;
        m_MonitorContext = monitorContext;
    }

    m_State.CompleteConnect(NT_SUCCESS(status), result);

    Output.Result = result;
    Output.MonitorState = m_State.MonitorState();

    WdfWaitLockRelease(m_Lock);
}

void PanelDeviceContext::HandleDisconnect(_Out_ PANEL_COMMAND_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);

    PANEL_COMMAND_RESULT result = PanelCommandResultFailed;
    IDDCX_MONITOR monitor = nullptr;

    WdfWaitLockAcquire(m_Lock, nullptr);

    const bool shouldRemove = m_State.BeginDisconnect(result);

    if (shouldRemove)
    {
        //
        // Detach before releasing the lock: from here on a status read reports
        // from the state machine only and can never reach the dying context.
        //
        monitor = m_Monitor;
        m_Monitor = nullptr;
        m_MonitorContext = nullptr;
    }

    WdfWaitLockRelease(m_Lock);

    if (!shouldRemove)
    {
        PANEL_TRACE_INFO("Disconnect.NoOp", TraceLoggingInt32(static_cast<INT32>(result), "result"));

        WdfWaitLockAcquire(m_Lock, nullptr);
        Output.Result = result;
        Output.MonitorState = m_State.MonitorState();
        WdfWaitLockRelease(m_Lock);
        return;
    }

    PANEL_TRACE_INFO0("Disconnect.Requested");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] DISCONNECT requested\n");

    // Stop exposing pixels immediately. The validated monitor-departure path
    // below remains unchanged and will join the swapchain worker as before.
    m_FrameStore.InvalidateAll();

    const NTSTATUS departureStatus = monitor != nullptr
        ? RemoveMonitor(monitor)
        : STATUS_INVALID_DEVICE_STATE;

    WdfWaitLockAcquire(m_Lock, nullptr);
    m_State.CompleteDisconnect(NT_SUCCESS(departureStatus), result);
    Output.Result = result;
    Output.MonitorState = m_State.MonitorState();
    WdfWaitLockRelease(m_Lock);

    PANEL_TRACE_INFO("Disconnect.MonitorStateCleared",
        TraceLoggingNTStatus(departureStatus, "departureStatus"),
        TraceLoggingInt32(static_cast<INT32>(result), "result"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] monitor state cleared (departure 0x%08X)\n",
                      departureStatus);

    if (!NT_SUCCESS(departureStatus))
    {
        // The old monitor handle is intentionally gone from driver state, but a
        // failed departure does not guarantee that the OS destroyed the child.
        // Restarting the device tears down the adapter before any new connect is
        // accepted, preventing a duplicate monitor from being announced.
        PANEL_TRACE_ERROR("Device.RestartRequestedAfterDepartureFailure",
            TraceLoggingNTStatus(departureStatus, "departureStatus"));
        WdfDeviceSetFailed(m_WdfDevice, WdfDeviceFailedAttemptRestart);
    }
}

void PanelDeviceContext::HandleGetStatus(_Out_ PANEL_STATUS_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;

    WdfWaitLockAcquire(m_Lock, nullptr);

    Output.DriverState = m_State.DriverState();
    Output.MonitorState = m_State.MonitorState();
    Output.ConnectCount = m_State.ConnectCount();

    Output.ModeCount = m_Modes.Count();
    for (ULONG i = 0; i < Output.ModeCount && i < PANEL_MAX_REPORTED_MODES; ++i)
    {
        Output.Modes[i] = m_Modes.At(i);
    }

    if (m_MonitorContext != nullptr)
    {
        Output.ActiveMode = m_MonitorContext->ActiveMode();
        Output.FramesProcessed = m_MonitorContext->FramesProcessed();
    }

    WdfWaitLockRelease(m_Lock);
}

void PanelDeviceContext::HandleGetFrameInfo(_In_ const PANEL_FRAME_QUERY_INPUT& Input,
                                            _Out_ PANEL_FRAME_OUTPUT& Output)
{
    (void)m_FrameStore.GetFrameInfo(Input.LastSequence, Output);
}

PANEL_FRAME_RESULT PanelDeviceContext::HandleReadLatestFrame(
    _In_ const PANEL_FRAME_READ_INPUT& Input,
    _Out_ PANEL_FRAME_OUTPUT& Output,
    _Out_ std::shared_ptr<const PanelOS::LatestFrameSnapshot>& Snapshot)
{
    return m_FrameStore.ReadFrame(Input.Generation, Input.Sequence, Output, Snapshot);
}

void PanelDeviceContext::HandleGetCaptureMetrics(
    _Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;

    WdfWaitLockAcquire(m_Lock, nullptr);
    Output.TargetFramesPerSecond = m_CaptureTargetFramesPerSecond;
    if (m_MonitorContext != nullptr)
    {
        m_MonitorContext->GetCaptureMetrics(Output);
    }
    WdfWaitLockRelease(m_Lock);
}

void PanelDeviceContext::HandleGetCapturePacingMetrics(
    _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;
    Output.HistogramBucketCount =
        PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS;

    WdfWaitLockAcquire(m_Lock, nullptr);
    Output.TargetFramesPerSecond = m_CaptureTargetFramesPerSecond;
    if (m_MonitorContext != nullptr)
    {
        m_MonitorContext->GetCapturePacingMetrics(Output);
    }
    WdfWaitLockRelease(m_Lock);
}

bool PanelDeviceContext::HandleSetCaptureConfig(
    _In_ const PANEL_CAPTURE_CONFIG_INPUT& Input,
    _Out_ PANEL_CAPTURE_CONFIG_OUTPUT& Output)
{
    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;

    PANEL_TRACE_INFO("VideoTarget.IoctlReceived",
        TraceLoggingUInt32(Input.TargetFramesPerSecond, "targetFps"),
        TraceLoggingUInt32(PANEL_DISPLAY_PROTOCOL_VERSION, "protocolVersion"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoTarget] Driver IOCTL received=%lu protocol=v%lu\n",
        Input.TargetFramesPerSecond,
        static_cast<ULONG>(PANEL_DISPLAY_PROTOCOL_VERSION));

    if (!IsSupportedCaptureTargetFps(Input.TargetFramesPerSecond))
    {
        PANEL_TRACE_ERROR("VideoTarget.Unsupported",
            TraceLoggingUInt32(Input.TargetFramesPerSecond, "targetFps"));
        return false;
    }

    WdfWaitLockAcquire(m_Lock, nullptr);
    m_CaptureTargetFramesPerSecond = Input.TargetFramesPerSecond;
    if (m_MonitorContext != nullptr)
    {
        m_MonitorContext->SetCaptureTargetFps(Input.TargetFramesPerSecond);
    }
    Output.TargetFramesPerSecond = m_CaptureTargetFramesPerSecond;
    WdfWaitLockRelease(m_Lock);

    PANEL_TRACE_INFO("VideoTarget.Stored",
        TraceLoggingUInt32(Output.TargetFramesPerSecond, "targetFps"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoTarget] configuredTarget=%lu\n",
        Output.TargetFramesPerSecond);
    return true;
}

// ---------------------------------------------------------------------------
// IddCx adapter callbacks
// ---------------------------------------------------------------------------

_Use_decl_annotations_
NTSTATUS PanelDisplayAdapterInitFinished(IDDCX_ADAPTER AdapterObject,
                                         const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(AdapterObject);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    wrapper->pContext->OnAdapterInitFinished(AdapterObject, pInArgs->AdapterInitStatus);

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayAdapterCommitModes(IDDCX_ADAPTER AdapterObject,
                                        const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);

    //
    // Windows tells us which paths are now active. Record the mode on the
    // monitor so status can report it; there is no hardware to program.
    //
    for (UINT i = 0; i < pInArgs->PathCount; ++i)
    {
        const IDDCX_PATH& path = pInArgs->pPaths[i];

        auto* wrapper = WdfObjectGet_PanelMonitorContextWrapper(path.MonitorObject);
        if (wrapper == nullptr || wrapper->pContext == nullptr)
        {
            continue;
        }

        const bool active = (path.Flags & IDDCX_PATH_FLAGS_ACTIVE) != 0;
        const PANEL_DISPLAY_MODE mode = PanelSignalInfoToMode(path.TargetVideoSignalInfo);

        wrapper->pContext->SetActiveMode(mode, active);

        if (active)
        {
            //
            // A resolution outside our advertised list would mean the mode
            // reporting callbacks and the mode table have drifted apart. It is
            // not fatal - Windows chose it from what we reported - but it is
            // worth knowing about.
            //
            if (!wrapper->pContext->Modes().ContainsResolution(mode.Width, mode.Height))
            {
                PANEL_TRACE_ERROR("CommitModes.UnadvertisedResolution",
                    TraceLoggingUInt32(mode.Width, "width"),
                    TraceLoggingUInt32(mode.Height, "height"));
            }

            PANEL_TRACE_INFO("CommitModes.ModeSelected",
                TraceLoggingUInt32(mode.Width, "width"),
                TraceLoggingUInt32(mode.Height, "height"),
                TraceLoggingUInt32(mode.RefreshHz, "refreshHz"));

            PANEL_DEBUG_PRINT("[PanelDisplayDriver] mode selected %ux%u@%uHz\n",
                              mode.Width, mode.Height, mode.RefreshHz);
        }
        else
        {
            PANEL_TRACE_INFO0("CommitModes.PathInactive");
        }
    }

    return STATUS_SUCCESS;
}

// ---------------------------------------------------------------------------
// Control path
// ---------------------------------------------------------------------------

_Use_decl_annotations_
void PanelDisplayDeviceIoControl(WDFDEVICE Device,
                                 WDFREQUEST Request,
                                 size_t OutputBufferLength,
                                 size_t InputBufferLength,
                                 ULONG IoControlCode)
{
    //
    // IddCx forwards here only the control codes it does not handle itself, so
    // anything arriving is either one of ours or genuinely unknown.
    //
    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(Device);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        WdfRequestComplete(Request, STATUS_INVALID_DEVICE_STATE);
        return;
    }

    PanelDeviceContext* context = wrapper->pContext;

    NTSTATUS status = STATUS_INVALID_DEVICE_REQUEST;
    size_t bytesReturned = 0;

    switch (IoControlCode)
    {
    case IOCTL_PANEL_CONNECT_DISPLAY:
    {
        if (OutputBufferLength < sizeof(PANEL_COMMAND_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        //
        // The input buffer is optional: no buffer means "connect with driver
        // defaults". A buffer that is present must be complete and self
        // consistent, otherwise we would act on a partially written request.
        //
        const PANEL_CONNECT_INPUT* input = nullptr;

        if (InputBufferLength > 0)
        {
            if (InputBufferLength < sizeof(PANEL_CONNECT_INPUT))
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }

            PVOID inputBuffer = nullptr;
            status = WdfRequestRetrieveInputBuffer(Request, sizeof(PANEL_CONNECT_INPUT), &inputBuffer, nullptr);
            if (!NT_SUCCESS(status))
            {
                break;
            }

            input = static_cast<const PANEL_CONNECT_INPUT*>(inputBuffer);

            if (input->Size != sizeof(PANEL_CONNECT_INPUT))
            {
                status = STATUS_INVALID_PARAMETER;
                break;
            }
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(PANEL_COMMAND_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleConnect(input, *static_cast<PANEL_COMMAND_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_COMMAND_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_DISCONNECT_DISPLAY:
    {
        if (OutputBufferLength < sizeof(PANEL_COMMAND_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(PANEL_COMMAND_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleDisconnect(*static_cast<PANEL_COMMAND_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_COMMAND_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_GET_STATUS:
    {
        if (OutputBufferLength < sizeof(PANEL_STATUS_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(Request, sizeof(PANEL_STATUS_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleGetStatus(*static_cast<PANEL_STATUS_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_STATUS_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_GET_FRAME_INFO:
    {
        if (InputBufferLength < sizeof(PANEL_FRAME_QUERY_INPUT) ||
            OutputBufferLength < sizeof(PANEL_FRAME_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID inputBuffer = nullptr;
        status = WdfRequestRetrieveInputBuffer(
            Request, sizeof(PANEL_FRAME_QUERY_INPUT), &inputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        const auto input = *static_cast<const PANEL_FRAME_QUERY_INPUT*>(inputBuffer);
        if (input.Size != sizeof(input) || input.Reserved != 0)
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(
            Request, sizeof(PANEL_FRAME_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleGetFrameInfo(input, *static_cast<PANEL_FRAME_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_FRAME_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_READ_LATEST_FRAME:
    {
        if (InputBufferLength < sizeof(PANEL_FRAME_READ_INPUT) ||
            OutputBufferLength < sizeof(PANEL_FRAME_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID inputBuffer = nullptr;
        status = WdfRequestRetrieveInputBuffer(
            Request, sizeof(PANEL_FRAME_READ_INPUT), &inputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        // METHOD_OUT_DIRECT has distinct input and output storage, but retain a
        // local copy before mapping the caller's output pages regardless.
        const auto input = *static_cast<const PANEL_FRAME_READ_INPUT*>(inputBuffer);
        if (input.Size != sizeof(input) || input.Reserved != 0)
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        PANEL_FRAME_OUTPUT output = {};
        std::shared_ptr<const PanelOS::LatestFrameSnapshot> snapshot;
        const PANEL_FRAME_RESULT result =
            context->HandleReadLatestFrame(input, output, snapshot);

        size_t requiredBytes = sizeof(PANEL_FRAME_OUTPUT);
        if (result == PanelFrameResultOk)
        {
            if (!snapshot ||
                snapshot->Pixels.size() != output.PayloadBytes ||
                snapshot->Pixels.size() > (SIZE_MAX - requiredBytes))
            {
                status = STATUS_INVALID_BUFFER_SIZE;
                break;
            }

            requiredBytes += snapshot->Pixels.size();
            if (OutputBufferLength < requiredBytes)
            {
                status = STATUS_BUFFER_TOO_SMALL;
                break;
            }
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(Request, requiredBytes, &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        RtlCopyMemory(outputBuffer, &output, sizeof(output));
        if (result == PanelFrameResultOk && output.PayloadBytes != 0)
        {
            RtlCopyMemory(static_cast<BYTE*>(outputBuffer) + sizeof(output),
                          snapshot->Pixels.data(), snapshot->Pixels.size());
        }

        bytesReturned = requiredBytes;
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_GET_CAPTURE_METRICS:
    {
        if (OutputBufferLength < sizeof(PANEL_CAPTURE_METRICS_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(
            Request, sizeof(PANEL_CAPTURE_METRICS_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleGetCaptureMetrics(
            *static_cast<PANEL_CAPTURE_METRICS_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_CAPTURE_METRICS_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_SET_CAPTURE_CONFIG:
    {
        if (InputBufferLength < sizeof(PANEL_CAPTURE_CONFIG_INPUT) ||
            OutputBufferLength < sizeof(PANEL_CAPTURE_CONFIG_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID inputBuffer = nullptr;
        status = WdfRequestRetrieveInputBuffer(
            Request, sizeof(PANEL_CAPTURE_CONFIG_INPUT), &inputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        const auto input =
            *static_cast<const PANEL_CAPTURE_CONFIG_INPUT*>(inputBuffer);
        if (input.Size != sizeof(input))
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(
            Request, sizeof(PANEL_CAPTURE_CONFIG_OUTPUT), &outputBuffer, nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        if (!context->HandleSetCaptureConfig(
                input,
                *static_cast<PANEL_CAPTURE_CONFIG_OUTPUT*>(outputBuffer)))
        {
            status = STATUS_INVALID_PARAMETER;
            break;
        }

        bytesReturned = sizeof(PANEL_CAPTURE_CONFIG_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    case IOCTL_PANEL_GET_CAPTURE_PACING_METRICS:
    {
        if (OutputBufferLength <
            sizeof(PANEL_CAPTURE_PACING_METRICS_OUTPUT))
        {
            status = STATUS_BUFFER_TOO_SMALL;
            break;
        }

        PVOID outputBuffer = nullptr;
        status = WdfRequestRetrieveOutputBuffer(
            Request,
            sizeof(PANEL_CAPTURE_PACING_METRICS_OUTPUT),
            &outputBuffer,
            nullptr);
        if (!NT_SUCCESS(status))
        {
            break;
        }

        context->HandleGetCapturePacingMetrics(
            *static_cast<PANEL_CAPTURE_PACING_METRICS_OUTPUT*>(outputBuffer));
        bytesReturned = sizeof(PANEL_CAPTURE_PACING_METRICS_OUTPUT);
        status = STATUS_SUCCESS;
        break;
    }

    default:
        PANEL_TRACE_ERROR("Ioctl.Unknown", TraceLoggingUInt32(IoControlCode, "ioControlCode"));
        status = STATUS_INVALID_DEVICE_REQUEST;
        break;
    }

    if (NT_SUCCESS(status))
    {
        WdfRequestCompleteWithInformation(Request, status, bytesReturned);
    }
    else
    {
        WdfRequestComplete(Request, status);
    }
}
