/*++

Module Name:

    Driver.cpp

Abstract:

    WDF driver and device entry points for PanelDisplayDriver.

    DriverEntry -> PanelDisplayDeviceAdd -> PanelDisplayDeviceD0Entry -> adapter.
    No virtual monitor is created anywhere along that path: the device comes up
    with the adapter present and zero monitors, and stays that way until user
    mode sends IOCTL_PANEL_CONNECT_DISPLAY (see Device.cpp).

--*/

//
// initguid.h must precede the first inclusion of Public.h so this translation
// unit is the one that defines GUID_DEVINTERFACE_PANEL_DISPLAY.
//
#include <initguid.h>

#include "Driver.h"
#include "Device.h"

namespace
{
    void PanelDriverCleanup(_In_ WDFOBJECT /*DriverObject*/)
    {
        PANEL_TRACE_INFO0("Driver.Unloading");
        PanelTraceUnregister();
    }

    void PanelDeviceCleanup(_In_ WDFOBJECT Object)
    {
        auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(Object);
        if (wrapper != nullptr)
        {
            wrapper->Cleanup();
        }
    }
}

//
// Annotations come from the DRIVER_INITIALIZE declaration in Driver.h; repeating
// them here would be flagged as an inconsistent annotation by code analysis.
//
extern "C" NTSTATUS DriverEntry(PDRIVER_OBJECT pDriverObject, PUNICODE_STRING pRegistryPath)
{
    PanelTraceRegister();
    PANEL_TRACE_INFO0("Driver.EntryBegin");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] DriverEntry begin\n");

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.EvtCleanupCallback = PanelDriverCleanup;

    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, PanelDisplayDeviceAdd);

    const NTSTATUS status = WdfDriverCreate(pDriverObject, pRegistryPath, &attributes, &config, WDF_NO_HANDLE);

    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Driver.CreateFailed", TraceLoggingNTStatus(status, "status"));
        PanelTraceUnregister();
        return status;
    }

    PANEL_TRACE_INFO("Driver.Loaded", TraceLoggingUInt32(PANEL_DISPLAY_PROTOCOL_VERSION, "protocolVersion"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] driver loaded, protocol v%u\n", PANEL_DISPLAY_PROTOCOL_VERSION);

    return status;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
    UNREFERENCED_PARAMETER(Driver);

    PANEL_TRACE_INFO0("Device.AddBegin");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] EvtDeviceAdd begin\n");

    WDF_PNPPOWER_EVENT_CALLBACKS pnpPowerCallbacks;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&pnpPowerCallbacks);
    pnpPowerCallbacks.EvtDeviceD0Entry = PanelDisplayDeviceD0Entry;
    pnpPowerCallbacks.EvtDeviceD0Exit = PanelDisplayDeviceD0Exit;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &pnpPowerCallbacks);

    IDD_CX_CLIENT_CONFIG iddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&iddConfig);

    //
    // EvtIddCxDeviceIoControl is the documented channel for a companion
    // application to control the driver; it receives every control code IddCx
    // does not consume itself. This is how PanelDisplayCtl - and later
    // PanelHost - drives connect/disconnect.
    //
    iddConfig.EvtIddCxDeviceIoControl = PanelDisplayDeviceIoControl;

    iddConfig.EvtIddCxAdapterInitFinished = PanelDisplayAdapterInitFinished;
    iddConfig.EvtIddCxAdapterCommitModes = PanelDisplayAdapterCommitModes;

    iddConfig.EvtIddCxParseMonitorDescription = PanelDisplayParseMonitorDescription;
    iddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = PanelDisplayMonitorGetDefaultModes;
    iddConfig.EvtIddCxMonitorQueryTargetModes = PanelDisplayMonitorQueryTargetModes;
    iddConfig.EvtIddCxMonitorAssignSwapChain = PanelDisplayMonitorAssignSwapChain;
    iddConfig.EvtIddCxMonitorUnassignSwapChain = PanelDisplayMonitorUnassignSwapChain;

    NTSTATUS status = IddCxDeviceInitConfig(pDeviceInit, &iddConfig);
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.IddCxDeviceInitConfigFailed", TraceLoggingNTStatus(status, "status"));
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, PanelDeviceContextWrapper);
    attributes.EvtCleanupCallback = PanelDeviceCleanup;

    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&pDeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.CreateFailed", TraceLoggingNTStatus(status, "status"));
        return status;
    }

    status = IddCxDeviceInitialize(device);
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.IddCxDeviceInitializeFailed", TraceLoggingNTStatus(status, "status"));
        return status;
    }

    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(device);
    wrapper->pContext = new (std::nothrow) PanelDeviceContext(device);

    if (wrapper->pContext == nullptr)
    {
        PANEL_TRACE_ERROR("Device.ContextAllocationFailed",
            TraceLoggingNTStatus(STATUS_INSUFFICIENT_RESOURCES, "status"));
        return STATUS_INSUFFICIENT_RESOURCES;
    }

    status = wrapper->pContext->Initialize();
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    //
    // Publish the control interface. Without it PanelDisplayCtl has no path to
    // open, and the driver would be permanently stuck with no monitor.
    //
    status = WdfDeviceCreateDeviceInterface(device, &GUID_DEVINTERFACE_PANEL_DISPLAY, nullptr);
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.CreateDeviceInterfaceFailed", TraceLoggingNTStatus(status, "status"));
        return status;
    }

    PANEL_TRACE_INFO0("Device.AddSuccess");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] EvtDeviceAdd success, control interface published\n");

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);

    PANEL_TRACE_INFO0("Device.D0EntryBegin");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] D0Entry begin\n");

    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(Device);
    if (wrapper == nullptr || wrapper->pContext == nullptr)
    {
        return STATUS_INVALID_DEVICE_STATE;
    }

    //
    // Bring up the IddCx adapter only. The Microsoft IddSample also creates its
    // monitors from here; PanelDisplayDriver deliberately does not, because the
    // whole point of v0.1 is that the monitor appears and disappears on command.
    //
    const NTSTATUS status = wrapper->pContext->InitAdapter();
    if (!NT_SUCCESS(status))
    {
        PANEL_TRACE_ERROR("Device.D0EntryFailed", TraceLoggingNTStatus(status, "status"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] D0Entry failed 0x%08X\n", status);
        return status;
    }

    PANEL_TRACE_INFO0("Device.D0EntrySuccess");
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] D0Entry success\n");
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS PanelDisplayDeviceD0Exit(WDFDEVICE Device, WDF_POWER_DEVICE_STATE TargetState)
{
    UNREFERENCED_PARAMETER(TargetState);

    auto* wrapper = WdfObjectGet_PanelDeviceContextWrapper(Device);
    if (wrapper != nullptr && wrapper->pContext != nullptr)
    {
        //
        // Drop the monitor cleanly rather than letting the adapter teardown take
        // it with us; that keeps Windows' view of the topology consistent if the
        // device is only power cycled.
        //
        wrapper->pContext->OnDeviceStopped();
    }

    return STATUS_SUCCESS;
}
