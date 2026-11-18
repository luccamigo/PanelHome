package com.panelos.panelhome

import android.content.Intent
import android.graphics.Bitmap
import android.os.Build
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.unit.Density
import androidx.test.core.app.ActivityScenario
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.protocol.*
import com.panelos.panelhome.presentation.*
import com.panelos.panelhome.session.PanelSessionService
import com.panelos.panelhome.session.PanelSessionServiceTestDependencies
import com.panelos.panelhome.ui.integrations.NoticeBanner
import com.panelos.panelhome.ui.integrations.NoticesScreen
import com.panelos.panelhome.ui.integrations.PermissionOverlay
import com.panelos.panelhome.ui.monitoring.ProcessesScreen
import com.panelos.panelhome.ui.theme.PanelHomeTheme
import java.io.File
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith

/** Real Compose views, fake in-memory state: no host, network, hook or process action. */
@RunWith(AndroidJUnit4::class)
class Phase1ScreenshotsTest {
    @get:Rule val compose = createEmptyComposeRule()

    private fun prepareEmulator() {
        assumeTrue("Screenshot fixtures run only on an emulator", Build.HARDWARE in setOf("ranchu", "goldfish"))
        if (Build.VERSION.SDK_INT >= 33) {
            val instrumentation = InstrumentationRegistry.getInstrumentation()
            instrumentation.uiAutomation.executeShellCommand(
                "pm grant ${instrumentation.targetContext.packageName} android.permission.POST_NOTIFICATIONS"
            ).close()
        }
    }

    @Test fun unspacedSummaryRemainsFullyLaidOutAtLargeFont() {
        prepareEmulator()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        context.stopService(Intent(context, PanelSessionService::class.java))
        PanelSessionServiceTestDependencies.sessionFactory = { InstrumentedFakePanelSession() }
        val command = "X".repeat(351) + "FIM123456"
        assertTrue(command.length == 360)
        val request = ClaudePermissionRequest("0123456789abcdef0123456789abcdef", "Projeto fictício", "Bash",
            command, "", 0, 1_800_000_060_000L)
        val state = LocalIntegrationsState(permissionsAvailable = true, connected = true, permissionCount = 1, now = 10_000,
            permission = PermissionUi(request, 70_000, shownAt = 9_000, stage = PermissionStage.ACTIVE))
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenario.onActivity { activity -> activity.setContent {
                    CompositionLocalProvider(LocalDensity provides Density(LocalDensity.current.density, fontScale = 1.3f)) {
                        PanelHomeTheme { Surface(Modifier.fillMaxSize()) { PermissionOverlay(state, {}, { _, _ -> }) } }
                    }
                } }
                val layouts = mutableListOf<TextLayoutResult>()
                compose.onNodeWithText(command, useUnmergedTree = true)
                    .performSemanticsAction(SemanticsActions.GetTextLayoutResult) { it(layouts) }
                val layout = layouts.single()
                assertFalse("All 360 characters must be laid out without ellipsis: lines=${layout.lineCount}, size=${layout.size}, fontScale=1.3", layout.hasVisualOverflow)
                assertTrue("After removing the cap, this fixture must use more than six lines", layout.lineCount > 6)
                fun assertDecisionsVisible() {
                    compose.onNodeWithText("Permitir", useUnmergedTree = true).assertIsDisplayed().assertIsEnabled()
                    compose.onNodeWithText("Negar", useUnmergedTree = true).assertIsDisplayed().assertIsEnabled()
                    compose.onNodeWithText("Deixar no PC", useUnmergedTree = true).assertIsDisplayed().assertIsEnabled()
                }
                assertDecisionsVisible()
                val output = File(context.getExternalFilesDir(null), "phase1-screenshots").apply { mkdirs() }
                fun capture(name: String) {
                    compose.waitForIdle()
                    val bitmap = instrumentation.uiAutomation.takeScreenshot()
                    assertNotNull(bitmap)
                    File(output, "$name.png").outputStream().use { assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) }
                    bitmap.recycle()
                }
                capture("pan6-360-fonte13-inicio")
                compose.onNode(hasScrollAction()).performSemanticsAction(SemanticsActions.ScrollBy) { it(0f, 10_000f) }
                assertDecisionsVisible()
                capture("pan6-360-fonte13-fim")
            }
        } finally {
            context.stopService(Intent(context, PanelSessionService::class.java))
            PanelSessionServiceTestDependencies.reset()
        }
    }

    @Test fun capturePhase1ScreensWithFictionalData() {
        prepareEmulator()
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        context.stopService(Intent(context, PanelSessionService::class.java))
        PanelSessionServiceTestDependencies.sessionFactory = { InstrumentedFakePanelSession() }
        val output = File(context.getExternalFilesDir(null), "phase1-screenshots").apply { mkdirs() }
        fun capture(name: String) {
            compose.waitForIdle()
            instrumentation.waitForIdleSync()
            val bitmap = instrumentation.uiAutomation.takeScreenshot()
            assertNotNull("Screenshot $name", bitmap)
            assertTrue(bitmap.width > 0 && bitmap.height > 0)
            File(output, "$name.png").outputStream().use { assertTrue(bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) }
            bitmap.recycle()
        }
        fun expandProcess(name: String) {
            compose.onNodeWithText(name, useUnmergedTree = true).performTouchInput { click() }
            compose.waitForIdle()
        }
        val now = 10_000L
        val notice = NoticeUi(LocalNotice("demo-notice", "Build de demonstração", "Validação concluída. Dados fictícios.",
            "success", null, "processes", 10, 1_800_000_000_000L, 1_800_000_300_000L), now - 2_000, now + 300_000)
        val values = listOf(PublishedValueUi(LocalPublishedValue("Build", "OK", null, "success", 1_800_000_000_000L, null), now - 2_000, null),
            PublishedValueUi(LocalPublishedValue("Fila", "3", "tarefas fictícias", "info", 1_800_000_000_000L, null), now - 3_000, null))
        val state = LocalIntegrationsState(noticesAvailable = true, permissionsAvailable = true, enabled = true,
            connected = true, values = values, history = listOf(notice), now = now)
        val pages = mapOf("processes" to "Processos", "notices" to "Avisos")
        val processes = MonitoringState(processesAvailable = true, receivedProcesses = true, connected = true,
            terminateEnabled = false, now = now, processRows = listOf(
                ProcessRow("demo-openbot", ProcessItem("OpenBot.exe", 70001, 638000000000000001, 1.5, 200_000_000, "current_user", true)),
                ProcessRow("demo-cua", ProcessItem("cua-driver.exe", 70002, 638000000000000002, 0.5, 60_000_000, "current_user", true)),
                ProcessRow("demo-editor", ProcessItem("Editor fictício", 70003, 638000000000000003, 2.0, 150_000_000, "current_user", false))))
        val request = ClaudePermissionRequest("0123456789abcdef0123456789abcdef", "Projeto fictício", "Edit",
            "C:/demo/exemplo.txt", "", 40, 1_800_000_060_000L, contentHidden = true)
        fun permission(request: ClaudePermissionRequest) = state.copy(permissionCount = 1,
            permission = PermissionUi(request, now + 60_000, shownAt = now - 1_000, stage = PermissionStage.ACTIVE))
        val scenes: List<Pair<String, @Composable () -> Unit>> = listOf(
            "pan9-avisos-valores" to { NoticesScreen(state, ConnectionStatus.CONNECTED, pages, {}, {}, {}, {}) },
            "pan9-desligado" to { NoticesScreen(state.copy(enabled = false, values = emptyList(), history = emptyList()), ConnectionStatus.CONNECTED, pages, {}, {}, {}, {}) },
            "pan9-banner" to { Box(Modifier.fillMaxSize()) {
                NoticesScreen(state, ConnectionStatus.CONNECTED, pages, {}, {}, {}, {})
                NoticeBanner(state.copy(banner = notice), false, pages, {}, {}, {})
            } },
            "pan6-resumo-incompleto" to { PermissionOverlay(permission(request), {}, { _, _ -> }) },
            "pan6-completo" to { PermissionOverlay(permission(request.copy(tool = "Bash", head = "echo demonstracao", omitted = 0, contentHidden = false)), {}, { _, _ -> }) },
            "pan14-processos" to { ProcessesScreen(processes, MockPanelData.initial, {}, {}, connectionStatus = ConnectionStatus.CONNECTED) },
        )
        try {
            ActivityScenario.launch(MainActivity::class.java).use { scenario ->
                scenes.forEach { (name, content) ->
                    scenario.onActivity { activity -> activity.setContent { PanelHomeTheme { Surface(Modifier.fillMaxSize()) { content() } } } }
                    compose.waitForIdle()
                    when {
                        name.startsWith("pan9") -> compose.onNodeWithText("VALORES").assertIsDisplayed()
                        name.startsWith("pan6") -> compose.onNodeWithText("Deixar no PC", useUnmergedTree = true).assertIsDisplayed()
                        name == "pan14-processos" -> compose.onNodeWithText("Editor fictício", useUnmergedTree = true).assertIsDisplayed()
                    }
                    capture(name)
                    if (name == "pan14-processos") {
                        expandProcess("Editor fictício")
                        compose.onNodeWithText("Encerrar pelo tablet está desligado no Control Center.").assertIsDisplayed()
                        capture("pan14-encerramento-desligado")
                        expandProcess("OpenBot.exe")
                        compose.onNodeWithText("Processo protegido. O PC não permite encerrar daqui.").assertIsDisplayed()
                        capture("pan14-protegido")
                    }
                }
            }
        } finally {
            context.stopService(Intent(context, PanelSessionService::class.java))
            PanelSessionServiceTestDependencies.reset()
        }
    }
}
