/*++

Module Name:

    Public.h

Abstract:

    Public control contract for PanelDisplayDriver.

    This header is the single source of truth shared by the driver, by
    PanelDisplayCtl.exe and - later - by PanelHost. It depends only on basic
    Windows types, and is self-sufficient: including it alone is enough.

    Nothing in here may depend on WDF, IddCx or D3D.

--*/

#pragma once

//
// CTL_CODE is our proxy for "the ambient Windows headers are already in scope".
// In the driver it arrives via <wudfwdm.h>, which must not be mixed with the
// user-mode <winioctl.h>; in user mode nothing has usually been included yet.
// Note that <windows.h> only pulls in <winioctl.h> when WIN32_LEAN_AND_MEAN is
// undefined, so the explicit include below is load bearing.
//
#ifndef CTL_CODE
#include <windows.h>
#include <winioctl.h>
#endif

//
// Version of the control contract itself. Bump whenever the layout of any
// structure below changes; PanelDisplayCtl and PanelHost compare this against
// PANEL_DISPLAY_PROTOCOL_VERSION reported by the running driver.
//
// v2: IOCTL access bits tightened from FILE_ANY_ACCESS to READ/WRITE. The access
//     field is part of the control code, so the numeric IOCTL values changed and
//     driver and clients must be rebuilt together.
//
// v3: added latest-frame discovery and direct-read IOCTLs. The existing monitor
//     lifecycle IOCTL values and structures are unchanged.
//
// v4: added read-only cumulative capture-stage metrics. No existing IOCTL or
//     structure layout changed.
//
// v5: added configurable 30/45/60 FPS capture pacing and exposed the active
//     target in capture metrics.
#define PANEL_DISPLAY_PROTOCOL_VERSION  5

//
// Device interface exposed by the driver. User mode enumerates this interface
// to find the device path to open.
//
// {2F21C12F-51EC-48CB-93D1-776DC7468B1B}
//
DEFINE_GUID(GUID_DEVINTERFACE_PANEL_DISPLAY,
    0x2f21c12f, 0x51ec, 0x48cb, 0x93, 0xd1, 0x77, 0x6d, 0xc7, 0x46, 0x8b, 0x1b);

//
// IOCTL codes.
//
// FILE_DEVICE_UNKNOWN plus function codes in the 0x900 range keeps us clear of
// the codes IddCx handles internally before it forwards unrecognised requests to
// EvtIddCxDeviceIoControl.
//
// Access bits
// -----------
// The access field of CTL_CODE is enforced by the I/O manager against the access
// the caller's handle was actually granted, so it is a real privilege boundary
// rather than documentation:
//
//   FILE_READ_ACCESS   requires FILE_READ_DATA  on the handle (via GENERIC_READ)
//   FILE_WRITE_ACCESS  requires FILE_WRITE_DATA on the handle (via GENERIC_WRITE)
//   FILE_ANY_ACCESS    requires nothing at all
//
// GET_STATUS only reads state, so it takes FILE_READ_ACCESS. CONNECT and
// DISCONNECT change what monitors the OS sees, so they take FILE_WRITE_ACCESS
// and cannot be issued through a read-only handle. Nothing here is
// FILE_ANY_ACCESS: that would let any handle that could be opened at all drive
// monitor creation, which is exactly the check worth keeping.
//
#define PANEL_IOCTL_BASE  0x900

#define IOCTL_PANEL_CONNECT_DISPLAY \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 0, METHOD_BUFFERED, FILE_WRITE_ACCESS)

#define IOCTL_PANEL_DISCONNECT_DISPLAY \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 1, METHOD_BUFFERED, FILE_WRITE_ACCESS)

#define IOCTL_PANEL_GET_STATUS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 2, METHOD_BUFFERED, FILE_READ_ACCESS)

// Small, buffered size/sequence negotiation request. Pixel payloads are never
// returned through this IOCTL.
#define IOCTL_PANEL_GET_FRAME_INFO \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 3, METHOD_BUFFERED, FILE_READ_ACCESS)

// Returns a fixed header followed by the captured pixels. METHOD_OUT_DIRECT is
// intentional: WDF maps the caller's output pages for the large payload instead
// of copying the complete frame through the buffered-I/O system buffer.
#define IOCTL_PANEL_READ_LATEST_FRAME \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 4, METHOD_OUT_DIRECT, FILE_READ_ACCESS)

// Small cumulative diagnostic snapshot. PanelHost samples this at multi-second
// intervals; it is never part of the per-frame capture path.
#define IOCTL_PANEL_GET_CAPTURE_METRICS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 5, METHOD_BUFFERED, FILE_READ_ACCESS)

// Changes capture pacing without recreating the monitor or swapchain. The
// driver accepts only the explicitly supported interactive-video targets.
#define IOCTL_PANEL_SET_CAPTURE_CONFIG \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 6, METHOD_BUFFERED, FILE_WRITE_ACCESS)

// Supplemental v5 pacing diagnostics. This leaves every existing v5 layout
// unchanged while allowing a rebuilt Host to require explicit deadline data;
// an older v5 driver fails this IOCTL instead of silently fabricating metrics.
#define IOCTL_PANEL_GET_CAPTURE_PACING_METRICS \
    CTL_CODE(FILE_DEVICE_UNKNOWN, PANEL_IOCTL_BASE + 7, METHOD_BUFFERED, FILE_READ_ACCESS)

//
// Maximum number of modes the driver will ever report through IOCTL_PANEL_GET_STATUS.
// Keeps PANEL_STATUS_OUTPUT a fixed-size structure.
//
#define PANEL_MAX_REPORTED_MODES  8

//
// Lifecycle state of the driver as a whole. The driver is Ready once the IddCx
// adapter has finished initialising; only then can a monitor be created.
//
typedef enum _PANEL_DRIVER_STATE {
    PanelDriverStateInitializing = 0,
    PanelDriverStateReady        = 1,
    PanelDriverStateStopped      = 2,
} PANEL_DRIVER_STATE;

//
// Lifecycle state of the single virtual monitor. This driver deliberately
// supports 0 or 1 monitor - never more.
//
typedef enum _PANEL_MONITOR_STATE {
    PanelMonitorStateDisconnected = 0,
    PanelMonitorStateConnected    = 1,
} PANEL_MONITOR_STATE;

//
// Result of a CONNECT/DISCONNECT command. Both commands are idempotent: issuing
// them in the state they would produce is reported as AlreadyConnected /
// AlreadyDisconnected and is not an error.
//
typedef enum _PANEL_COMMAND_RESULT {
    PanelCommandResultOk                   = 0,
    PanelCommandResultAlreadyConnected     = 1,
    PanelCommandResultAlreadyDisconnected  = 2,
    PanelCommandResultAdapterNotReady      = 3,
    PanelCommandResultInvalidMode          = 4,
    PanelCommandResultFailed               = 5,
} PANEL_COMMAND_RESULT;

//
// A single video mode.
//
typedef struct _PANEL_DISPLAY_MODE {
    ULONG Width;
    ULONG Height;
    ULONG RefreshHz;
} PANEL_DISPLAY_MODE, *PPANEL_DISPLAY_MODE;

//
// Flags for PANEL_CONNECT_INPUT.
//
// PANEL_CONNECT_FLAG_USE_PREFERRED_MODE is the forward-looking hook for
// PanelHost: it will pass the tablet's real panel geometry here instead of
// letting the driver pick its built-in default. The mode still has to pass
// validation (see PanelDisplayModes.h) - the driver never blindly trusts it.
//
#define PANEL_CONNECT_FLAG_USE_PREFERRED_MODE  0x00000001u

//
// Optional input buffer for IOCTL_PANEL_CONNECT_DISPLAY. Passing a zero-length
// input buffer is legal and means "connect using the driver defaults".
//
typedef struct _PANEL_CONNECT_INPUT {
    ULONG              Size;           // sizeof(PANEL_CONNECT_INPUT)
    ULONG              Flags;          // PANEL_CONNECT_FLAG_*
    PANEL_DISPLAY_MODE PreferredMode;  // honoured only with USE_PREFERRED_MODE
} PANEL_CONNECT_INPUT, *PPANEL_CONNECT_INPUT;

//
// Output buffer for IOCTL_PANEL_CONNECT_DISPLAY and IOCTL_PANEL_DISCONNECT_DISPLAY.
//
typedef struct _PANEL_COMMAND_OUTPUT {
    ULONG                Size;          // sizeof(PANEL_COMMAND_OUTPUT)
    PANEL_COMMAND_RESULT Result;
    PANEL_MONITOR_STATE  MonitorState;  // state after the command completed
} PANEL_COMMAND_OUTPUT, *PPANEL_COMMAND_OUTPUT;

//
// Output buffer for IOCTL_PANEL_GET_STATUS.
//
typedef struct _PANEL_STATUS_OUTPUT {
    ULONG               Size;             // sizeof(PANEL_STATUS_OUTPUT)
    ULONG               ProtocolVersion;  // PANEL_DISPLAY_PROTOCOL_VERSION
    PANEL_DRIVER_STATE  DriverState;
    PANEL_MONITOR_STATE MonitorState;

    //
    // Mode currently committed by the OS for the virtual monitor. All zeroes
    // while disconnected, or while connected but not yet activated.
    //
    PANEL_DISPLAY_MODE  ActiveMode;

    //
    // Diagnostics. FramesProcessed is cumulative for the lifetime of the current
    // swap chain, ConnectCount for the lifetime of the device.
    //
    ULONGLONG           FramesProcessed;
    ULONG               ConnectCount;

    //
    // Modes the driver advertises to Windows.
    //
    ULONG               ModeCount;
    PANEL_DISPLAY_MODE  Modes[PANEL_MAX_REPORTED_MODES];
} PANEL_STATUS_OUTPUT, *PPANEL_STATUS_OUTPUT;

// Result carried in PANEL_FRAME_OUTPUT. "No frame" and "no new frame" are
// normal polling outcomes, not failed DeviceIoControl calls. FrameReplaced asks
// the client to repeat GET_FRAME_INFO because a newer generation/sequence won.
typedef enum _PANEL_FRAME_RESULT {
    PanelFrameResultOk             = 0,
    PanelFrameResultNoFrame        = 1,
    PanelFrameResultNoNewFrame     = 2,
    PanelFrameResultFrameReplaced  = 3,
} PANEL_FRAME_RESULT;

// Capture/wire formats exposed to PanelHost. These describe byte order in the
// payload, independently of DXGI enum values.
typedef enum _PANEL_FRAME_FORMAT {
    PanelFrameFormatUnknown   = 0,
    PanelFrameFormatBgra8888  = 1,
    PanelFrameFormatRgba8888  = 2,
} PANEL_FRAME_FORMAT;

// Input for IOCTL_PANEL_GET_FRAME_INFO. LastSequence suppresses retransmission
// of a frame the caller has already consumed.
typedef struct _PANEL_FRAME_QUERY_INPUT {
    ULONG     Size;          // sizeof(PANEL_FRAME_QUERY_INPUT)
    ULONG     Reserved;      // must be zero
    ULONGLONG LastSequence;
} PANEL_FRAME_QUERY_INPUT, *PPANEL_FRAME_QUERY_INPUT;

// Input for IOCTL_PANEL_READ_LATEST_FRAME. Both identity fields must match the
// latest snapshot reported by GET_FRAME_INFO; otherwise FrameReplaced is
// returned with the current header and no pixels.
typedef struct _PANEL_FRAME_READ_INPUT {
    ULONG     Size;          // sizeof(PANEL_FRAME_READ_INPUT)
    ULONG     Reserved;      // must be zero
    ULONGLONG Generation;
    ULONGLONG Sequence;
} PANEL_FRAME_READ_INPUT, *PPANEL_FRAME_READ_INPUT;

// Fixed 64-byte header returned by both frame IOCTLs. READ_LATEST_FRAME appends
// PayloadBytes bytes immediately after this structure when Result is Ok.
typedef struct _PANEL_FRAME_OUTPUT {
    ULONG              Size;                     // sizeof(PANEL_FRAME_OUTPUT)
    PANEL_FRAME_RESULT Result;
    ULONGLONG          Generation;               // swapchain generation
    ULONGLONG          Sequence;                 // monotonically increasing
    ULONGLONG          CaptureTimestampQpc;      // QueryPerformanceCounter
    ULONGLONG          CaptureTimestampFrequency;
    ULONG              Width;
    ULONG              Height;
    ULONG              Stride;
    PANEL_FRAME_FORMAT Format;
    ULONG              PayloadBytes;
    ULONG              Reserved;
} PANEL_FRAME_OUTPUT, *PPANEL_FRAME_OUTPUT;

typedef struct _PANEL_CAPTURE_CONFIG_INPUT {
    ULONG Size;                     // sizeof(PANEL_CAPTURE_CONFIG_INPUT)
    ULONG TargetFramesPerSecond;    // 30, 45, or 60
} PANEL_CAPTURE_CONFIG_INPUT, *PPANEL_CAPTURE_CONFIG_INPUT;

typedef struct _PANEL_CAPTURE_CONFIG_OUTPUT {
    ULONG Size;                     // sizeof(PANEL_CAPTURE_CONFIG_OUTPUT)
    ULONG ProtocolVersion;          // PANEL_DISPLAY_PROTOCOL_VERSION
    ULONG TargetFramesPerSecond;
    ULONG Reserved;
} PANEL_CAPTURE_CONFIG_OUTPUT, *PPANEL_CAPTURE_CONFIG_OUTPUT;

// Cumulative metrics for the current swapchain generation. QPC duration fields
// are sums for successful acquisitions / submitted copies / map attempts /
// publish attempts respectively. A new generation starts every field at zero.
typedef struct _PANEL_CAPTURE_METRICS_OUTPUT {
    ULONG     Size;                     // sizeof(PANEL_CAPTURE_METRICS_OUTPUT)
    ULONG     ProtocolVersion;          // PANEL_DISPLAY_PROTOCOL_VERSION
    ULONGLONG Generation;
    ULONGLONG QpcFrequency;
    ULONGLONG SwapchainAcquired;
    ULONGLONG CaptureThrottleSkipped;
    ULONGLONG CopySubmitted;
    ULONGLONG MapSuccess;
    ULONGLONG MapBusy;
    ULONGLONG MapFailure;
    ULONGLONG Published;
    ULONGLONG PublishDropped;
    ULONGLONG AcquireQpcTotal;
    ULONGLONG CopyQpcTotal;
    ULONGLONG MapAttemptQpcTotal;
    ULONGLONG CpuPublishQpcTotal;
    ULONG     TargetFramesPerSecond;
    ULONG     Reserved;
} PANEL_CAPTURE_METRICS_OUTPUT, *PPANEL_CAPTURE_METRICS_OUTPUT;

#define PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS 16

// Cumulative accumulated-deadline diagnostics for the current swapchain.
// DeadlineLatenessHistogram uses these inclusive upper bounds in microseconds:
// 250, 500, 1000, 1500, 2000, 3000, 4000, 5000, 6000, 8000, 10000,
// 12000, 16000, 24000, 33000, 1000000.
typedef struct _PANEL_CAPTURE_PACING_METRICS_OUTPUT {
    ULONG     Size;                     // sizeof(...)
    ULONG     ProtocolVersion;          // PANEL_DISPLAY_PROTOCOL_VERSION
    ULONGLONG Generation;
    ULONGLONG QpcFrequency;
    ULONGLONG ScheduledFrames;
    ULONGLONG MissedDeadlines;
    ULONGLONG DeadlineLatenessSamples;
    ULONGLONG DeadlineLatenessQpcTotal;
    ULONG     TargetFramesPerSecond;
    ULONG     SchedulerTargetFramesPerSecond;
    ULONG     HistogramBucketCount;
    ULONG     Reserved;
    ULONGLONG DeadlineLatenessHistogram[
        PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS];
} PANEL_CAPTURE_PACING_METRICS_OUTPUT,
  *PPANEL_CAPTURE_PACING_METRICS_OUTPUT;

#ifdef __cplusplus
static_assert(sizeof(PANEL_FRAME_QUERY_INPUT) == 16, "PANEL_FRAME_QUERY_INPUT contract drift");
static_assert(sizeof(PANEL_FRAME_READ_INPUT) == 24, "PANEL_FRAME_READ_INPUT contract drift");
static_assert(sizeof(PANEL_FRAME_OUTPUT) == 64, "PANEL_FRAME_OUTPUT contract drift");
static_assert(sizeof(PANEL_CAPTURE_CONFIG_INPUT) == 8,
              "PANEL_CAPTURE_CONFIG_INPUT contract drift");
static_assert(sizeof(PANEL_CAPTURE_CONFIG_OUTPUT) == 16,
              "PANEL_CAPTURE_CONFIG_OUTPUT contract drift");
static_assert(sizeof(PANEL_CAPTURE_METRICS_OUTPUT) == 128,
              "PANEL_CAPTURE_METRICS_OUTPUT contract drift");
static_assert(sizeof(PANEL_CAPTURE_PACING_METRICS_OUTPUT) == 200,
              "PANEL_CAPTURE_PACING_METRICS_OUTPUT contract drift");
#endif
