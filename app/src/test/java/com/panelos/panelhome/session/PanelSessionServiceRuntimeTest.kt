package com.panelos.panelhome.session

import android.view.Surface
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.transport.AccessoryHandle
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionPhase
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelUiState
import com.panelos.panelhome.presentation.PanelViewModel
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class PanelSessionServiceRuntimeTest {
    @Test
    fun `physical detach retains monitoring history and marks a gap without retaining live values`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        runtime.start(SessionTransportConfig(SessionTransportKind.ADB_TCP))
        val sample = com.panelos.panelhome.presentation.TelemetrySample(1_000, 2_000, cpu = 40.0)
        sessions.single().publishMonitoring(com.panelos.panelhome.presentation.MonitoringState(
            computerAvailable = true, connected = true, telemetry = listOf(sample), lastTelemetryAt = 1_000))
        runtime.stopSession()
        assertTrue(runtime.uiState.value.monitoring.computerAvailable)
        assertEquals(sample, runtime.uiState.value.monitoring.telemetry.first())
        assertEquals(false, runtime.uiState.value.monitoring.connected)
        assertEquals(1, runtime.uiState.value.monitoring.gaps.size)
        runtime.shutdown()
        scope.cancel()
    }
    @Test
    fun `monitoring preferences and touch freeze cross the binder runtime`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        runtime.start(SessionTransportConfig(SessionTransportKind.ADB_TCP))
        val viewModel = PanelViewModel(projectionScope = scope)
        viewModel.attachSession(runtime)
        val preferences = com.panelos.panelhome.presentation.MonitoringPreferences(windowMinutes = 15)
        viewModel.updateMonitoringPreferences(preferences)
        viewModel.setProcessListInteracting(true)
        assertEquals(preferences, sessions.single().monitoringPreferences)
        assertTrue(sessions.single().lastInteractingValue)
        viewModel.detachSession(runtime)
        runtime.shutdown()
        scope.cancel()
    }
    @Test
    fun `destroyed ViewModel rebinds to the same session owner and current state`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        runtime.start(SessionTransportConfig(SessionTransportKind.ADB_TCP))
        val concreteOwner = sessions.single()
        concreteOwner.publish(ConnectionStatus.CONNECTED)

        val firstViewModel = PanelViewModel(projectionScope = scope)
        firstViewModel.attachSession(runtime)
        assertEquals(ConnectionStatus.CONNECTED, firstViewModel.uiState.value.connectionStatus)
        firstViewModel.detachSession(runtime)

        val recreatedViewModel = PanelViewModel(projectionScope = scope)
        recreatedViewModel.attachSession(runtime)

        assertSame(concreteOwner.videoFrameStore, recreatedViewModel.videoFrameStore)
        assertEquals(ConnectionStatus.CONNECTED, recreatedViewModel.uiState.value.connectionStatus)
        assertEquals(1, sessions.size)
        assertEquals(1L, runtime.sessionGeneration)
        runtime.shutdown()
        scope.cancel()
    }

    @Test
    fun `stopping the session leaves the runtime and its binder alive`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        val handle = countingHandle()
        runtime.start(
            SessionTransportConfig(
                kind = SessionTransportKind.NATIVE_USB,
                accessory = handle.first,
                nativeGeneration = 7,
            ),
        )
        sessions.single().publish(ConnectionStatus.CONNECTED)
        assertEquals(7L, runtime.activeNativeGeneration)

        runtime.stopSession()

        // This is the state a cable pull leaves behind: no session, no
        // descriptor, and a runtime every Activity binding still observes.
        assertTrue(sessions.single().closed)
        assertEquals(0L, runtime.activeNativeGeneration)
        assertEquals(ConnectionStatus.DISCONNECTED, runtime.uiState.value.connectionStatus)
        assertNull(runtime.uiState.value.hostName)
        runtime.shutdown()
        scope.cancel()
    }

    @Test
    fun `the USB lifecycle never stops a development session that replaced it`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        runtime.start(SessionTransportConfig(SessionTransportKind.ADB_TCP))

        runtime.stopSession(SessionTransportKind.NATIVE_USB)

        assertEquals(false, sessions.single().closed)
        runtime.shutdown()
        scope.cancel()
    }

    @Test
    fun `a descriptor the runtime declines to use is released rather than stranded`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        val first = countingHandle()
        runtime.start(
            SessionTransportConfig(
                kind = SessionTransportKind.NATIVE_USB,
                accessory = first.first,
                nativeGeneration = 1,
            ),
        )
        val declined = countingHandle()

        // Same carrier, no force: the runtime keeps the session it has, so
        // nothing would own this descriptor. `/dev/usb_accessory` is
        // single-open, so leaking it means no later attach can open anything.
        runtime.start(
            SessionTransportConfig(
                kind = SessionTransportKind.NATIVE_USB,
                accessory = declined.first,
                nativeGeneration = 2,
            ),
        )

        assertEquals(1, declined.second.get())
        assertEquals(1, sessions.size)
        runtime.shutdown()
        scope.cancel()
    }

    @Test
    fun `the published phase survives a session replacement`() {
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
        val sessions = mutableListOf<FakeServiceSession>()
        val runtime = runtime(scope, sessions)
        runtime.publishPhase(ConnectionPhase.WAITING_FOR_USB)
        assertEquals(ConnectionPhase.WAITING_FOR_USB, runtime.uiState.value.connectionPhase)

        runtime.start(
            SessionTransportConfig(
                kind = SessionTransportKind.NATIVE_USB,
                accessory = countingHandle().first,
                nativeGeneration = 3,
            ),
        )
        sessions.single().publish(ConnectionStatus.CONNECTED)

        assertEquals(ConnectionPhase.WAITING_FOR_USB, runtime.uiState.value.connectionPhase)
        runtime.publishPhase(ConnectionPhase.CONNECTED)
        assertEquals(ConnectionPhase.CONNECTED, runtime.uiState.value.connectionPhase)
        runtime.shutdown()
        scope.cancel()
    }

    private fun runtime(
        scope: CoroutineScope,
        sessions: MutableList<FakeServiceSession>,
    ) = PanelSessionServiceRuntime(
        scope = scope,
        factory = PanelSessionFactory {
            FakeServiceSession().also(sessions::add).let { session ->
                PanelSessionInstance(session = session, close = session::markClosed)
            }
        },
    )

    private fun countingHandle(): Pair<AccessoryHandle, AtomicInteger> {
        val closes = AtomicInteger()
        val handle = AccessoryHandle(
            input = ByteArrayInputStream(ByteArray(0)),
            output = ByteArrayOutputStream(),
            closeDescriptor = { closes.incrementAndGet() },
        )
        return handle to closes
    }
}

private class FakeServiceSession : PanelSession {
    var monitoringPreferences: com.panelos.panelhome.presentation.MonitoringPreferences? = null
    var lastInteractingValue = false
    override fun updateMonitoringPreferences(preferences: com.panelos.panelhome.presentation.MonitoringPreferences) {
        monitoringPreferences = preferences
    }
    override fun setProcessListInteracting(active: Boolean) { lastInteractingValue = active }
    private val mutableState = MutableStateFlow(disconnected())
    override val uiState: StateFlow<PanelUiState> = mutableState
    override val videoFrameStore = LatestVideoFrameStore()
    override val h264Decoder: H264DecoderController = FakeServiceDecoder
    override val streamDeckIcons = NoStreamDeckIconStore
    var closed = false
        private set

    fun publish(status: ConnectionStatus) {
        mutableState.value = mutableState.value.copy(connectionStatus = status)
    }
    fun publishMonitoring(state: com.panelos.panelhome.presentation.MonitoringState) {
        mutableState.value = mutableState.value.copy(monitoring = state)
    }

    fun markClosed() {
        closed = true
        videoFrameStore.clear()
    }

    override fun openDesktop() = Unit
    override fun openSettings() { mutableState.value = mutableState.value.copy(mode = PanelMode.SETTINGS) }
    override fun enterAmbient() = Unit
    override fun returnToDashboard() = Unit
    override fun retryDesktop() = Unit
    override fun updateDeviceProfile(deviceProfile: DeviceProfile?) = Unit
    override fun onVideoFrameDrawn(generation: Long, sequence: Long) = Unit
    override fun onVideoRenderError(
        generation: Long,
        sequence: Long,
        stage: String,
        error: Throwable,
    ) = Unit
    override fun previousTrack() = Unit
    override fun nextTrack() = Unit
    override fun togglePlayback() = Unit
    override fun setVolume(volumePercent: Int) = Unit
    override fun finishVolumeChange(volumePercent: Int) = Unit
    override fun toggleVolumeMute() = Unit
    override fun invokeStreamDeckButton(buttonId: String) = Unit
    override fun clearStreamDeckFailure() = Unit
    override fun onDesktopTouch(event: DesktopTouchEvent) = Unit

    private companion object {
        fun disconnected() = PanelUiState(
            mode = PanelMode.DASHBOARD,
            panelData = MockPanelData.initial,
        )
    }
}

private object FakeServiceDecoder : H264DecoderController {
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
