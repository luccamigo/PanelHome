package com.panelos.panelhome

import android.content.Context
import android.content.Intent
import android.os.Build
import android.view.Surface
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelUiState
import com.panelos.panelhome.presentation.PanelViewModel
import com.panelos.panelhome.session.PanelSessionService
import com.panelos.panelhome.session.PanelSessionServiceTestDependencies
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/** Emulator smoke: Activity -> real Service bind -> fake process-local session. */
@RunWith(AndroidJUnit4::class)
class MainActivitySmokeTest {
    private val context: Context
        get() = ApplicationProvider.getApplicationContext()
    private lateinit var session: InstrumentedFakePanelSession

    @Before
    fun installFakeSession() {
        if (Build.VERSION.SDK_INT >= 33) {
            InstrumentationRegistry.getInstrumentation().uiAutomation
                .executeShellCommand(
                    "pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS",
                )
                .close()
        }
        context.stopService(Intent(context, PanelSessionService::class.java))
        session = InstrumentedFakePanelSession()
        PanelSessionServiceTestDependencies.sessionFactory = { session }
    }

    @After
    fun stopServiceAndResetDependency() {
        context.stopService(Intent(context, PanelSessionService::class.java))
        PanelSessionServiceTestDependencies.reset()
    }

    @Test
    fun activityCreatesAndBindsTheForegroundSessionService() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.eventually { activity ->
                !activity.isFinishing && activity.isSessionBoundForTesting()
            }
        }
    }

    @Test
    fun activityRecreationRebindsAndRetainsItsViewModel() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            var firstActivity: MainActivity? = null
            var firstViewModel: PanelViewModel? = null
            scenario.eventually { activity ->
                firstActivity = activity
                firstViewModel = activity.panelViewModelForTesting()
                activity.isSessionBoundForTesting()
            }

            scenario.recreate()

            scenario.eventually { recreated ->
                recreated.isSessionBoundForTesting() &&
                    recreated !== firstActivity &&
                    recreated.panelViewModelForTesting() === firstViewModel
            }
            scenario.onActivity { recreated -> assertNotSame(firstActivity, recreated) }
        }
    }

    @Test
    fun uiReceivesLiveSessionStateThroughTheServiceBinding() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.eventually { it.isSessionBoundForTesting() }
            session.publishConnected("Instrumented CPU")

            scenario.eventually { activity ->
                val state = activity.panelViewModelForTesting().uiState.value
                state.connectionStatus == ConnectionStatus.CONNECTED &&
                    state.hostName == "INSTRUMENTED-HOST" &&
                    state.panelData.cpu.name == "Instrumented CPU"
            }
        }
    }

    @Test
    fun desktopModeProjectsTheSessionDesktopState() {
        ActivityScenario.launch(MainActivity::class.java).use { scenario ->
            scenario.eventually { it.isSessionBoundForTesting() }
            session.publishConnected("Instrumented CPU")
            scenario.onActivity { it.panelViewModelForTesting().openDesktop() }
            session.publishDesktopRunning()

            scenario.eventually { activity ->
                val state = activity.panelViewModelForTesting().uiState.value
                state.mode == PanelMode.DESKTOP &&
                    state.desktopStatus == DesktopStatus.RUNNING &&
                    session.openDesktopCalls == 1
            }
            scenario.onActivity { activity ->
                assertEquals(
                    DesktopStatus.RUNNING,
                    activity.panelViewModelForTesting().uiState.value.desktopStatus,
                )
            }
        }
    }

    private fun ActivityScenario<MainActivity>.eventually(
        predicate: (MainActivity) -> Boolean,
    ) {
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(8)
        while (System.nanoTime() < deadline) {
            var matched = false
            onActivity { matched = predicate(it) }
            if (matched) return
            Thread.sleep(25)
        }
        onActivity { activity -> assertTrue("Activity state did not converge", predicate(activity)) }
    }
}

internal class InstrumentedFakePanelSession : PanelSession {
    private val mutableState = MutableStateFlow(
        PanelUiState(mode = PanelMode.DASHBOARD, panelData = MockPanelData.initial),
    )
    override val uiState: StateFlow<PanelUiState> = mutableState
    override val videoFrameStore = LatestVideoFrameStore()
    override val h264Decoder: H264DecoderController = InstrumentedNoOpDecoder
    override val streamDeckIcons: StreamDeckIconStore = NoStreamDeckIconStore
    var openDesktopCalls = 0
        private set

    fun publishConnected(cpuName: String) {
        mutableState.value = mutableState.value.copy(
            connectionStatus = ConnectionStatus.CONNECTED,
            hostName = "INSTRUMENTED-HOST",
            panelData = mutableState.value.panelData.copy(cpu = CpuMetrics(name = cpuName)),
        )
    }

    fun publishDesktopRunning() {
        mutableState.value = mutableState.value.copy(
            mode = PanelMode.DESKTOP,
            desktopStatus = DesktopStatus.RUNNING,
            desktopTouchGeneration = 7,
        )
    }

    override fun openDesktop() {
        openDesktopCalls++
        mutableState.value = mutableState.value.copy(
            mode = PanelMode.DESKTOP,
            desktopStatus = DesktopStatus.STARTING,
        )
    }

    override fun openSettings() = Unit
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
}

private data object InstrumentedNoOpDecoder : H264DecoderController {
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
