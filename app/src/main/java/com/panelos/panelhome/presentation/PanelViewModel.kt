package com.panelos.panelhome.presentation

import android.view.Surface
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import kotlinx.coroutines.Job
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

/** Activity-scoped projection of the Service-owned logical PanelOS session. */
class PanelViewModel(
    private val projectionScope: CoroutineScope? = null,
) : ViewModel() {
    private val fallbackFrameStore = LatestVideoFrameStore()
    private val _uiState = MutableStateFlow(
        PanelUiState(mode = PanelMode.DASHBOARD, panelData = MockPanelData.initial),
    )
    val uiState: StateFlow<PanelUiState> = _uiState.asStateFlow()

    private var session: PanelSession? = null
    private var stateJob: Job? = null

    val videoFrameStore: LatestVideoFrameStore
        get() = session?.videoFrameStore ?: fallbackFrameStore
    val h264Decoder: H264DecoderController
        get() = session?.h264Decoder ?: UnboundH264Decoder
    val streamDeckIcons: StreamDeckIconStore
        get() = session?.streamDeckIcons ?: NoStreamDeckIconStore

    fun attachSession(boundSession: PanelSession) {
        if (session === boundSession && stateJob?.isActive == true) return
        stateJob?.cancel()
        session = boundSession
        _uiState.value = boundSession.uiState.value
        stateJob = (projectionScope ?: viewModelScope).launch {
            boundSession.uiState.collect { state ->
                if (session === boundSession) _uiState.value = state
            }
        }
    }

    /** Unbinding removes only the UI projection; it never closes the session. */
    fun detachSession(boundSession: PanelSession? = session) {
        if (session !== boundSession) return
        stateJob?.cancel()
        stateJob = null
        session = null
    }

    fun openDesktop() { session?.openDesktop() }
    fun openSettings() { session?.openSettings() }
    fun enterAmbient() { session?.enterAmbient() }
    fun returnToDashboard() { session?.returnToDashboard() }
    fun retryDesktop() { session?.retryDesktop() }
    fun updateDeviceProfile(deviceProfile: DeviceProfile?) {
        session?.updateDeviceProfile(deviceProfile)
    }
    fun onVideoFrameDrawn(generation: Long, sequence: Long) {
        session?.onVideoFrameDrawn(generation, sequence)
    }
    fun onVideoRenderError(
        generation: Long,
        sequence: Long,
        stage: String,
        error: Throwable,
    ) {
        session?.onVideoRenderError(generation, sequence, stage, error)
    }
    fun previousTrack() { session?.previousTrack() }
    fun nextTrack() { session?.nextTrack() }
    fun togglePlayback() { session?.togglePlayback() }
    fun setVolume(volumePercent: Int) { session?.setVolume(volumePercent) }
    fun finishVolumeChange(volumePercent: Int) {
        session?.finishVolumeChange(volumePercent)
    }
    fun toggleVolumeMute() { session?.toggleVolumeMute() }
    fun invokeStreamDeckButton(buttonId: String) { session?.invokeStreamDeckButton(buttonId) }
    fun clearStreamDeckFailure() { session?.clearStreamDeckFailure() }
    fun onDesktopTouch(event: DesktopTouchEvent) { session?.onDesktopTouch(event) }
    fun updateMonitoringPreferences(preferences: MonitoringPreferences) {
        session?.updateMonitoringPreferences(preferences)
    }
    fun setProcessListInteracting(active: Boolean) { session?.setProcessListInteracting(active) }
    fun requestTerminateProcess(key: String) { session?.requestTerminateProcess(key) }
    fun dismissTermination() { session?.dismissTermination() }
    fun setUiTouchActive(active: Boolean) { session?.setUiTouchActive(active) }
    fun markNoticesSeen() { session?.markNoticesSeen() }
    fun dismissNoticeBanner() { session?.dismissNoticeBanner() }
    fun permissionShown(requestId: String) { session?.permissionShown(requestId) }
    fun respondToPermission(requestId: String, decision: String) { session?.respondToPermission(requestId, decision) }

    override fun onCleared() {
        detachSession()
        fallbackFrameStore.clear()
    }
}

private object UnboundH264Decoder : H264DecoderController {
    override fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    ) = Unit

    override fun attachSurface(generation: Long, surface: Surface) = Unit
    override fun detachSurface(generation: Long, surface: Surface) = Unit
    override fun queuePacket(generation: Long, packet: H264VideoPacket) = Unit
    override fun stopSession() = Unit
    override fun close() = Unit
}
