package com.panelos.panelhome.session

import android.view.Surface
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.transport.AccessoryHandle
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionPhase
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelUiState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch

internal enum class SessionTransportKind {
    ADB_TCP,
    NATIVE_USB,
    WIFI,
}

internal class SessionTransportConfig(
    val kind: SessionTransportKind,
    val adbAuthenticationToken: ByteArray? = null,
    /**
     * Already open, and owned by this config until a session takes it. The
     * lifecycle opens the descriptor before the session exists precisely so that
     * exactly one component can decide when an accessory may be opened.
     */
    val accessory: AccessoryHandle? = null,
    /** The physical generation this config belongs to; 0 for ADB/TCP. */
    val nativeGeneration: Long = 0,
    /** The paired PC for a WIFI session. */
    val wifiHost: com.panelos.panelhome.data.wifi.WifiPairedHost? = null,
) {
    /**
     * Compares only what selects a carrier. The descriptor and the generation
     * are deliberately excluded: two configs for the same carrier are the same
     * request even when one of them carries a fresh accessory.
     */
    fun isEquivalentTo(other: SessionTransportConfig): Boolean =
        kind == other.kind && when {
            kind == SessionTransportKind.WIFI -> wifiHost?.hostId.contentEqualsNullable(other.wifiHost?.hostId)
            adbAuthenticationToken == null -> other.adbAuthenticationToken == null
            other.adbAuthenticationToken == null -> false
            else -> adbAuthenticationToken.contentEquals(other.adbAuthenticationToken)
        }

    private fun ByteArray?.contentEqualsNullable(other: ByteArray?) = if (this == null || other == null) this == other else contentEquals(other)
}

internal class PanelSessionInstance(
    val session: PanelSession,
    val close: () -> Unit,
)

internal fun interface PanelSessionFactory {
    fun create(config: SessionTransportConfig): PanelSessionInstance
}

/**
 * Stable, process-local owner behind the Service binder.
 *
 * A physical USB recovery replaces the concrete coordinator, but replacement is
 * close-before-open and this object remains the single API observed by every
 * Activity/ViewModel binding. It performs replacements; it does not decide them.
 * That decision belongs to [UsbLifecycleController], which is the only component
 * that knows whether an accessory exists to reopen - the distinction the old
 * timer-based recovery here could not make, and the reason a cable pull used to
 * end in a reopen loop against nothing.
 */
internal class PanelSessionServiceRuntime(
    private val scope: CoroutineScope,
    private val factory: PanelSessionFactory,
    private val log: (String, Throwable?) -> Unit = { _, _ -> },
    private val monitoringClock: () -> Long = { System.nanoTime() / 1_000_000 },
    private val saveMonitoringPreferences: (com.panelos.panelhome.presentation.MonitoringPreferences) -> Unit = {},
) : PanelSession {
    private val lock = Any()
    private val fallbackFrameStore = LatestVideoFrameStore()
    private val _uiState = MutableStateFlow(disconnectedState())
    override val uiState: StateFlow<PanelUiState> = _uiState.asStateFlow()

    private var active: PanelSessionInstance? = null
    private var activeConfig: SessionTransportConfig? = null
    private var stateJob: Job? = null
    private var closed = false
    private var phase = ConnectionPhase.DISABLED

    val sessionGeneration: Long
        get() = synchronized(lock) { generation }
    private var generation = 0L

    /** The physical USB generation the live session belongs to; 0 when none. */
    val activeNativeGeneration: Long
        get() = synchronized(lock) { activeConfig?.nativeGeneration ?: 0L }

    override val videoFrameStore: LatestVideoFrameStore
        get() = synchronized(lock) { active?.session?.videoFrameStore } ?: fallbackFrameStore
    override val h264Decoder: H264DecoderController
        get() = synchronized(lock) { active?.session?.h264Decoder } ?: InactiveH264Decoder
    override val streamDeckIcons: StreamDeckIconStore
        get() = synchronized(lock) { active?.session?.streamDeckIcons } ?: NoStreamDeckIconStore

    /**
     * Never call this on the main thread. Replacing a session closes logical
     * channels and a physical descriptor, and after a cable pull that work has
     * to be allowed to take as long as its own bounds permit.
     */
    fun start(config: SessionTransportConfig, forceRestart: Boolean = false) {
        val previous = synchronized(lock) {
            if (closed) {
                config.accessory?.close()
                return
            }
            if (!forceRestart && active != null && activeConfig?.isEquivalentTo(config) == true) {
                // The descriptor would otherwise be stranded: nothing else holds
                // a reference to it once this call declines to use it.
                config.accessory?.close()
                return
            }
            stateJob?.cancel()
            stateJob = null
            active.also {
                active = null
                activeConfig = config
                _uiState.value = disconnectedState().copy(monitoring = retainedMonitoring(),
                    integrations = _uiState.value.integrations.disconnected(monitoringClock()))
            }
        }
        previous?.close?.invoke()

        val created = try {
            factory.create(config)
        } catch (error: Throwable) {
            log("Unable to create PanelOS session", error)
            config.accessory?.close()
            return
        }
        synchronized(lock) {
            if (closed || active != null || activeConfig?.isEquivalentTo(config) != true) {
                created.close()
                return
            }
            active = created
            generation++
            stateJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                created.session.uiState.collect { state ->
                    if (synchronized(lock) { active === created }) {
                        _uiState.value = state.copy(connectionPhase = phase)
                    }
                }
            }
        }
    }

    /**
     * Close-before-open, without an open.
     *
     * This is the state a physical detach leaves the panel in: the Service, the
     * binder and every Activity binding survive, and nothing holds a descriptor.
     */
    fun stopSession(onlyKind: SessionTransportKind? = null) {
        val previous = synchronized(lock) {
            if (closed) return
            // Scoped on purpose: the USB lifecycle must never tear down a
            // development ADB/TCP session that replaced it in the meantime.
            if (onlyKind != null && activeConfig?.kind != onlyKind) return
            stateJob?.cancel()
            stateJob = null
            activeConfig = null
            active.also {
                active = null
                _uiState.value = disconnectedState().copy(connectionPhase = phase, monitoring = retainedMonitoring(),
                    integrations = _uiState.value.integrations.disconnected(monitoringClock()))
            }
        }
        previous?.close?.invoke()
        fallbackFrameStore.clear()
    }

    /** Overlay published on top of whatever the concrete session reports. */
    fun publishPhase(next: ConnectionPhase) {
        synchronized(lock) {
            if (phase == next) return
            phase = next
        }
        _uiState.value = _uiState.value.copy(connectionPhase = next)
    }

    fun shutdown() {
        val previous = synchronized(lock) {
            if (closed) return
            closed = true
            stateJob?.cancel()
            stateJob = null
            active.also { active = null }
        }
        previous?.close?.invoke()
        fallbackFrameStore.clear()
        _uiState.value = disconnectedState()
    }

    private fun activeSession(): PanelSession? = synchronized(lock) { active?.session }
    private fun retainedMonitoring() = _uiState.value.monitoring.disconnected(monitoringClock())

    override fun openDesktop() { activeSession()?.openDesktop() }
    override fun openSettings() { activeSession()?.openSettings() }
    override fun enterAmbient() { activeSession()?.enterAmbient() }
    override fun returnToDashboard() { activeSession()?.returnToDashboard() }
    override fun retryDesktop() { activeSession()?.retryDesktop() }
    override fun updateDeviceProfile(deviceProfile: DeviceProfile?) {
        activeSession()?.updateDeviceProfile(deviceProfile)
    }
    override fun onVideoFrameDrawn(generation: Long, sequence: Long) {
        activeSession()?.onVideoFrameDrawn(generation, sequence)
    }
    override fun onVideoRenderError(
        generation: Long,
        sequence: Long,
        stage: String,
        error: Throwable,
    ) {
        activeSession()?.onVideoRenderError(generation, sequence, stage, error)
    }
    override fun previousTrack() { activeSession()?.previousTrack() }
    override fun nextTrack() { activeSession()?.nextTrack() }
    override fun togglePlayback() { activeSession()?.togglePlayback() }
    override fun setVolume(volumePercent: Int) { activeSession()?.setVolume(volumePercent) }
    override fun finishVolumeChange(volumePercent: Int) {
        activeSession()?.finishVolumeChange(volumePercent)
    }
    override fun toggleVolumeMute() { activeSession()?.toggleVolumeMute() }
    override fun invokeStreamDeckButton(buttonId: String) {
        activeSession()?.invokeStreamDeckButton(buttonId)
    }
    override fun clearStreamDeckFailure() { activeSession()?.clearStreamDeckFailure() }
    override fun onDesktopTouch(event: DesktopTouchEvent) {
        activeSession()?.onDesktopTouch(event)
    }
    override fun updateMonitoringPreferences(preferences: com.panelos.panelhome.presentation.MonitoringPreferences) {
        val session = activeSession()
        if (session != null) session.updateMonitoringPreferences(preferences) else {
            _uiState.value = _uiState.value.copy(monitoring = _uiState.value.monitoring.copy(preferences = preferences))
            saveMonitoringPreferences(preferences)
        }
    }
    override fun requestTerminateProcess(key: String) { activeSession()?.requestTerminateProcess(key) }
    override fun dismissTermination() { activeSession()?.dismissTermination() }
    override fun setProcessListInteracting(active: Boolean) {
        activeSession()?.setProcessListInteracting(active)
    }
    override fun setUiTouchActive(active: Boolean) { activeSession()?.setUiTouchActive(active) }
    override fun markNoticesSeen() { activeSession()?.markNoticesSeen() }
    override fun dismissNoticeBanner() { activeSession()?.dismissNoticeBanner() }
    override fun permissionShown(requestId: String) { activeSession()?.permissionShown(requestId) }
    override fun respondToPermission(requestId: String, decision: String) { activeSession()?.respondToPermission(requestId, decision) }

    private companion object {
        fun disconnectedState() = PanelUiState(
            mode = PanelMode.DASHBOARD,
            panelData = MockPanelData.initial,
        )
    }
}

private object InactiveH264Decoder : H264DecoderController {
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
