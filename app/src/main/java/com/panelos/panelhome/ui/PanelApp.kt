package com.panelos.panelhome.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.animation.core.tween
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.PointerEventPass
import com.panelos.panelhome.ui.integrations.NoticesScreen
import com.panelos.panelhome.ui.integrations.NoticeBanner
import com.panelos.panelhome.ui.integrations.PermissionOverlay
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.power.PowerOffDelay
import com.panelos.panelhome.power.PowerSettings
import com.panelos.panelhome.presentation.PanelViewModel
import com.panelos.panelhome.ui.ambient.AmbientScreen
import com.panelos.panelhome.ui.ambient.rememberAmbientDateTime
import com.panelos.panelhome.ui.components.rememberCurrentTimeMillis
import com.panelos.panelhome.ui.components.rememberPanelDateTime
import com.panelos.panelhome.ui.dashboard.DashboardScreen
import com.panelos.panelhome.ui.desktop.DesktopScreen
import com.panelos.panelhome.ui.components.SegmentedControl
import com.panelos.panelhome.ui.home.ClockBlock
import com.panelos.panelhome.ui.home.HomeLayout
import com.panelos.panelhome.ui.home.PageChips
import com.panelos.panelhome.ui.home.PageTitle
import com.panelos.panelhome.ui.home.PanelHomePager
import com.panelos.panelhome.ui.loopmedia.LoopMediaScreen
import com.panelos.panelhome.ui.home.streamDeckPageIndex
import com.panelos.panelhome.ui.settings.SettingsScreen
import com.panelos.panelhome.ui.settings.WifiSettingsModel
import com.panelos.panelhome.ui.streamdeck.StreamDeckEmptyScreen
import com.panelos.panelhome.ui.streamdeck.StreamDeckPageScreen
import com.panelos.panelhome.ui.monitoring.ComputerResource
import com.panelos.panelhome.ui.monitoring.ComputerScreen
import com.panelos.panelhome.ui.monitoring.ComputerDetailScreen
import com.panelos.panelhome.ui.monitoring.ProcessesScreen
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion

@Composable
internal fun PanelApp(
    viewModel: PanelViewModel,
    powerSettings: PowerSettings,
    deviceAdminActive: Boolean,
    onWakeOnPowerChanged: (Boolean) -> Unit,
    onKeepScreenOnWhilePoweredChanged: (Boolean) -> Unit,
    onSleepAfterPowerLossChanged: (PowerOffDelay) -> Unit,
    onRequestDeviceAdmin: () -> Unit,
    onRetryConnection: () -> Unit,
    modifier: Modifier = Modifier,
    wifi: WifiSettingsModel? = null,
) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()
    val currentTimeMillis = rememberCurrentTimeMillis()
    val dateTime = rememberPanelDateTime(currentTimeMillis)

    val streamDeck = uiState.streamDeck
    val loopMedia = uiState.loopMedia
    val streamDeckPageCount = streamDeck.carouselPageCount(loopMedia.hasPage)
    val monitoring = uiState.monitoring
    val integrations = uiState.integrations
    // Processes is a tab inside Computador, not a page of its own.
    val chartsOn = monitoring.computerAvailable && monitoring.preferences.computerPage
    val processesOn = monitoring.processesAvailable && monitoring.preferences.processesPage
    val layout = HomeLayout(
        streamDeckPageCount = streamDeckPageCount,
        hasComputer = chartsOn || processesOn,
        hasAlerts = integrations.noticesAvailable && monitoring.preferences.noticesPage,
        hasMedia = loopMedia.hasPage,
    )
    var requestedPage by remember { mutableStateOf<Int?>(null) }
    var computerTab by remember { mutableStateOf(ComputerTab.CHARTS) }
    val pageRoutes = buildMap {
        streamDeck.pages.forEachIndexed { index, page -> put(page.id, index + 1) }
        layout.computerPage?.let { put("computer", it); put("processes", it) }
        layout.alertsPage?.let { put("notices", it) }
    }
    val pageNames = pageRoutes.mapValues { (key, _) -> when (key) {
        "computer" -> "Computador"; "processes" -> "Processos"; "notices" -> "Avisos"
        else -> streamDeck.pages.find { it.id == key }?.name ?: key
    } }
    val navigate: (String) -> Unit = { key ->
        if (key == "processes") computerTab = ComputerTab.PROCESSES else if (key == "computer") computerTab = ComputerTab.CHARTS
        pageRoutes[key]?.let { requestedPage = it; viewModel.returnToDashboard(); viewModel.dismissNoticeBanner() }
    }
    var detail by remember { mutableStateOf(ComputerResource.CPU) }
    var detailOpen by remember { mutableStateOf(false) }
    val reducedMotion = LocalPanelReducedMotion.current
    LaunchedEffect(uiState.mode, monitoring.computerAvailable, monitoring.preferences.computerPage) {
        if (uiState.mode != PanelMode.DASHBOARD || !chartsOn) detailOpen = false
    }

    BackHandler(enabled = uiState.mode != PanelMode.DASHBOARD) {
        viewModel.returnToDashboard()
    }

    Surface(
        modifier = modifier.fillMaxSize(),
        color = if (uiState.mode == PanelMode.AMBIENT) {
            Color.Black
        } else {
            MaterialTheme.colorScheme.background
        },
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .pointerInput(viewModel) {
                    awaitPointerEventScope {
                        var pressed = false
                        while (true) {
                            val active = awaitPointerEvent(PointerEventPass.Initial).changes.any { it.pressed }
                            if (active != pressed) { pressed = active; viewModel.setUiTouchActive(active) }
                        }
                    }
                }
                .windowInsetsPadding(WindowInsets.safeDrawing),
        ) {
            Box(Modifier.fillMaxSize().then(if (integrations.permission != null) Modifier.clearAndSetSemantics {} else Modifier)) {
            when (uiState.mode) {
                // Only the main mode is a carousel: the dashboard, then one
                // page per configured Stream Deck page.
                PanelMode.DASHBOARD -> PanelHomePager(
                    layout = layout,
                    connectionStatus = uiState.connectionStatus,
                    onOpenSettings = viewModel::openSettings,
                    onEnterAmbient = viewModel::enterAmbient,
                    alertsUnseen = integrations.unseen,
                    userScrollEnabled = !detailOpen && integrations.permission == null,
                    modifier = if (detailOpen || integrations.permission != null) Modifier.clearAndSetSemantics {} else Modifier,
                    requestedPage = requestedPage,
                    onPageJumpConsumed = { requestedPage = null },
                    topBarStart = { page ->
                        when {
                            page == 0 -> ClockBlock(dateTime)
                            page == layout.computerPage -> PageTitle(stringResource(R.string.nav_computer)) {
                                if (chartsOn && processesOn) {
                                    SegmentedControl(
                                        options = listOf(stringResource(R.string.computer_tab_charts), stringResource(R.string.computer_tab_processes)),
                                        selectedIndex = computerTab.ordinal,
                                        onSelect = { computerTab = ComputerTab.entries[it] },
                                    )
                                }
                            }
                            page == layout.alertsPage -> PageTitle(stringResource(R.string.nav_alerts))
                            layout.isShortcutsPage(page) -> PageTitle(stringResource(R.string.streamdeck_title)) {
                                if (streamDeck.pages.size > 1) {
                                    PageChips(
                                        names = streamDeck.pages.map { it.name },
                                        selectedIndex = page - 1,
                                        onSelect = { requestedPage = it + 1 },
                                    )
                                }
                            }
                        }
                    },
                    topBarEnd = { page ->
                        val showingCharts = if (chartsOn && processesOn) computerTab == ComputerTab.CHARTS else chartsOn
                        if (page == layout.computerPage && showingCharts) {
                            val windows = listOf(1, 5, 15)
                            SegmentedControl(
                                options = windows.map { stringResource(R.string.window_minutes, it) },
                                selectedIndex = windows.indexOf(monitoring.preferences.windowMinutes).coerceAtLeast(0),
                                onSelect = { viewModel.updateMonitoringPreferences(monitoring.preferences.copy(windowMinutes = windows[it])) },
                            )
                        }
                    },
                ) { page, visible ->
                    val streamDeckIndex = streamDeckPageIndex(page, streamDeckPageCount)
                    val streamDeckPage = streamDeckIndex
                        ?.let { index -> streamDeck.pages.getOrNull(index) }
                    if (page == layout.mediaPage) {
                        LoopMediaScreen(loopMedia, isCurrentPage = visible && !detailOpen && integrations.permission == null,
                            onReturnToPanel = { requestedPage = 0 })
                    } else if (page == layout.computerPage) {
                        // Gráficos and Processos share one page; the switch lives in the top bar.
                        val tab = if (chartsOn && processesOn) computerTab else if (processesOn) ComputerTab.PROCESSES else ComputerTab.CHARTS
                        if (tab == ComputerTab.CHARTS) {
                            if (visible && !detailOpen) ComputerScreen(
                                state = monitoring, data = uiState.panelData,
                                connectionStatus = uiState.connectionStatus,
                                onRetryConnection = onRetryConnection,
                                onOpenSettings = viewModel::openSettings,
                                onOpenResource = { detail = it; detailOpen = true },
                                fps = uiState.fps,
                            )
                        } else if (visible) ProcessesScreen(
                            state = monitoring, data = uiState.panelData,
                            onPreferences = viewModel::updateMonitoringPreferences,
                            onInteracting = viewModel::setProcessListInteracting,
                            onTerminate = viewModel::requestTerminateProcess,
                            onDismissTermination = viewModel::dismissTermination,
                            connectionStatus = uiState.connectionStatus,
                            onRetryConnection = onRetryConnection,
                            onOpenSettings = viewModel::openSettings,
                        )
                    } else if (page == layout.alertsPage) {
                        if (visible) NoticesScreen(integrations, uiState.connectionStatus, pageNames,
                            viewModel::markNoticesSeen, navigate, onRetryConnection, viewModel::openSettings)
                    } else if (streamDeckIndex != null) {
                        if (streamDeckPage == null) {
                            StreamDeckEmptyScreen()
                        } else {
                            StreamDeckPageScreen(
                                page = streamDeckPage,
                                streamDeck = streamDeck,
                                icons = viewModel.streamDeckIcons,
                                onButtonPressed = viewModel::invokeStreamDeckButton,
                            )
                        }
                    } else {
                        DashboardScreen(
                            data = uiState.panelData,
                            monitoring = monitoring,
                            fps = uiState.fps,
                            connectionStatus = uiState.connectionStatus,
                            onOpenDesktop = viewModel::openDesktop,
                            onOpenSettings = viewModel::openSettings,
                            onRetryConnection = onRetryConnection,
                            onPreviousTrack = viewModel::previousTrack,
                            onPlayPause = viewModel::togglePlayback,
                            onNextTrack = viewModel::nextTrack,
                            onVolumeChange = viewModel::setVolume,
                            onVolumeChangeFinished = viewModel::finishVolumeChange,
                            onToggleVolumeMute = viewModel::toggleVolumeMute,
                        )
                    }
                }

                PanelMode.DESKTOP -> DesktopScreen(
                    status = uiState.desktopStatus,
                    errorCode = uiState.desktopErrorCode,
                    videoState = uiState.desktopVideo,
                    videoFrameStore = viewModel.videoFrameStore,
                    h264Decoder = viewModel.h264Decoder,
                    touchFeatureNegotiated = uiState.desktopTouchAvailable,
                    touchGeneration = uiState.desktopTouchGeneration,
                    onBackToDashboard = viewModel::returnToDashboard,
                    onRetry = viewModel::retryDesktop,
                    onVideoFrameDrawn = viewModel::onVideoFrameDrawn,
                    onVideoRenderError = viewModel::onVideoRenderError,
                    onDesktopTouch = viewModel::onDesktopTouch,
                )

                PanelMode.AMBIENT -> AmbientScreen(
                    data = uiState.panelData,
                    dateTime = rememberAmbientDateTime(currentTimeMillis),
                    connectionStatus = uiState.connectionStatus,
                    onBackToDashboard = viewModel::returnToDashboard,
                )

                PanelMode.SETTINGS -> SettingsScreen(
                    connectionStatus = uiState.connectionStatus,
                    hostName = uiState.hostName,
                    powerSettings = powerSettings,
                    deviceAdminActive = deviceAdminActive,
                    onWakeOnPowerChanged = onWakeOnPowerChanged,
                    onKeepScreenOnWhilePoweredChanged =
                        onKeepScreenOnWhilePoweredChanged,
                    onSleepAfterPowerLossChanged = onSleepAfterPowerLossChanged,
                    onRequestDeviceAdmin = onRequestDeviceAdmin,
                    onBack = viewModel::returnToDashboard,
                    wifi = wifi,
                    monitoring = monitoring,
                    onMonitoringPreferences = viewModel::updateMonitoringPreferences,
                )
            }
            AnimatedVisibility(detailOpen && uiState.mode == PanelMode.DASHBOARD,
                enter = fadeIn(tween(if (reducedMotion) 100 else 220)) +
                    slideInVertically(tween(if (reducedMotion) 0 else 220)) { if (reducedMotion) 0 else 24 },
                exit = fadeOut(tween(if (reducedMotion) 100 else 160)) +
                    slideOutVertically(tween(if (reducedMotion) 0 else 160)) { if (reducedMotion) 0 else 24 }) {
                ComputerDetailScreen(detail, monitoring, uiState.panelData,
                    viewModel::updateMonitoringPreferences, onBack = { detailOpen = false })
            }
            }
            val banner = integrations.banner
            val desktop = uiState.mode == PanelMode.DESKTOP
            if (banner != null && integrations.permission == null && (!desktop || monitoring.preferences.noticesOnDesktop)) {
                NoticeBanner(integrations,
                    compact = desktop && banner.notice.level in setOf("info", "success"), pages = pageNames,
                    onDismiss = viewModel::dismissNoticeBanner,
                    onOpenNotices = { navigate("notices") }, onNavigate = navigate)
            }
            AnimatedVisibility(integrations.permission != null && integrations.permissionsAvailable,
                enter = fadeIn(tween(if (reducedMotion) 100 else 200)) + scaleIn(tween(if (reducedMotion) 0 else 200), initialScale = if (reducedMotion) 1f else 0.96f),
                exit = fadeOut(tween(if (reducedMotion) 100 else 150)) + scaleOut(tween(if (reducedMotion) 0 else 150), targetScale = if (reducedMotion) 1f else 0.96f)) {
                PermissionOverlay(integrations, viewModel::permissionShown, viewModel::respondToPermission)
            }
        }
    }
}

/** The two views of the Computador page. */
private enum class ComputerTab { CHARTS, PROCESSES }
