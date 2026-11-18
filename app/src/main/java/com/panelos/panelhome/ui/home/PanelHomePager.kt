package com.panelos.panelhome.ui.home

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.PageSize
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelSize
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Index of the dashboard inside the home carousel. Stream Deck pages follow it.
 */
const val DASHBOARD_PAGE_INDEX = 0

/**
 * Modes the carousel owns.
 *
 * Desktop is deliberately absent: it forwards horizontal drags to Windows as
 * touch, and a pager wrapped around it would swallow every one of them. Ambient
 * and Settings are full-screen modes reached from the dashboard, not pages.
 */
internal val PAGED_PANEL_MODES = setOf(PanelMode.DASHBOARD)

/** How many pages the carousel has for a given number of Stream Deck pages. */
internal fun homePageCount(
    streamDeckPageCount: Int,
    extraPageCount: Int = 0,
    hasLoopMediaPage: Boolean = false,
): Int = 1 + streamDeckPageCount.coerceAtLeast(0) + extraPageCount.coerceAtLeast(0) +
    if (hasLoopMediaPage) 1 else 0

/** Media is last; the phase1 monitoring and notices pages keep their order. */
internal fun loopMediaPageIndex(
    streamDeckPageCount: Int,
    hasLoopMediaPage: Boolean,
    extraPageCount: Int = 0,
): Int? = if (hasLoopMediaPage) homePageCount(streamDeckPageCount, extraPageCount) else null

/**
 * Keeps the current page inside the carousel.
 *
 * The Control Center can delete a page while the panel is showing it, so the
 * index is clamped rather than trusted. An invalid index must never reach the
 * pager: it would be a crash caused by an edit made on another machine.
 */
internal fun clampHomePage(currentPage: Int, pageCount: Int): Int =
    currentPage.coerceIn(0, (pageCount - 1).coerceAtLeast(0))

/** Maps a carousel index onto a Stream Deck page, or null for the dashboard. */
internal fun streamDeckPageIndex(page: Int, streamDeckPageCount: Int = Int.MAX_VALUE): Int? =
    (page - 1).takeIf { it >= 0 && it < streamDeckPageCount }

/**
 * The main mode: a carousel of pages with fixed chrome around it. Top bar
 * (connection chip and the menu) and bottom bar (Painel, Atalhos, Computador,
 * Avisos, Mídia) live outside the pager, so only the page content swipes.
 *
 * Desktop, Ambient and Settings are deliberately outside this pager. Desktop
 * forwards horizontal gestures to Windows as touch, so a carousel wrapped
 * around it would eat every drag the user meant for the desktop.
 *
 * The Mídia page is the exception to the chrome: it shows only the media. Its
 * chrome starts hidden; a tap that is not a drag shows it, and it hides again
 * on another tap or after [PanelMotion.MediaChromeHideMillis] without touch.
 * Swiping works the same with the screen clean.
 */
@Composable
internal fun PanelHomePager(
    layout: HomeLayout,
    connectionStatus: ConnectionStatus,
    onOpenSettings: () -> Unit,
    onEnterAmbient: () -> Unit,
    modifier: Modifier = Modifier,
    userScrollEnabled: Boolean = true,
    requestedPage: Int? = null,
    onPageJumpConsumed: () -> Unit = {},
    alertsUnseen: Boolean = false,
    topBarStart: @Composable (Int) -> Unit = {},
    topBarEnd: @Composable (Int) -> Unit = {},
    pageContent: @Composable (Int, Boolean) -> Unit,
) {
    val pageCount = layout.pageCount
    val pagerState = rememberPagerState(pageCount = { pageCount })
    val scope = rememberCoroutineScope()
    val reducedMotion = LocalPanelReducedMotion.current
    val currentPage = clampHomePage(pagerState.currentPage, pageCount)
    val onMediaPage = layout.mediaPage == currentPage

    var lastShortcutsPage by remember { mutableIntStateOf(1) }
    var mediaRevealed by remember { mutableStateOf(false) }
    var touchTick by remember { mutableIntStateOf(0) }
    var menuExpanded by remember { mutableStateOf(false) }
    val showChrome = chromeVisible(onMediaPage, mediaRevealed)

    LaunchedEffect(pageCount) {
        val target = clampHomePage(pagerState.currentPage, pageCount)
        if (target != pagerState.currentPage) {
            pagerState.scrollToPage(target)
        }
    }
    LaunchedEffect(requestedPage, pageCount) {
        requestedPage?.let { pagerState.scrollToPage(clampHomePage(it, pageCount)); onPageJumpConsumed() }
    }
    LaunchedEffect(currentPage) {
        if (layout.isShortcutsPage(currentPage)) lastShortcutsPage = currentPage
        // Entering (or leaving) Mídia always starts clean.
        mediaRevealed = false
        menuExpanded = false
    }
    // Hide after a few seconds without touch; an open menu counts as touch.
    LaunchedEffect(mediaRevealed, touchTick, menuExpanded) {
        if (mediaRevealed && !menuExpanded) {
            delay(PanelMotion.MediaChromeHideMillis)
            mediaRevealed = false
        }
    }

    val fadeMillis = PanelMotion.fade(reducedMotion, PanelMotion.ChromeFadeMillis)
    val chromeEnter = fadeIn(tween(fadeMillis, easing = PanelMotion.EaseOut))
    val chromeExit = fadeOut(tween(fadeMillis, easing = PanelMotion.EaseOut))

    Box(modifier = modifier.fillMaxSize()) {
        HorizontalPager(
            state = pagerState,
            userScrollEnabled = userScrollEnabled,
            modifier = Modifier.fillMaxSize(),
            // One page per swipe: this is a carousel of panels, not a scrolling surface.
            pageSize = PageSize.Fill,
        ) { page ->
            val isMedia = layout.mediaPage == page
            val pageModifier = if (isMedia) {
                // A tap reveals the chrome; a drag is left to the pager (detectTapGestures
                // gives up as soon as the finger moves past the touch slop).
                Modifier
                    .fillMaxSize()
                    .pointerInput(Unit) { detectTapGestures { mediaRevealed = !mediaRevealed } }
            } else {
                Modifier
                    .fillMaxSize()
                    .padding(top = PanelSize.topBar, bottom = PanelSize.navBar)
            }
            Box(pageModifier) {
                pageContent(clampHomePage(page, pageCount), page == pagerState.currentPage)
            }
        }

        AnimatedVisibility(
            visible = showChrome,
            modifier = Modifier.align(Alignment.TopCenter),
            enter = chromeEnter,
            exit = chromeExit,
        ) {
            Box(Modifier.touchBump { touchTick++ }.hideOnBackgroundTap { mediaRevealed = false }) {
                PanelTopBar(
                    status = connectionStatus,
                    onChipClick = onOpenSettings,
                    menuExpanded = menuExpanded,
                    onMenuExpandedChange = { menuExpanded = it },
                    onEnterAmbient = onEnterAmbient,
                    onOpenSettings = onOpenSettings,
                    overMedia = onMediaPage,
                    start = { if (!onMediaPage) topBarStart(currentPage) },
                    end = { if (!onMediaPage) topBarEnd(currentPage) },
                )
            }
        }
        AnimatedVisibility(
            visible = showChrome,
            modifier = Modifier.align(Alignment.BottomCenter),
            enter = chromeEnter,
            exit = chromeExit,
        ) {
            Box(Modifier.touchBump { touchTick++ }.hideOnBackgroundTap { mediaRevealed = false }) {
                PanelNavBar(
                    destinations = layout.destinations,
                    selected = layout.destinationOf(currentPage),
                    alertsUnseen = alertsUnseen,
                    overMedia = onMediaPage,
                    onSelect = { destination ->
                        layout.targetPage(destination, lastShortcutsPage)?.let { page ->
                            if (page != pagerState.currentPage) scope.launch { pagerState.scrollToPage(page) }
                        }
                    },
                )
            }
        }
    }
}

/** Reports every touch on the chrome without consuming it, so the media timer can restart. */
private fun Modifier.touchBump(onTouch: () -> Unit): Modifier = pointerInput(Unit) {
    awaitPointerEventScope {
        while (true) {
            val event = awaitPointerEvent(PointerEventPass.Initial)
            if (event.changes.any { it.pressed }) onTouch()
        }
    }
}

/** A tap on the chrome that no control took (its empty area) hides it again. */
private fun Modifier.hideOnBackgroundTap(onTap: () -> Unit): Modifier =
    pointerInput(Unit) { detectTapGestures { onTap() } }
