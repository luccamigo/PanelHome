/*++

Module Name:

    SwapChainProcessor.cpp

Abstract:

    Implementation of the IddCx swap chain processing thread. See
    SwapChainProcessor.h.

    Structure follows the official Microsoft IddSample: a dedicated thread, run
    at Distribution MMCSS priority, that loops on
    IddCxSwapChainReleaseAndAcquireBuffer until the swap chain is torn down.

--*/

#include "SwapChainProcessor.h"
#include "../common/PanelFrameDiagnostics.h"

using namespace Microsoft::WRL;

Direct3DDevice::Direct3DDevice()
{
    AdapterLuid = LUID{};
}

Direct3DDevice::Direct3DDevice(LUID InAdapterLuid)
    : AdapterLuid(InAdapterLuid)
{
}

HRESULT Direct3DDevice::Init()
{
    //
    // The DXGI factory is needed to resolve the LUID Windows gave us into the
    // adapter that rendered the desktop; creating the D3D device on any other
    // adapter would force a cross-adapter copy of every frame.
    //
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr))
    {
        PANEL_TRACE_ERROR("SwapChain.CreateDxgiFactoryFailed", TraceLoggingHResult(hr, "hr"));
        return hr;
    }

    hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    if (FAILED(hr))
    {
        PANEL_TRACE_ERROR("SwapChain.EnumAdapterByLuidFailed", TraceLoggingHResult(hr, "hr"));
        return hr;
    }

    hr = D3D11CreateDevice(Adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                           D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                           D3D11_SDK_VERSION, &Device, nullptr, &DeviceContext);
    if (FAILED(hr))
    {
        PANEL_TRACE_ERROR("SwapChain.D3D11CreateDeviceFailed", TraceLoggingHResult(hr, "hr"));
        return hr;
    }

    return S_OK;
}

SwapChainProcessor::SwapChainProcessor(_In_ IDDCX_SWAPCHAIN SwapChain,
                                       _In_ std::shared_ptr<Direct3DDevice> Device,
                                       _In_ HANDLE NewFrameEvent,
                                       _In_ PanelOS::LatestFrameStore* FrameStore,
                                       _In_ ULONGLONG Generation,
                                       _In_ ULONG TargetFramesPerSecond)
    : m_SwapChain(SwapChain)
    , m_Device(Device)
    , m_AvailableBufferEvent(NewFrameEvent)
    , m_OwnsSwapChain(false)
    , m_FrameStore(FrameStore)
    , m_Generation(Generation)
    , m_StagingDesc{}
    , m_HasPendingStaging(false)
    , m_PendingFormat(PanelFrameFormatUnknown)
    , m_PendingTimestampQpc(0)
    , m_QpcFrequency(0)
    , m_TargetFramesPerSecond(static_cast<LONG>(TargetFramesPerSecond))
    , m_SchedulerTargetFramesPerSecond(TargetFramesPerSecond)
    , m_LoggedUnsupportedFormats{}
    , m_LoggedUnsupportedFormatCount(0)
    , m_LoggedUnsupportedTextureShape(false)
    , m_HasObservedScheduleDecision(false)
    , m_LastStagingCreateError(S_OK)
    , m_DiagnosticsLogged(0)
    , m_FrameCount(0)
    , m_CaptureThrottleSkippedCount(0)
    , m_CopyRequestedCount(0)
    , m_MapBusyCount(0)
    , m_MapSuccessCount(0)
    , m_MapFailureCount(0)
    , m_PublishedCount(0)
    , m_PublishDroppedCount(0)
    , m_ScheduledFrameCount(0)
    , m_MissedDeadlineCount(0)
    , m_DeadlineLatenessSampleCount(0)
    , m_DeadlineLatenessQpcTotal(0)
    , m_DeadlineLatenessHistogram{}
    , m_AcquireQpcTotal(0)
    , m_CopyQpcTotal(0)
    , m_MapAttemptQpcTotal(0)
    , m_CpuPublishQpcTotal(0)
{
    PANEL_TRACE_INFO("VideoTarget.SwapChainProcessorConstructed",
        TraceLoggingUInt32(TargetFramesPerSecond, "targetFps"),
        TraceLoggingUInt64(Generation, "generation"));
    LARGE_INTEGER frequency = {};
    if (QueryPerformanceFrequency(&frequency))
    {
        m_QpcFrequency = static_cast<ULONGLONG>(frequency.QuadPart);
        m_CaptureScheduler.Reset(m_QpcFrequency, TargetFramesPerSecond);
        TraceSchedulerTarget(TargetFramesPerSecond);
    }

    HANDLE terminateEvent = CreateEvent(nullptr, FALSE, FALSE, nullptr);
    if (terminateEvent == nullptr)
    {
        PANEL_TRACE_ERROR("SwapChain.CreateTerminateEventFailed",
            TraceLoggingUInt32(GetLastError(), "error"));
        return;
    }

    m_TerminateEvent.Attach(terminateEvent);

}

bool SwapChainProcessor::Start()
{
    if (!m_TerminateEvent.Get())
    {
        return false;
    }

    // Once a thread can observe this object, the callback is committed to
    // returning success and therefore to owning/deleting the swapchain.
    m_OwnsSwapChain = true;
    HANDLE thread = CreateThread(nullptr, 0, RunThread, this, 0, nullptr);
    if (thread == nullptr)
    {
        m_OwnsSwapChain = false;
        PANEL_TRACE_ERROR("SwapChain.CreateThreadFailed",
            TraceLoggingUInt32(GetLastError(), "error"));
        return false;
    }

    m_Thread.Attach(thread);

    PANEL_TRACE_INFO("SwapChain.Started", TraceLoggingPointer(m_SwapChain, "swapChain"));
    PANEL_DEBUG_PRINT("[PanelDisplayDriver] swapchain started\n");

    return true;
}

SwapChainProcessor::~SwapChainProcessor()
{
    //
    // Ask the thread to stop and wait for it. The thread owns the swap chain
    // object, so it must be gone before we return - otherwise IddCx could tear
    // the monitor down underneath a live buffer acquisition.
    //
    if (m_Thread.Get())
    {
        SetEvent(m_TerminateEvent.Get());
        WaitForSingleObject(m_Thread.Get(), INFINITE);
    }
    else if (m_OwnsSwapChain && m_SwapChain != nullptr)
    {
        // Ownership was accepted by AssignSwapChain, but no worker existed to
        // execute Run() and perform its normal deletion.
        WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_SwapChain));
        m_SwapChain = nullptr;
    }

    m_FrameStore->InvalidateGeneration(m_Generation);

    const ULONGLONG acquired = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_FrameCount, 0, 0));
    const ULONGLONG copyRequested = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_CopyRequestedCount, 0, 0));
    const ULONGLONG mapBusy = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_MapBusyCount, 0, 0));
    const ULONGLONG mapSuccess = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_MapSuccessCount, 0, 0));
    const ULONGLONG mapFailure = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_MapFailureCount, 0, 0));
    const ULONGLONG published = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_PublishedCount, 0, 0));
    const ULONGLONG publishDropped = static_cast<ULONGLONG>(InterlockedCompareExchange64(
        &m_PublishDroppedCount, 0, 0));

    PANEL_TRACE_INFO("SwapChain.CaptureSummary",
        TraceLoggingUInt64(m_Generation, "generation"),
        TraceLoggingUInt64(acquired, "acquired"),
        TraceLoggingUInt64(copyRequested, "copyRequested"),
        TraceLoggingUInt64(mapBusy, "mapBusy"),
        TraceLoggingUInt64(mapSuccess, "mapSuccess"),
        TraceLoggingUInt64(mapFailure, "mapFailure"),
        TraceLoggingUInt64(published, "published"),
        TraceLoggingUInt64(publishDropped, "publishDropped"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoDebug] capture summary generation=%llu "
        "acquired=%llu copyRequested=%llu mapBusy=%llu mapSuccess=%llu "
        "mapFailure=%llu published=%llu publishDropped=%llu\n",
        static_cast<unsigned long long>(m_Generation),
        static_cast<unsigned long long>(acquired),
        static_cast<unsigned long long>(copyRequested),
        static_cast<unsigned long long>(mapBusy),
        static_cast<unsigned long long>(mapSuccess),
        static_cast<unsigned long long>(mapFailure),
        static_cast<unsigned long long>(published),
        static_cast<unsigned long long>(publishDropped));
}

ULONGLONG SwapChainProcessor::FramesProcessed() const
{
    return static_cast<ULONGLONG>(InterlockedCompareExchange64(
        const_cast<volatile LONG64*>(&m_FrameCount), 0, 0));
}

void SwapChainProcessor::SetCaptureTargetFps(
    _In_ ULONG TargetFramesPerSecond)
{
    const LONG previous = InterlockedExchange(
        &m_TargetFramesPerSecond,
        static_cast<LONG>(TargetFramesPerSecond));
    PANEL_TRACE_INFO("VideoTarget.SwapChainProcessorUpdated",
        TraceLoggingUInt32(static_cast<ULONG>(previous), "previousTargetFps"),
        TraceLoggingUInt32(TargetFramesPerSecond, "targetFps"),
        TraceLoggingUInt64(m_Generation, "generation"));
}

void SwapChainProcessor::GetCaptureMetrics(
    _Out_ PANEL_CAPTURE_METRICS_OUTPUT& Output) const
{
    const auto readCounter = [](const volatile LONG64* Value) -> ULONGLONG
    {
        return static_cast<ULONGLONG>(InterlockedCompareExchange64(
            const_cast<volatile LONG64*>(Value), 0, 0));
    };

    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;
    Output.Generation = m_Generation;
    Output.QpcFrequency = m_QpcFrequency;
    Output.SwapchainAcquired = readCounter(&m_FrameCount);
    Output.CaptureThrottleSkipped = readCounter(&m_CaptureThrottleSkippedCount);
    Output.CopySubmitted = readCounter(&m_CopyRequestedCount);
    Output.MapSuccess = readCounter(&m_MapSuccessCount);
    Output.MapBusy = readCounter(&m_MapBusyCount);
    Output.MapFailure = readCounter(&m_MapFailureCount);
    Output.Published = readCounter(&m_PublishedCount);
    Output.PublishDropped = readCounter(&m_PublishDroppedCount);
    Output.AcquireQpcTotal = readCounter(&m_AcquireQpcTotal);
    Output.CopyQpcTotal = readCounter(&m_CopyQpcTotal);
    Output.MapAttemptQpcTotal = readCounter(&m_MapAttemptQpcTotal);
    Output.CpuPublishQpcTotal = readCounter(&m_CpuPublishQpcTotal);
    Output.TargetFramesPerSecond = static_cast<ULONG>(InterlockedCompareExchange(
        const_cast<volatile LONG*>(&m_TargetFramesPerSecond), 0, 0));
}

void SwapChainProcessor::GetCapturePacingMetrics(
    _Out_ PANEL_CAPTURE_PACING_METRICS_OUTPUT& Output) const
{
    const auto readCounter = [](const volatile LONG64* Value) -> ULONGLONG
    {
        return static_cast<ULONGLONG>(InterlockedCompareExchange64(
            const_cast<volatile LONG64*>(Value), 0, 0));
    };

    Output = {};
    Output.Size = sizeof(Output);
    Output.ProtocolVersion = PANEL_DISPLAY_PROTOCOL_VERSION;
    Output.Generation = m_Generation;
    Output.QpcFrequency = m_QpcFrequency;
    Output.ScheduledFrames = readCounter(&m_ScheduledFrameCount);
    Output.MissedDeadlines = readCounter(&m_MissedDeadlineCount);
    Output.DeadlineLatenessSamples =
        readCounter(&m_DeadlineLatenessSampleCount);
    Output.DeadlineLatenessQpcTotal =
        readCounter(&m_DeadlineLatenessQpcTotal);
    Output.TargetFramesPerSecond = static_cast<ULONG>(InterlockedCompareExchange(
        const_cast<volatile LONG*>(&m_TargetFramesPerSecond), 0, 0));
    Output.SchedulerTargetFramesPerSecond = static_cast<ULONG>(
        InterlockedCompareExchange(
            const_cast<volatile LONG*>(&m_SchedulerTargetFramesPerSecond),
            0,
            0));
    Output.HistogramBucketCount =
        PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS;
    for (ULONG index = 0;
         index < PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS;
         ++index)
    {
        Output.DeadlineLatenessHistogram[index] =
            readCounter(&m_DeadlineLatenessHistogram[index]);
    }
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID Argument)
{
    reinterpret_cast<SwapChainProcessor*>(Argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    //
    // Register with MMCSS so frame processing is not starved by ordinary
    // workloads. Failure is non-fatal: we simply run at normal priority.
    //
    DWORD taskIndex = 0;
    HANDLE avrtHandle = AvSetMmThreadCharacteristics(L"Distribution", &taskIndex);

    if (avrtHandle)
    {
        AvSetMmThreadPriority(avrtHandle, AVRT_PRIORITY_HIGH);
    }

    RunCore();

    // A render-adapter loss can end the worker before IddCx emits Unassign.
    // Retire the snapshot here so a dead swapchain never remains readable.
    m_FrameStore->InvalidateGeneration(m_Generation);

    //
    // The swap chain object is owned by this thread. Destroying it here - rather
    // than in the destructor - guarantees no buffer operation is in flight when
    // IddCx reclaims it.
    //
    WdfObjectDelete(reinterpret_cast<WDFOBJECT>(m_SwapChain));
    m_SwapChain = nullptr;

    if (avrtHandle)
    {
        AvRevertMmThreadCharacteristics(avrtHandle);
    }
}

void SwapChainProcessor::RunCore()
{
    //
    // Hand IddCx the DXGI device the buffers should be created against.
    //
    ComPtr<IDXGIDevice> dxgiDevice;
    HRESULT hr = m_Device->Device.As(&dxgiDevice);
    if (FAILED(hr))
    {
        PANEL_TRACE_ERROR("SwapChain.QueryDxgiDeviceFailed", TraceLoggingHResult(hr, "hr"));
        return;
    }

    IDARG_IN_SWAPCHAINSETDEVICE setDevice = {};
    setDevice.pDevice = dxgiDevice.Get();

    hr = IddCxSwapChainSetDevice(m_SwapChain, &setDevice);
    if (FAILED(hr))
    {
        PANEL_TRACE_ERROR("SwapChain.SetDeviceFailed", TraceLoggingHResult(hr, "hr"));
        return;
    }

    for (;;)
    {
        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer = {};
        LARGE_INTEGER acquireStarted = {};
        if (m_QpcFrequency != 0)
        {
            QueryPerformanceCounter(&acquireStarted);
        }
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_SwapChain, &buffer);

        if (hr == E_PENDING)
        {
            //
            // No new frame yet. Wait for either a new buffer or teardown.
            //
            // The 16 ms cap matches the sample: it keeps the thread responsive
            // if the OS stops presenting without tearing the swap chain down.
            //
            TryPublishPendingAtCurrentDeadline();
            HANDLE waitHandles[] = { m_AvailableBufferEvent, m_TerminateEvent.Get() };
            const DWORD waitResult = WaitForMultipleObjects(
                ARRAYSIZE(waitHandles),
                waitHandles,
                FALSE,
                PendingDeadlineWaitMilliseconds());

            if (waitResult == WAIT_OBJECT_0)
            {
                continue;
            }

            if (waitResult == WAIT_TIMEOUT)
            {
                TryPublishPendingAtCurrentDeadline();
                continue;
            }

            if (waitResult == WAIT_OBJECT_0 + 1)
            {
                break;
            }

            PANEL_TRACE_ERROR("SwapChain.WaitFailed", TraceLoggingUInt32(GetLastError(), "error"));
            break;
        }

        if (FAILED(hr))
        {
            //
            // The swap chain is being torn down (monitor unassigned, mode change,
            // or the render adapter was lost). Exiting the loop lets Run() delete
            // the swap chain object; IddCx will hand us a new one if needed.
            //
            PANEL_TRACE_INFO("SwapChain.AcquireEnded", TraceLoggingHResult(hr, "hr"));
            break;
        }

        if (acquireStarted.QuadPart != 0)
        {
            LARGE_INTEGER acquireEnded = {};
            if (QueryPerformanceCounter(&acquireEnded) &&
                acquireEnded.QuadPart >= acquireStarted.QuadPart)
            {
                InterlockedAdd64(
                    &m_AcquireQpcTotal,
                    acquireEnded.QuadPart - acquireStarted.QuadPart);
            }
        }

        // IddCx transfers one reference to pSurface with every successful
        // acquire. Attach it to a smart pointer so that reference is always
        // released before the buffer is returned, including early exits.
        ComPtr<IUnknown> acquiredSurface;
        acquiredSurface.Attach(buffer.MetaData.pSurface);

        ProcessFrame(buffer.MetaData);
        acquiredSurface.Reset();

        hr = IddCxSwapChainFinishedProcessingFrame(m_SwapChain);
        if (FAILED(hr))
        {
            PANEL_TRACE_ERROR("SwapChain.FinishedProcessingFrameFailed", TraceLoggingHResult(hr, "hr"));
            break;
        }
    }
}

void SwapChainProcessor::ProcessFrame(_In_ const IDDCX_METADATA& MetaData)
{
    InterlockedIncrement64(&m_FrameCount);

    LARGE_INTEGER arrived = {};
    if (m_QpcFrequency == 0 || !QueryPerformanceCounter(&arrived))
    {
        return;
    }
    const ULONGLONG captureTimestamp =
        static_cast<ULONGLONG>(arrived.QuadPart);
    const PanelOS::CaptureScheduleDecision decision =
        EvaluateCaptureSchedule(captureTimestamp);

    // The scheduler gates publication, not CopyResource. The pending staging
    // texture is refreshed by every producer arrival, so the first arrival at
    // or after a deadline publishes the freshest frame which was already ready
    // before that deadline. No queue or duplicate frame is introduced.
    PublishPendingForDecision(decision);
    if (!decision.ShouldCapture)
    {
        InterlockedIncrement64(&m_CaptureThrottleSkippedCount);
    }

    if (MetaData.pSurface == nullptr)
    {
        return;
    }

    ComPtr<ID3D11Texture2D> texture;
    const HRESULT queryHr = MetaData.pSurface->QueryInterface(IID_PPV_ARGS(&texture));
    if (FAILED(queryHr))
    {
        // pSurface can legally be another DXGI surface implementation. It is
        // not fatal to the swapchain; this capture opportunity is just skipped.
        return;
    }

    D3D11_TEXTURE2D_DESC sourceDesc = {};
    texture->GetDesc(&sourceDesc);

    const PANEL_FRAME_FORMAT frameFormat = ToPanelFrameFormat(sourceDesc.Format);
    if (frameFormat == PanelFrameFormatUnknown)
    {
        TraceUnsupportedFormatOnce(sourceDesc.Format);
        return;
    }

    // A CPU-readable D3D11 staging texture cannot be multisampled. IddCx swap
    // surfaces are expected to be one 2D subresource; reject anything else
    // without destabilising frame completion.
    if (sourceDesc.Width == 0 || sourceDesc.Height == 0 ||
        sourceDesc.ArraySize != 1 || sourceDesc.MipLevels != 1 ||
        sourceDesc.SampleDesc.Count != 1 ||
        (static_cast<ULONGLONG>(sourceDesc.Width) * sourceDesc.Height * 4u) >
            PanelOS::MaxCapturedFramePayloadBytes)
    {
        if (!m_LoggedUnsupportedTextureShape)
        {
            m_LoggedUnsupportedTextureShape = true;
            PANEL_TRACE_ERROR("SwapChain.UnsupportedTextureShape",
                TraceLoggingUInt32(sourceDesc.Width, "width"),
                TraceLoggingUInt32(sourceDesc.Height, "height"),
                TraceLoggingUInt32(sourceDesc.ArraySize, "arraySize"),
                TraceLoggingUInt32(sourceDesc.MipLevels, "mipLevels"),
                TraceLoggingUInt32(sourceDesc.SampleDesc.Count, "sampleCount"));
        }
        return;
    }

    if (!EnsureStagingTexture(sourceDesc))
    {
        return;
    }

    // Queue the current frame for the next capture tick. CopyResource is
    // asynchronous; no Flush or GPU wait is introduced on this thread.
    LARGE_INTEGER copyStarted = {};
    if (m_QpcFrequency != 0)
    {
        QueryPerformanceCounter(&copyStarted);
    }
    m_Device->DeviceContext->CopyResource(m_StagingTexture.Get(), texture.Get());
    if (copyStarted.QuadPart != 0)
    {
        LARGE_INTEGER copyEnded = {};
        if (QueryPerformanceCounter(&copyEnded) &&
            copyEnded.QuadPart >= copyStarted.QuadPart)
        {
            InterlockedAdd64(
                &m_CopyQpcTotal,
                copyEnded.QuadPart - copyStarted.QuadPart);
        }
    }
    InterlockedIncrement64(&m_CopyRequestedCount);
    m_HasPendingStaging = true;
    m_PendingFormat = frameFormat;
    m_PendingTimestampQpc = captureTimestamp;
}

PanelOS::CaptureScheduleDecision SwapChainProcessor::EvaluateCaptureSchedule(
    _In_ ULONGLONG TimestampQpc)
{
    const ULONG targetFramesPerSecond = static_cast<ULONG>(
        InterlockedCompareExchange(&m_TargetFramesPerSecond, 0, 0));
    const ULONG schedulerTargetFramesPerSecond = static_cast<ULONG>(
        InterlockedCompareExchange(
            &m_SchedulerTargetFramesPerSecond, 0, 0));
    if (targetFramesPerSecond != schedulerTargetFramesPerSecond)
    {
        // Reset on the swapchain thread so CaptureScheduler itself remains
        // single-threaded. The first frame under the new target is accepted,
        // then accumulated deadlines resume without a catch-up burst.
        InterlockedExchange(
            &m_SchedulerTargetFramesPerSecond,
            static_cast<LONG>(targetFramesPerSecond));
        m_CaptureScheduler.Reset(m_QpcFrequency, targetFramesPerSecond);
        TraceSchedulerTarget(targetFramesPerSecond);
    }
    return m_CaptureScheduler.Evaluate(TimestampQpc);
}

void SwapChainProcessor::TryPublishPendingAtCurrentDeadline()
{
    if (!m_StagingTexture || !m_HasPendingStaging || m_QpcFrequency == 0)
    {
        return;
    }

    LARGE_INTEGER now = {};
    if (!QueryPerformanceCounter(&now))
    {
        return;
    }

    PublishPendingForDecision(EvaluateCaptureSchedule(
        static_cast<ULONGLONG>(now.QuadPart)));
}

void SwapChainProcessor::PublishPendingForDecision(
    _In_ const PanelOS::CaptureScheduleDecision& Decision)
{
    if (!Decision.ShouldCapture)
    {
        return;
    }

    if (!m_HasObservedScheduleDecision)
    {
        m_HasObservedScheduleDecision = true;
        if (!m_StagingTexture || !m_HasPendingStaging)
        {
            return;
        }
    }

    InterlockedAdd64(
        &m_ScheduledFrameCount,
        static_cast<LONG64>(Decision.ElapsedDeadlines));
    if (!m_StagingTexture || !m_HasPendingStaging)
    {
        InterlockedAdd64(
            &m_MissedDeadlineCount,
            static_cast<LONG64>(Decision.ElapsedDeadlines));
        return;
    }

    if (Decision.ElapsedDeadlines > 1u)
    {
        InterlockedAdd64(
            &m_MissedDeadlineCount,
            static_cast<LONG64>(Decision.ElapsedDeadlines - 1u));
    }

    ULONGLONG publishTimestampQpc = 0;
    if (TryPublishPendingStaging(publishTimestampQpc))
    {
        RecordDeadlineLateness(
            Decision.ScheduledDeadline,
            publishTimestampQpc);
    }
    else
    {
        InterlockedIncrement64(&m_MissedDeadlineCount);
    }
}

DWORD SwapChainProcessor::PendingDeadlineWaitMilliseconds() const
{
    constexpr DWORD maximumWaitMilliseconds = 16u;
    if (!m_HasPendingStaging || m_QpcFrequency == 0)
    {
        return maximumWaitMilliseconds;
    }

    ULONGLONG deadline = 0;
    LARGE_INTEGER now = {};
    if (!m_CaptureScheduler.TryGetNextDeadline(deadline) ||
        !QueryPerformanceCounter(&now))
    {
        return maximumWaitMilliseconds;
    }

    const ULONGLONG current = static_cast<ULONGLONG>(now.QuadPart);
    if (deadline <= current)
    {
        return 0;
    }

    const ULONGLONG delta = deadline - current;
    const ULONGLONG wholeSeconds = delta / m_QpcFrequency;
    if (wholeSeconds != 0)
    {
        return maximumWaitMilliseconds;
    }

    const ULONGLONG remainder = delta % m_QpcFrequency;
    const ULONGLONG milliseconds =
        (remainder * 1'000u + m_QpcFrequency - 1u) / m_QpcFrequency;
    if (milliseconds == 0)
    {
        return 1u;
    }

    return static_cast<DWORD>(milliseconds < maximumWaitMilliseconds
        ? milliseconds
        : maximumWaitMilliseconds);
}

PANEL_FRAME_FORMAT SwapChainProcessor::ToPanelFrameFormat(_In_ DXGI_FORMAT Format)
{
    switch (Format)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
        return PanelFrameFormatBgra8888;
    case DXGI_FORMAT_R8G8B8A8_UNORM:
        return PanelFrameFormatRgba8888;
    default:
        return PanelFrameFormatUnknown;
    }
}

const char* SwapChainProcessor::DxgiFormatName(_In_ DXGI_FORMAT Format)
{
    switch (Format)
    {
    case DXGI_FORMAT_B8G8R8A8_UNORM:
        return "DXGI_FORMAT_B8G8R8A8_UNORM";
    case DXGI_FORMAT_R8G8B8A8_UNORM:
        return "DXGI_FORMAT_R8G8B8A8_UNORM";
    default:
        return "DXGI_FORMAT_UNKNOWN_OR_UNSUPPORTED";
    }
}

const char* SwapChainProcessor::PanelFrameFormatName(_In_ PANEL_FRAME_FORMAT Format)
{
    switch (Format)
    {
    case PanelFrameFormatBgra8888:
        return "BGRA8888";
    case PanelFrameFormatRgba8888:
        return "RGBA8888";
    default:
        return "UNKNOWN";
    }
}

void SwapChainProcessor::TraceUnsupportedFormatOnce(_In_ DXGI_FORMAT Format)
{
    for (ULONG index = 0; index < m_LoggedUnsupportedFormatCount; ++index)
    {
        if (m_LoggedUnsupportedFormats[index] == Format)
        {
            return;
        }
    }

    if (m_LoggedUnsupportedFormatCount < ARRAYSIZE(m_LoggedUnsupportedFormats))
    {
        m_LoggedUnsupportedFormats[m_LoggedUnsupportedFormatCount++] = Format;
        PANEL_TRACE_ERROR("SwapChain.UnsupportedFormat",
            TraceLoggingUInt32(static_cast<UINT32>(Format), "dxgiFormat"));
        PANEL_DEBUG_PRINT("[PanelDisplayDriver] unsupported swapchain DXGI format %u\n",
                          static_cast<unsigned>(Format));
    }
}

bool SwapChainProcessor::EnsureStagingTexture(_In_ const D3D11_TEXTURE2D_DESC& SourceDesc)
{
    if (m_StagingTexture &&
        m_StagingDesc.Width == SourceDesc.Width &&
        m_StagingDesc.Height == SourceDesc.Height &&
        m_StagingDesc.Format == SourceDesc.Format)
    {
        return true;
    }

    // A mode/format change invalidates the pending GPU copy. The new resource
    // is reused until those three capture properties change again.
    m_StagingTexture.Reset();
    m_StagingDesc = {};
    m_HasPendingStaging = false;
    m_PendingFormat = PanelFrameFormatUnknown;
    m_PendingTimestampQpc = 0;

    D3D11_TEXTURE2D_DESC stagingDesc = SourceDesc;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;

    const HRESULT hr = m_Device->Device->CreateTexture2D(
        &stagingDesc, nullptr, &m_StagingTexture);
    if (FAILED(hr))
    {
        if (hr != m_LastStagingCreateError)
        {
            m_LastStagingCreateError = hr;
            PANEL_TRACE_ERROR("SwapChain.CreateStagingTextureFailed",
                TraceLoggingHResult(hr, "hr"),
                TraceLoggingUInt32(SourceDesc.Width, "width"),
                TraceLoggingUInt32(SourceDesc.Height, "height"),
                TraceLoggingUInt32(static_cast<UINT32>(SourceDesc.Format), "format"));
        }
        return false;
    }

    m_LastStagingCreateError = S_OK;
    m_StagingDesc = stagingDesc;
    return true;
}

bool SwapChainProcessor::TryPublishPendingStaging(
    _Out_ ULONGLONG& PublishTimestampQpc)
{
    PublishTimestampQpc = 0;
    bool didPublish = false;
    D3D11_MAPPED_SUBRESOURCE mapped = {};
    LARGE_INTEGER mapStarted = {};
    if (m_QpcFrequency != 0)
    {
        QueryPerformanceCounter(&mapStarted);
    }
    const HRESULT hr = m_Device->DeviceContext->Map(
        m_StagingTexture.Get(), 0, D3D11_MAP_READ,
        D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
    if (mapStarted.QuadPart != 0)
    {
        LARGE_INTEGER mapEnded = {};
        if (QueryPerformanceCounter(&mapEnded) &&
            mapEnded.QuadPart >= mapStarted.QuadPart)
        {
            InterlockedAdd64(
                &m_MapAttemptQpcTotal,
                mapEnded.QuadPart - mapStarted.QuadPart);
        }
    }

    if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
    {
        InterlockedIncrement64(&m_MapBusyCount);
        return false;
    }

    if (FAILED(hr))
    {
        // A lost/reconfigured resource is recovered by a later assignment or
        // descriptor change. Avoid per-frame logging here.
        InterlockedIncrement64(&m_MapFailureCount);
        m_HasPendingStaging = false;
        return false;
    }

    InterlockedIncrement64(&m_MapSuccessCount);

    const ULONGLONG payloadBytes =
        static_cast<ULONGLONG>(mapped.RowPitch) * m_StagingDesc.Height;

    if (mapped.pData != nullptr && mapped.RowPitch != 0 &&
        payloadBytes <= PanelOS::MaxCapturedFramePayloadBytes)
    {
        LARGE_INTEGER publishStarted = {};
        if (m_QpcFrequency != 0)
        {
            QueryPerformanceCounter(&publishStarted);
        }
        ULONGLONG sequence = 0;
        const bool published = m_FrameStore->TryPublish(
            m_Generation,
            m_PendingTimestampQpc,
            m_QpcFrequency,
            m_StagingDesc.Width,
            m_StagingDesc.Height,
            mapped.RowPitch,
            m_PendingFormat,
            static_cast<const BYTE*>(mapped.pData),
            static_cast<size_t>(payloadBytes),
            &sequence);

        if (published)
        {
            didPublish = true;
            InterlockedIncrement64(&m_PublishedCount);
            LARGE_INTEGER publishedAt = {};
            if (QueryPerformanceCounter(&publishedAt))
            {
                PublishTimestampQpc =
                    static_cast<ULONGLONG>(publishedAt.QuadPart);
            }
            TraceFirstPublishedFrame(sequence,
                                     mapped.RowPitch,
                                     static_cast<const BYTE*>(mapped.pData),
                                     static_cast<size_t>(payloadBytes));
        }
        else
        {
            InterlockedIncrement64(&m_PublishDroppedCount);
        }

        if (publishStarted.QuadPart != 0)
        {
            LARGE_INTEGER publishEnded = {};
            if (QueryPerformanceCounter(&publishEnded) &&
                publishEnded.QuadPart >= publishStarted.QuadPart)
            {
                InterlockedAdd64(
                    &m_CpuPublishQpcTotal,
                    publishEnded.QuadPart - publishStarted.QuadPart);
            }
        }
    }
    else
    {
        InterlockedIncrement64(&m_PublishDroppedCount);
    }

    m_Device->DeviceContext->Unmap(m_StagingTexture.Get(), 0);
    m_HasPendingStaging = false;
    return didPublish;
}

void SwapChainProcessor::RecordDeadlineLateness(
    _In_ ULONGLONG ScheduledDeadlineQpc,
    _In_ ULONGLONG PublishTimestampQpc)
{
    if (m_QpcFrequency == 0 ||
        PublishTimestampQpc < ScheduledDeadlineQpc)
    {
        return;
    }

    const ULONGLONG latenessQpc =
        PublishTimestampQpc - ScheduledDeadlineQpc;
    InterlockedIncrement64(&m_DeadlineLatenessSampleCount);
    InterlockedAdd64(
        &m_DeadlineLatenessQpcTotal,
        static_cast<LONG64>(latenessQpc));

    const ULONGLONG wholeSeconds = latenessQpc / m_QpcFrequency;
    const ULONGLONG remainder = latenessQpc % m_QpcFrequency;
    const ULONGLONG maximum = (MAXULONGLONG / 1'000'000u);
    const ULONGLONG latenessMicros = wholeSeconds > maximum
        ? MAXULONGLONG
        : wholeSeconds * 1'000'000u +
            (remainder * 1'000'000u) / m_QpcFrequency;
    static constexpr ULONGLONG bucketUpperMicros[
        PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS] =
    {
        250u, 500u, 1'000u, 1'500u, 2'000u, 3'000u, 4'000u, 5'000u,
        6'000u, 8'000u, 10'000u, 12'000u, 16'000u, 24'000u,
        33'000u, 1'000'000u,
    };

    ULONG bucket = PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS - 1u;
    for (ULONG index = 0;
         index < PANEL_CAPTURE_LATENESS_HISTOGRAM_BUCKETS;
         ++index)
    {
        if (latenessMicros <= bucketUpperMicros[index])
        {
            bucket = index;
            break;
        }
    }
    InterlockedIncrement64(&m_DeadlineLatenessHistogram[bucket]);
}

void SwapChainProcessor::TraceSchedulerTarget(
    _In_ ULONG TargetFramesPerSecond) const
{
    const ULONGLONG wholePeriod = TargetFramesPerSecond == 0
        ? 0
        : m_QpcFrequency / TargetFramesPerSecond;
    const ULONGLONG periodRemainder = TargetFramesPerSecond == 0
        ? 0
        : m_QpcFrequency % TargetFramesPerSecond;
    PANEL_TRACE_INFO("VideoTarget.SchedulerConfigured",
        TraceLoggingUInt32(TargetFramesPerSecond, "targetFps"),
        TraceLoggingUInt64(m_QpcFrequency, "qpcFrequency"),
        TraceLoggingUInt64(wholePeriod, "wholePeriodQpc"),
        TraceLoggingUInt64(periodRemainder, "periodRemainderQpc"));
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoTarget] Scheduler target=%lu "
        "periodQpc=%llu+%llu/%lu frequency=%llu\n",
        TargetFramesPerSecond,
        static_cast<unsigned long long>(wholePeriod),
        static_cast<unsigned long long>(periodRemainder),
        TargetFramesPerSecond,
        static_cast<unsigned long long>(m_QpcFrequency));
}

void SwapChainProcessor::TraceFirstPublishedFrame(
    _In_ ULONGLONG Sequence,
    _In_ UINT RowPitch,
    _In_reads_bytes_(PayloadBytes) const BYTE* Pixels,
    _In_ size_t PayloadBytes)
{
    // One complete raw-frame CRC is enough to correlate Driver and Host. Keep
    // this to a single frame because the analysis runs before Unmap on the
    // swapchain worker and must remain a bounded diagnostic cost.
    constexpr ULONG DiagnosticFrameLimit = 1;
    if (m_DiagnosticsLogged >= DiagnosticFrameLimit)
    {
        return;
    }

    PanelOS::FrameDiagnosticSummary summary = {};
    if (!PanelOS::TryAnalyzeFrame(m_StagingDesc.Width,
                                  m_StagingDesc.Height,
                                  RowPitch,
                                  m_PendingFormat,
                                  Pixels,
                                  PayloadBytes,
                                  summary))
    {
        PANEL_TRACE_ERROR("SwapChain.FirstFrameDiagnosticFailed",
            TraceLoggingUInt64(m_Generation, "generation"),
            TraceLoggingUInt64(Sequence, "sequence"),
            TraceLoggingUInt32(m_StagingDesc.Width, "width"),
            TraceLoggingUInt32(m_StagingDesc.Height, "height"),
            TraceLoggingUInt32(RowPitch, "rowPitch"),
            TraceLoggingUInt64(static_cast<ULONGLONG>(PayloadBytes), "payloadBytes"));
        ++m_DiagnosticsLogged;
        return;
    }

    ++m_DiagnosticsLogged;
    const auto packBgra = [](const PanelOS::FramePixelSample& pixel) -> ULONG
    {
        return (static_cast<ULONG>(pixel.Alpha) << 24) |
               (static_cast<ULONG>(pixel.Red) << 16) |
               (static_cast<ULONG>(pixel.Green) << 8) |
               static_cast<ULONG>(pixel.Blue);
    };
    PANEL_TRACE_VERBOSE("SwapChain.FirstFrameDiagnostic",
        TraceLoggingUInt64(m_Generation, "generation"),
        TraceLoggingUInt32(m_DiagnosticsLogged, "diagnosticIndex"),
        TraceLoggingUInt64(Sequence, "sequence"),
        TraceLoggingUInt32(m_StagingDesc.Width, "width"),
        TraceLoggingUInt32(m_StagingDesc.Height, "height"),
        TraceLoggingUInt32(RowPitch, "rowPitch"),
        TraceLoggingUInt32(static_cast<UINT32>(m_StagingDesc.Format), "dxgiFormat"),
        TraceLoggingUInt32(static_cast<UINT32>(m_PendingFormat), "wireFormat"),
        TraceLoggingUInt64(static_cast<ULONGLONG>(PayloadBytes), "payloadBytes"),
        TraceLoggingHexUInt32(summary.Crc32, "crc32"),
        TraceLoggingHexUInt32(packBgra(summary.TopLeft), "topLeftBgra"),
        TraceLoggingHexUInt32(packBgra(summary.TopRight), "topRightBgra"),
        TraceLoggingHexUInt32(packBgra(summary.Center), "centerBgra"),
        TraceLoggingHexUInt32(packBgra(summary.BottomLeft), "bottomLeftBgra"),
        TraceLoggingHexUInt32(packBgra(summary.BottomRight), "bottomRightBgra"),
        TraceLoggingUInt32(summary.Luminance.Minimum, "luminanceMinimum"),
        TraceLoggingUInt32(summary.Luminance.Maximum, "luminanceMaximum"),
        TraceLoggingUInt32(summary.Luminance.Average, "luminanceAverage"),
        TraceLoggingUInt32(summary.Luminance.NonBlackSamples, "nonBlackSamples"),
        TraceLoggingUInt32(summary.Luminance.SampleCount, "sampleCount"));

    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoDebug] Driver frame %u/%u: sequence=%llu "
        "%ux%u RowPitch=%u DXGI=%s(%u) wire=%s(%u) bytes=%llu crc32=%08X\n",
        m_DiagnosticsLogged,
        DiagnosticFrameLimit,
        static_cast<unsigned long long>(Sequence),
        m_StagingDesc.Width,
        m_StagingDesc.Height,
        RowPitch,
        DxgiFormatName(m_StagingDesc.Format),
        static_cast<unsigned>(m_StagingDesc.Format),
        PanelFrameFormatName(m_PendingFormat),
        static_cast<unsigned>(m_PendingFormat),
        static_cast<unsigned long long>(PayloadBytes),
        summary.Crc32);
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoDebug] pixels TL=(%u,%u,%u,%u) "
        "TR=(%u,%u,%u,%u) C=(%u,%u,%u,%u) BL=(%u,%u,%u,%u) "
        "BR=(%u,%u,%u,%u) [B,G,R,A]\n",
        summary.TopLeft.Blue, summary.TopLeft.Green,
        summary.TopLeft.Red, summary.TopLeft.Alpha,
        summary.TopRight.Blue, summary.TopRight.Green,
        summary.TopRight.Red, summary.TopRight.Alpha,
        summary.Center.Blue, summary.Center.Green,
        summary.Center.Red, summary.Center.Alpha,
        summary.BottomLeft.Blue, summary.BottomLeft.Green,
        summary.BottomLeft.Red, summary.BottomLeft.Alpha,
        summary.BottomRight.Blue, summary.BottomRight.Green,
        summary.BottomRight.Red, summary.BottomRight.Alpha);
    PANEL_DEBUG_PRINT(
        "[PanelDisplayDriver][VideoDebug] luminance min=%u max=%u avg=%u "
        "nonBlack=%u/%u\n",
        summary.Luminance.Minimum,
        summary.Luminance.Maximum,
        summary.Luminance.Average,
        summary.Luminance.NonBlackSamples,
        summary.Luminance.SampleCount);
}
