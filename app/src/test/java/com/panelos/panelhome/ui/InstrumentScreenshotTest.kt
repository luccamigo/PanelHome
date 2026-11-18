package com.panelos.panelhome.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import com.github.takahirom.roborazzi.captureRoboImage
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.domain.model.*
import com.panelos.panelhome.domain.protocol.*
import com.panelos.panelhome.presentation.*
import com.panelos.panelhome.ui.dashboard.DashboardScreen
import com.panelos.panelhome.ui.home.*
import com.panelos.panelhome.ui.integrations.*
import com.panelos.panelhome.ui.loopmedia.LoopMediaScreen
import com.panelos.panelhome.ui.monitoring.*
import com.panelos.panelhome.ui.streamdeck.StreamDeckPageScreen
import com.panelos.panelhome.ui.theme.*
import java.io.File
import java.util.TimeZone
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** Synthetic input is confined to tests. Screens and chrome are the production composables. */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [35], qualifiers = "w960dp-h600dp-tvdpi")
class InstrumentScreenshotTest {
    @get:Rule val compose = createComposeRule()

    // The goldens were recorded with the chart axis label "desde 21:00" (epoch + 1 s in America/Sao_Paulo).
    // SimpleDateFormat follows the JVM zone, so a UTC runner would render "desde 00:00". Pin the zone here.
    private var previousZone: TimeZone? = null
    @Before fun pinTimeZone() { previousZone = TimeZone.getDefault(); TimeZone.setDefault(TimeZone.getTimeZone("America/Sao_Paulo")) }
    @After fun restoreTimeZone() { previousZone?.let(TimeZone::setDefault) }
    private val layout = HomeLayout(1, hasComputer = true, hasAlerts = true, hasMedia = true)
    private val series = List(120) { (110 + 30 * kotlin.math.sin(it / 9.0)).toFloat() }
    private val measuring = FpsState.Measuring("Stellar Drift", 144, series)
    private val data = MockPanelData.initial.copy(
        cpu = CpuMetrics("Ryzen 7 5800X", 62.0, 38.8, 4.11),
        gpu = GpuMetrics(name = "RTX 4070", temperatureCelsius = 72.0, usagePercent = 61.0,
            memoryUsedGb = 5.2, memoryTotalGb = 12.0),
        ram = RamMetrics(17.3, 32.0, 54.0), volume = VolumeState(true, 20, false),
    )
    private val monitoring = MonitoringState(connected = true, computerAvailable = true, processesAvailable = true,
        now = 120_000, lastTelemetryAt = 120_000, lastNetworkAt = 120_000,
        telemetry = List(120) { TelemetrySample(it * 1000L + 1000, it * 1000L, 38.8, 54.0, 61.0) },
        network = List(120) { NetworkSample(it * 1000L + 1000, it * 1000L,
            NetworkRatesMessage(it * 1000L, 3_200_000.0, 400_000.0)) })

    private fun render(name: String, page: Int = 0, status: ConnectionStatus = ConnectionStatus.CONNECTED,
        content: @Composable () -> Unit) {
        compose.setContent {
            PanelHomeTheme {
                Box(Modifier.size(960.dp, 600.dp).background(PanelBackground)) {
                    PanelHomePager(layout, status, {}, {}, requestedPage = page,
                        topBarStart = { if (it == 0) ClockBlock(com.panelos.panelhome.ui.components.PanelDateTime("22:41", "18 de novembro", "quarta-feira", "18 de novembro")) else PageTitle("Instrumento") },
                    ) { index, _ -> if (index == page) content() }
                }
            }
        }
        compose.waitForIdle()
        val dir = File(System.getProperty("panel.screenshot.dir") ?: "src/test/screenshots").apply { mkdirs() }
        compose.onRoot().captureRoboImage(File(dir, "$name.png").path)
    }

    private fun dashboard(name: String, fps: FpsState, media: Boolean = false,
        status: ConnectionStatus = ConnectionStatus.CONNECTED, temperature: Boolean = true) {
        val d = data.copy(cpu = data.cpu.copy(temperatureCelsius = if (temperature) 62.0 else null),
            media = if (!media) data.media else data.media.copy(available = true,
                track = MediaTrack("demo", "Spotify", "Midnight City", "M83", null), isPlaying = true,
                progress = MediaProgress(85_000, 244_000), capabilities = MediaCapabilities(true, true, true)))
        render(name, status = status) {
            DashboardScreen(d, monitoring.copy(connected = status == ConnectionStatus.CONNECTED), fps, status,
                {}, {}, {}, {}, {}, {}, {}, {}, {})
        }
        if (fps == FpsState.Unavailable) compose.onNodeWithText("FPS", useUnmergedTree = true).assertDoesNotExist()
        if (fps == FpsState.NoApp) compose.onNodeWithText("Nenhum jogo em primeiro plano", useUnmergedTree = true).assertExists()
        if (status == ConnectionStatus.DISCONNECTED) compose.onNodeWithText("Tentar de novo").assertIsDisplayed()
    }

    @Test fun painelMeasuring() = dashboard("painel-sem-midia", measuring)
    @Test fun painelNoApp() = dashboard("painel-sem-jogo", FpsState.NoApp)
    @Test fun painelUnavailable() = dashboard("painel-fps-indisponivel", FpsState.Unavailable)
    @Test fun painelMediaMeasuring() = dashboard("painel-com-midia", measuring, true)
    @Test fun painelMediaNoApp() = dashboard("painel-midia-sem-jogo", FpsState.NoApp, true)
    @Test fun painelMediaUnavailable() = dashboard("painel-midia-fps-indisponivel", FpsState.Unavailable, true)
    @Test fun painelDisconnected() = dashboard("painel-desconectado", measuring, status = ConnectionStatus.DISCONNECTED)
    @Test fun painelConnecting() = dashboard("painel-conectando", measuring, status = ConnectionStatus.CONNECTING)
    @Test fun painelWithoutTemperature() = dashboard("painel-sem-temperatura", measuring, temperature = false)
    @Test fun zeroIsARealFpsValue() = dashboard("painel-fps-zero", FpsState.Measuring("Stellar Drift", 0, listOf(0f)))

    private fun computer(name: String, fps: FpsState) = render(name, page = layout.computerPage!!) {
        ComputerScreen(monitoring, data, fps, ConnectionStatus.CONNECTED, {}, {}, {})
    }
    @Test fun computerMeasuring() = computer("computador", measuring)
    @Test fun computerNoApp() = computer("computador-sem-jogo", FpsState.NoApp)
    @Test fun computerUnavailable() = computer("computador-fps-indisponivel", FpsState.Unavailable)

    @Test fun processes() = render("processos", page = layout.computerPage!!) {
        ProcessesScreen(monitoring.copy(receivedProcesses = true, processRows = listOf(
            ProcessRow("demo", ProcessItem("Stellar Drift", pid = 42, cpuPercent = 18.4, memoryBytes = 2_000_000_000)),
            ProcessRow("system", ProcessItem("Sistema", cpuPercent = 3.2, memoryBytes = 150_000_000)),
        )), data, {}, {})
    }
    @Test fun shortcuts() = render("atalhos", page = 1) {
        StreamDeckPageScreen(StreamDeckPage("demo", "Jogos", 3, 4, List(12) { i ->
            StreamDeckButton("b$i", i / 4, i % 4, listOf("Desktop", "Jogo", "Música", "Microfone")[i % 4],
                enabled = i != 10)
        }), StreamDeckUiState(pendingButtonId = "b0", failedButtonId = "b1"), NoStreamDeckIconStore, {})
    }

    private fun notices(name: String, populated: Boolean) {
        val notice = NoticeUi(LocalNotice("demo", "Build concluído", "O projeto está pronto para revisar.", "success",
            null, null, 5, 0, 999999), 119_000, 125_000)
        render(name, page = layout.alertsPage!!) {
            NoticesScreen(LocalIntegrationsState(connected = true, enabled = true, now = 120_000,
                history = if (populated) listOf(notice) else emptyList(),
                values = if (populated) listOf(PublishedValueUi(LocalPublishedValue("Build", "42", null, "success", 0, null), 119_000, null)) else emptyList()),
                ConnectionStatus.CONNECTED, emptyMap(), {}, {}, {}, {})
        }
    }
    @Test fun noticesPopulated() = notices("avisos", true)
    @Test fun noticesEmpty() = notices("avisos-vazio", false)
    @Test fun noticesDisabled() = render("avisos-desligados", page = layout.alertsPage!!) {
        NoticesScreen(LocalIntegrationsState(connected = true), ConnectionStatus.CONNECTED, emptyMap(), {}, {}, {}, {})
    }
    @Test fun noticeBannerOverMediaDoesNotRevealHud() {
        render("midia-aviso-sem-hud", page = layout.mediaPage!!) {
            Box(Modifier.fillMaxSize().background(Color(0xFF1B3A5C))) {
                NoticeBanner(LocalIntegrationsState(now = 120_000, banner = NoticeUi(
                    LocalNotice("demo", "Build concluído", "Projeto pronto para revisar", "success", null, null, 5, 0, 999999),
                    119_000, 125_000)), false, emptyMap(), {}, {}, {})
            }
        }
        compose.onNodeWithText("PC conectado").assertDoesNotExist()
        compose.onNodeWithText("Painel").assertDoesNotExist()
    }
    @Test fun mediaErrorOffersExit() {
        render("midia-erro", page = layout.mediaPage!!) {
            LoopMediaScreen(LoopMediaUiState(phase = LoopMediaPhase.ERROR), false)
        }
        compose.onNodeWithText("Voltar ao painel").assertIsDisplayed()
    }
    @Test fun mediaDownloading() = render("midia-recebendo", page = layout.mediaPage!!) {
        LoopMediaScreen(LoopMediaUiState(phase = LoopMediaPhase.DOWNLOADING, progressPercent = 64), false)
    }
}
