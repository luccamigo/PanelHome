package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import kotlinx.coroutines.flow.StateFlow

/** Process-local session API exposed by the Service to presentation code. */
interface PanelSession {
    val uiState: StateFlow<PanelUiState>
    val videoFrameStore: LatestVideoFrameStore
    val h264Decoder: H264DecoderController
    val streamDeckIcons: StreamDeckIconStore

    fun openDesktop()
    fun openSettings()
    fun enterAmbient()
    fun returnToDashboard()
    fun retryDesktop()
    fun updateDeviceProfile(deviceProfile: DeviceProfile?)
    fun onVideoFrameDrawn(generation: Long, sequence: Long)
    fun onVideoRenderError(generation: Long, sequence: Long, stage: String, error: Throwable)
    fun previousTrack()
    fun nextTrack()
    fun togglePlayback()
    fun setVolume(volumePercent: Int)
    fun finishVolumeChange(volumePercent: Int)
    fun toggleVolumeMute()
    fun invokeStreamDeckButton(buttonId: String)
    fun clearStreamDeckFailure()
    fun onDesktopTouch(event: DesktopTouchEvent)
    fun updateMonitoringPreferences(preferences: MonitoringPreferences) {}
    fun setProcessListInteracting(active: Boolean) {}
    fun requestTerminateProcess(key: String) {}
    fun dismissTermination() {}
    fun setUiTouchActive(active: Boolean) {}
    fun markNoticesSeen() {}
    fun dismissNoticeBanner() {}
    fun permissionShown(requestId: String) {}
    fun respondToPermission(requestId: String, decision: String) {}
}
