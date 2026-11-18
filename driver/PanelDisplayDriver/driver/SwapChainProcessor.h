/*++

Module Name:

    SwapChainProcessor.h

Abstract:

    Correct handling and bounded CPU capture of the IddCx swap chain. Every
    buffer is acquired and finished immediately. Each acquired surface refreshes
    one reusable D3D11 staging texture without waiting for the GPU; accumulated
    deadlines bound publication into a latest-only store. Encoding and transport
    deliberately remain in PanelHost.

--*/

#pragma once

#include "Driver.h"
#include "../common/CaptureScheduler.h"
#include "../common/LatestFrameStore.h"

//
// The D3D device the swap chain buffers belong to. Created on the same adapter
// Windows rendered the desktop on, which is what IddCx requires.
//
struct Direct3DDevice
{
    Direct3DDevice();
    explicit Direct3DDevice(LUID AdapterLuid);

    HRESULT Init();

    LUID                                       AdapterLuid;
    Microsoft::WRL::ComPtr<IDXGIFactory5>      DxgiFactory;
    Microsoft::WRL::ComPtr<IDXGIAdapter1>      Adapter;
    Microsoft::WRL::ComPtr<ID3D11Device>       Device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> DeviceContext;
};

class SwapChainProcessor
{
public:
    SwapChainProcessor(_In_ IDDCX_SWAPCHAIN SwapChain,
                       _In_ std::shared_ptr<Direct3DDevice> Device,
                       _In_ HANDLE NewFrameEvent,
                       _In_ PanelOS::LatestFrameStore* FrameStore,
                       _In_ ULONGLONG Generation,
                       _In_ ULONG TargetFramesPerSecond);
    ~SwapChainProcessor();

    // Starts the worker and commits this processor to owning the accepted
    // swapchain. On failure no ownership is taken, so AssignSwapChain can return
    // STATUS_GRAPHICS_INDIRECT_DISPLAY_ABANDON_SWAPCHAIN safely.
    bool Start();

    //
    // Frames acquired since this swap chain was assigned. Safe to read from any
    // thread.
    //
    ULONGLONG FramesProcessed() const;
    ULONGLONG Generation() const { return m_Generation; }
    void SetCaptureTargetFps(_In_ ULONG TargetFramesPerSecond);
    void GetCaptureMetrics(_Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output) const;
    void GetCapturePacingMetrics(
        _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output) const;

private:
    static DWORD CALLBACK RunThread(LPVOID Argument);

    void Run();
    void RunCore();

    //
    // Called once per acquired buffer. See the PanelHost handoff note above.
    //
    void ProcessFrame(_In_ const IDDCX_METADATA& MetaData);
    PanelOS::CaptureScheduleDecision EvaluateCaptureSchedule(
        _In_ ULONGLONG TimestampQpc);
    void TryPublishPendingAtCurrentDeadline();
    void PublishPendingForDecision(
        _In_ const PanelOS::CaptureScheduleDecision& Decision);
    DWORD PendingDeadlineWaitMilliseconds() const;
    bool EnsureStagingTexture(_In_ const D3D11_TEXTURE2D_DESC& SourceDesc);
    bool TryPublishPendingStaging(_Out_ ULONGLONG& PublishTimestampQpc);
    void RecordDeadlineLateness(_In_ ULONGLONG ScheduledDeadlineQpc,
                                _In_ ULONGLONG PublishTimestampQpc);
    void TraceSchedulerTarget(_In_ ULONG TargetFramesPerSecond) const;
    void TraceFirstPublishedFrame(_In_ ULONGLONG Sequence,
                                  _In_ UINT RowPitch,
                                  _In_reads_bytes_(PayloadBytes) const BYTE* Pixels,
                                  _In_ size_t PayloadBytes);
    static PANEL_FRAME_FORMAT ToPanelFrameFormat(_In_ DXGI_FORMAT Format);
    static const char* DxgiFormatName(_In_ DXGI_FORMAT Format);
    static const char* PanelFrameFormatName(_In_ PANEL_FRAME_FORMAT Format);
    void TraceUnsupportedFormatOnce(_In_ DXGI_FORMAT Format);

    IDDCX_SWAPCHAIN                     m_SwapChain;
    std::shared_ptr<Direct3DDevice>     m_Device;
    HANDLE                              m_AvailableBufferEvent;
    Microsoft::WRL::Wrappers::Thread    m_Thread;
    Microsoft::WRL::Wrappers::Event     m_TerminateEvent;
    bool                                m_OwnsSwapChain;

    // Latest-frame capture is deliberately independent of the IddCx object
    // lifetime. The device owns the store; Generation prevents this processor
    // from publishing after a newer swapchain has been assigned.
    PanelOS::LatestFrameStore*           m_FrameStore;
    ULONGLONG                            m_Generation;

    Microsoft::WRL::ComPtr<ID3D11Texture2D> m_StagingTexture;
    D3D11_TEXTURE2D_DESC                 m_StagingDesc;
    bool                                 m_HasPendingStaging;
    PANEL_FRAME_FORMAT                   m_PendingFormat;
    ULONGLONG                            m_PendingTimestampQpc;
    ULONGLONG                            m_QpcFrequency;
    PanelOS::CaptureScheduler            m_CaptureScheduler;
    volatile LONG                        m_TargetFramesPerSecond;
    volatile LONG                        m_SchedulerTargetFramesPerSecond;
    DXGI_FORMAT                          m_LoggedUnsupportedFormats[8];
    ULONG                                m_LoggedUnsupportedFormatCount;
    bool                                 m_LoggedUnsupportedTextureShape;
    bool                                 m_HasObservedScheduleDecision;
    HRESULT                              m_LastStagingCreateError;
    ULONG                                m_DiagnosticsLogged;

    volatile LONG64                     m_FrameCount;
    volatile LONG64                     m_CaptureThrottleSkippedCount;
    volatile LONG64                     m_CopyRequestedCount;
    volatile LONG64                     m_MapBusyCount;
    volatile LONG64                     m_MapSuccessCount;
    volatile LONG64                     m_MapFailureCount;
    volatile LONG64                     m_PublishedCount;
    volatile LONG64                     m_PublishDroppedCount;
    volatile LONG64                     m_ScheduledFrameCount;
    volatile LONG64                     m_MissedDeadlineCount;
    volatile LONG64                     m_DeadlineLatenessSampleCount;
    volatile LONG64                     m_DeadlineLatenessQpcTotal;
    volatile LONG64                     m_DeadlineLatenessHistogram[
        PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS];
    volatile LONG64                     m_AcquireQpcTotal;
    volatile LONG64                     m_CopyQpcTotal;
    volatile LONG64                     m_MapAttemptQpcTotal;
    volatile LONG64                     m_CpuPublishQpcTotal;
};
