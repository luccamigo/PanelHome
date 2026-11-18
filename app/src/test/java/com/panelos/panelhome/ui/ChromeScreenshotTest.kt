package com.panelos.panelhome.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.onRoot
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.click
import androidx.compose.ui.test.swipeRight
import androidx.compose.ui.unit.dp
import com.github.takahirom.roborazzi.captureRoboImage
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.ui.components.BannerAction
import com.panelos.panelhome.ui.components.BannerTone
import com.panelos.panelhome.ui.components.MetricCard
import com.panelos.panelhome.ui.components.MetricCardContent
import com.panelos.panelhome.ui.components.PanelButton
import com.panelos.panelhome.ui.components.PanelButtonStyle
import com.panelos.panelhome.ui.components.SegmentedControl
import com.panelos.panelhome.ui.components.StatusBanner
import com.panelos.panelhome.ui.home.HomeLayout
import com.panelos.panelhome.ui.home.PanelHomePager
import com.panelos.panelhome.ui.theme.PanelBackground
import com.panelos.panelhome.ui.theme.PanelHomeTheme
import com.panelos.panelhome.ui.theme.PanelType
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/**
 * Host-side renders of the redesign base on a 960x600 dp frame (the test tablet at
 * 1280x800 px, tvdpi). They draw the real pager, bottom bar, top bar and base
 * components with made-up data, and check the Mídia chrome behaviour. Fonts are
 * the JVM's, not the tablet's Roboto: widths differ slightly.
 */
@RunWith(RobolectricTestRunner::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [35], qualifiers = "w960dp-h600dp-tvdpi")
class ChromeScreenshotTest {
    @get:Rule
    val compose = createComposeRule()

    private fun shot(name: String) {
        val dir = File(System.getProperty("panel.screenshot.dir") ?: "src/test/screenshots")
        dir.mkdirs()
        compose.onRoot().captureRoboImage(File(dir, "$name.png").path)
    }

    private val full = HomeLayout(streamDeckPageCount = 2, hasComputer = true, hasAlerts = true, hasMedia = true)

    private fun cpuSeries() = List(120) { i -> (30 + 25 * kotlin.math.sin(i / 9.0)).toFloat() }

    private fun card(label: String, value: String?, unit: String?, caption: String?, secondary: String? = null) = MetricCardContent(
        label = label, value = value, unit = unit, secondary = secondary, caption = caption,
        series = if (value == null) emptyList() else cpuSeries(), description = "$label $value",
    )

    @Composable
    private fun FakePainel(stale: Boolean = false) {
        val label = if (stale) "há 2 min" else null
        Column(Modifier.fillMaxSize().padding(horizontal = 24.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                MetricCard(card("CPU", "38,8", "%", "4,11 GHz", "Ryzen 7 5800X"), Modifier.weight(1f).fillMaxSize(), staleLabel = label)
                MetricCard(card("GPU", "61,0", "%", "72 °C", "RTX 4070"), Modifier.weight(1f).fillMaxSize(), staleLabel = label)
                MetricCard(card("RAM", "17,3", "GB", "de 32 GB"), Modifier.weight(1f).fillMaxSize(), staleLabel = label)
                MetricCard(card("VRAM", null, null, null), Modifier.weight(1f).fillMaxSize(), staleLabel = label)
            }
            Row(Modifier.weight(0.8f), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                MetricCard(card("REDE", "3,2", "MB/s", "↑ 0,4 MB/s"), Modifier.weight(3f).fillMaxSize(), staleLabel = label)
                PanelButton("Abrir Desktop", {}, Modifier.weight(1f).fillMaxSize(), iconRes = com.panelos.panelhome.R.drawable.ic_desktop)
            }
        }
    }

    @Composable
    private fun Frame(status: ConnectionStatus, startAt: Int? = null, onVisible: (Int) -> Unit = {}, page: @Composable (Int) -> Unit) {
        PanelHomeTheme {
            Box(Modifier.size(960.dp, 600.dp).background(PanelBackground)) {
                PanelHomePager(
                    layout = full,
                    connectionStatus = status,
                    onOpenSettings = {},
                    onEnterAmbient = {},
                    requestedPage = startAt,
                    alertsUnseen = true,
                ) { index, visible ->
                    if (visible) onVisible(index)
                    page(index)
                }
            }
        }
    }

    @Composable
    private fun FakeMedia() {
        Box(
            Modifier.fillMaxSize().background(Brush.linearGradient(listOf(Color(0xFF1B3A5C), Color(0xFFB5527A), Color(0xFFF2A65A)))),
            contentAlignment = Alignment.Center,
        ) { Text("(mídia de exemplo)", style = PanelType.body, color = Color.White) }
    }

    @Test
    fun painelWithBars() {
        compose.setContent { Frame(ConnectionStatus.CONNECTED) { if (it == 0) FakePainel() } }
        compose.waitForIdle()
        shot("base-painel")
        compose.onNodeWithText("Painel").assertExists()
        compose.onNodeWithText("Atalhos").assertExists()
        compose.onNodeWithText("Computador").assertExists()
        compose.onNodeWithText("Avisos").assertExists()
        compose.onNodeWithText("Mídia").assertExists()
        compose.onNodeWithText("PC conectado").assertExists()
    }

    @Test
    fun disconnectedKeepsAnExitAndDimsTheValues() {
        compose.setContent {
            Frame(ConnectionStatus.DISCONNECTED) {
                if (it == 0) Column(Modifier.fillMaxSize().padding(horizontal = 24.dp)) {
                    StatusBanner(
                        title = "PC desconectado",
                        message = "Confira se o computador está ligado e se o cabo USB está conectado.",
                        tone = BannerTone.Disconnected,
                        primary = BannerAction("Tentar de novo", {}, com.panelos.panelhome.R.drawable.ic_refresh),
                        secondary = BannerAction("Configurações", {}),
                    )
                    Box(Modifier.height(12.dp))
                    Box(Modifier.weight(1f)) { FakePainel(stale = true) }
                }
            }
        }
        compose.waitForIdle()
        shot("base-desconectado")
        compose.onNodeWithText("Tentar de novo").assertExists()
        // The chip and the banner title both say it.
        compose.onAllNodesWithText("PC desconectado").assertCountEquals(2)
    }

    @Test
    fun componentSheet() {
        compose.setContent {
            PanelHomeTheme {
                Column(
                    Modifier.size(960.dp, 600.dp).background(PanelBackground).padding(24.dp),
                    verticalArrangement = Arrangement.spacedBy(16.dp),
                ) {
                    StatusBanner("Conectando ao PC…", "Procurando o computador pelo cabo USB. Costuma levar alguns segundos.", BannerTone.Connecting,
                        secondary = BannerAction("Cancelar", {}))
                    StatusBanner("Não foi possível abrir o Desktop", "O computador ainda está preparando o vídeo. Tente de novo em alguns segundos.",
                        BannerTone.Error, primary = BannerAction("Tentar de novo", {}, com.panelos.panelhome.R.drawable.ic_refresh),
                        secondary = BannerAction("Voltar ao painel", {}))
                    var tab by remember { mutableIntStateOf(0) }
                    Row(horizontalArrangement = Arrangement.spacedBy(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        SegmentedControl(listOf("Gráficos", "Processos"), tab, { tab = it })
                        SegmentedControl(listOf("1 min", "5 min", "15 min"), 1, {})
                        PanelButton("Primário", {})
                        PanelButton("Secundário", {}, style = PanelButtonStyle.Secondary)
                        PanelButton("Encerrar", {}, style = PanelButtonStyle.Destructive)
                    }
                    Row(Modifier.weight(1f).fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                        MetricCard(card("CPU", "94,1", "%", "4,60 GHz", "Ryzen 7 5800X"), Modifier.width(216.dp).fillMaxSize(), highLoad = true)
                        MetricCard(card("GPU", null, null, null), Modifier.width(216.dp).fillMaxSize())
                        MetricCard(card("RAM", "17,3", "GB", null), Modifier.width(216.dp).fillMaxSize(), staleLabel = "há 2 min")
                    }
                }
            }
        }
        compose.waitForIdle()
        shot("base-componentes")
        compose.onNodeWithText("Sem leitura", useUnmergedTree = true).assertExists()
    }

    @Test
    fun midiaEntersCleanAndATapShowsTheChrome() {
        compose.setContent { Frame(ConnectionStatus.CONNECTED, startAt = full.mediaPage) { if (it == full.mediaPage) FakeMedia() } }
        compose.waitForIdle()
        shot("base-midia-limpo")
        // Clean on entry: no bar, no chip, no menu.
        compose.onNodeWithText("Painel").assertDoesNotExist()
        compose.onNodeWithText("PC conectado").assertDoesNotExist()

        compose.onRoot().performTouchInput { click() }
        compose.waitForIdle()
        shot("base-midia-hud")
        compose.onNodeWithText("Painel").assertExists()
        compose.onNodeWithText("PC conectado").assertExists()

        // Hides again after a few seconds without touch.
        compose.mainClock.advanceTimeBy(4_500)
        compose.onNodeWithText("Painel").assertDoesNotExist()

        // A second tap shows it, a third hides it.
        compose.onRoot().performTouchInput { click() }
        compose.waitForIdle()
        compose.onNodeWithText("Painel").assertExists()
        compose.onRoot().performTouchInput { click() }
        compose.waitForIdle()
        compose.onNodeWithText("Painel").assertDoesNotExist()
    }

    @Test
    fun swipingStillChangesPageWithTheMediaScreenClean() {
        var seen by mutableIntStateOf(-1)
        compose.setContent {
            Frame(ConnectionStatus.CONNECTED, startAt = full.mediaPage, onVisible = { seen = it }) { if (it == full.mediaPage) FakeMedia() }
        }
        compose.waitForIdle()
        assertEquals(full.mediaPage, seen)
        compose.onRoot().performTouchInput { swipeRight() }
        compose.waitForIdle()
        assertEquals(full.mediaPage!! - 1, seen)
        // Leaving Mídia brings the regular chrome back.
        compose.onNodeWithText("Painel").assertExists()
    }
}
