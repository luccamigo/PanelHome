package com.panelos.panelhome.session

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.IBinder
import android.os.Process
import android.view.Surface
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.panelos.panelhome.MainActivity
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelUiState
import com.panelos.panelhome.presentation.PanelViewModel
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PanelSessionServiceInstrumentedTest {
    private val context: Context = ApplicationProvider.getApplicationContext()

    @Before
    fun grantNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33) {
            InstrumentationRegistry.getInstrumentation().uiAutomation
                .executeShellCommand("pm grant ${context.packageName} android.permission.POST_NOTIFICATIONS")
                .close()
        }
    }

    @After
    fun stopService() {
        PanelSessionServiceTestDependencies.reset()
        context.stopService(Intent(context, PanelSessionService::class.java))
    }

    @Test
    fun foregroundServiceStartsAndBindsWithoutUsbHardware() {
        startService()
        val binding = bindService()

        assertTrue(binding.binder.sessionGeneration >= 1L)
        context.unbindService(binding.connection)
    }

    @Test
    fun destroyedActivityAndViewModelRebindToTheSameServiceSessionOwner() {
        val before = bindService()
        val owner = before.binder.session
        val generation = before.binder.sessionGeneration
        lateinit var firstViewModel: PanelViewModel
        ActivityScenario.launch<MainActivity>(adbActivityIntent()).use { scenario ->
            scenario.onActivity { firstViewModel = it.panelViewModelForTesting() }
        }

        lateinit var secondViewModel: PanelViewModel
        ActivityScenario.launch<MainActivity>(adbActivityIntent()).use { scenario ->
            scenario.onActivity { secondViewModel = it.panelViewModelForTesting() }
            val after = bindService()
            assertNotSame(firstViewModel, secondViewModel)
            assertSame(owner, after.binder.session)
            assertEquals(generation, after.binder.sessionGeneration)
            context.unbindService(after.connection)
        }
        context.unbindService(before.connection)
    }

    /**
     * The behaviour a cable pull depends on: a session can die and be replaced
     * without the process, the Service or the binder going with it.
     *
     * PanelHome is the HOME app. A recovery that needs a new process is a
     * recovery that blanks the tablet's home screen, so "same PID" is not a
     * nicety here - it is the requirement.
     */
    @Test
    fun serviceAndProcessSurviveASessionReplacement() {
        val pidBefore = Process.myPid()
        val sessions = mutableListOf<FakeInstrumentedSession>()
        PanelSessionServiceTestDependencies.sessionFactory = {
            FakeInstrumentedSession().also(sessions::add)
        }
        val binding = bindService()
        val owner = binding.binder.session
        val generationBefore = binding.binder.sessionGeneration

        // A transport failure, as the runtime sees one.
        sessions.forEach { it.publish(ConnectionStatus.DISCONNECTED) }
        startService()

        assertSame(owner, binding.binder.session)
        assertEquals(pidBefore, Process.myPid())
        assertNotEquals(UsbRecoveryState.STOPPED, binding.binder.usbState)
        assertTrue(binding.binder.sessionGeneration >= generationBefore)
        context.unbindService(binding.connection)
    }

    @Test
    fun theUsbLifecycleStaysOwnedByTheServiceAcrossActivityRecreation() {
        val binding = bindService()
        val stateBefore = binding.binder.usbState

        ActivityScenario.launch<MainActivity>(adbActivityIntent()).use { }
        ActivityScenario.launch<MainActivity>(adbActivityIntent()).use { }

        // Nothing an Activity does may move the USB lifecycle: it does not own
        // the descriptor and cannot decide when one is opened.
        assertEquals(stateBefore, binding.binder.usbState)
        assertEquals(0L, binding.binder.usbMetrics.usbPhysicalDetaches)
        context.unbindService(binding.connection)
    }

    private fun startService() {
        val intent = Intent(context, PanelSessionService::class.java).putExtra(
            PanelSessionService.TRANSPORT_EXTRA,
            "adb",
        )
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(intent)
        } else {
            context.startService(intent)
        }
    }

    private fun adbActivityIntent() = Intent(context, MainActivity::class.java).putExtra(
        PanelSessionService.TRANSPORT_EXTRA,
        "adb",
    )

    private fun bindService(): Binding {
        startService()
        val latch = CountDownLatch(1)
        var binder: PanelSessionService.LocalBinder? = null
        val connection = object : ServiceConnection {
            override fun onServiceConnected(name: ComponentName, service: IBinder) {
                binder = service as PanelSessionService.LocalBinder
                latch.countDown()
            }

            override fun onServiceDisconnected(name: ComponentName) = Unit
        }
        assertTrue(
            context.bindService(
                Intent(context, PanelSessionService::class.java),
                connection,
                Context.BIND_AUTO_CREATE,
            ),
        )
        assertTrue("Service binding timed out", latch.await(10, TimeUnit.SECONDS))
        return Binding(connection, requireNotNull(binder))
    }

    private data class Binding(
        val connection: ServiceConnection,
        val binder: PanelSessionService.LocalBinder,
    )
}

private class FakeInstrumentedSession : PanelSession {
    private val mutableState = MutableStateFlow(
        PanelUiState(mode = PanelMode.DASHBOARD, panelData = MockPanelData.initial),
    )
    override val uiState: StateFlow<PanelUiState> = mutableState
    override val videoFrameStore = LatestVideoFrameStore()
    override val h264Decoder: H264DecoderController = NoInstrumentedDecoder
    override val streamDeckIcons = NoStreamDeckIconStore

    fun publish(status: ConnectionStatus) {
        mutableState.value = mutableState.value.copy(connectionStatus = status)
    }

    override fun openDesktop() = Unit
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

private object NoInstrumentedDecoder : H264DecoderController {
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
