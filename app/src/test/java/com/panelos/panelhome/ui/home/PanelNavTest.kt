package com.panelos.panelhome.ui.home

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The bottom bar's map between carousel pages and its five destinations. */
class PanelNavTest {
    private val full = HomeLayout(streamDeckPageCount = 3, hasComputer = true, hasAlerts = true, hasMedia = true)

    @Test
    fun orderIsPanelShortcutsComputerAlertsMedia() {
        assertEquals(
            listOf(
                NavDestination.PANEL, NavDestination.SHORTCUTS, NavDestination.COMPUTER,
                NavDestination.ALERTS, NavDestination.MEDIA,
            ),
            full.destinations,
        )
        // Painel 0, Atalhos 1..3, Computador 4, Avisos 5, Mídia 6.
        assertEquals(7, full.pageCount)
        assertEquals(4, full.computerPage)
        assertEquals(5, full.alertsPage)
        assertEquals(6, full.mediaPage)
    }

    @Test
    fun theCarouselMatchesTheLegacyPageArithmetic() {
        assertEquals(homePageCount(3, extraPageCount = 2, hasLoopMediaPage = true), full.pageCount)
        assertEquals(loopMediaPageIndex(3, true, 2), full.mediaPage)
    }

    @Test
    fun everyShortcutsPageKeepsAtalhosActive() {
        for (page in 1..3) assertEquals(NavDestination.SHORTCUTS, full.destinationOf(page))
        assertEquals(NavDestination.PANEL, full.destinationOf(0))
        assertEquals(NavDestination.COMPUTER, full.destinationOf(4))
        assertEquals(NavDestination.ALERTS, full.destinationOf(5))
        assertEquals(NavDestination.MEDIA, full.destinationOf(6))
    }

    @Test
    fun unavailableDestinationsAreLeftOutAndTheRestKeepsItsOrder() {
        val noDeck = HomeLayout(streamDeckPageCount = 0, hasComputer = true, hasAlerts = false, hasMedia = true)
        assertEquals(listOf(NavDestination.PANEL, NavDestination.COMPUTER, NavDestination.MEDIA), noDeck.destinations)
        assertEquals(1, noDeck.computerPage)
        assertEquals(2, noDeck.mediaPage)
        assertNull(noDeck.alertsPage)
        assertNull(noDeck.firstPageOf(NavDestination.SHORTCUTS))
        assertNull(noDeck.firstPageOf(NavDestination.ALERTS))
    }

    @Test
    fun aLonePanelHasASingleDestination() {
        val alone = HomeLayout(streamDeckPageCount = 0)
        assertEquals(listOf(NavDestination.PANEL), alone.destinations)
        assertEquals(1, alone.pageCount)
    }

    @Test
    fun processesIsATabSoItAddsNoPage() {
        // Computador is one page whether Gráficos, Processos or both are on.
        assertEquals(2, HomeLayout(streamDeckPageCount = 0, hasComputer = true).pageCount)
    }

    @Test
    fun atalhosJumpsBackToTheLastShortcutsPageVisited() {
        assertEquals(2, full.targetPage(NavDestination.SHORTCUTS, lastShortcutsPage = 2))
        // The Control Center deleted pages: the remembered one is clamped into range.
        assertEquals(3, full.targetPage(NavDestination.SHORTCUTS, lastShortcutsPage = 9))
        assertEquals(1, full.targetPage(NavDestination.SHORTCUTS, lastShortcutsPage = 0))
        assertEquals(4, full.targetPage(NavDestination.COMPUTER, lastShortcutsPage = 2))
        assertEquals(0, full.targetPage(NavDestination.PANEL, lastShortcutsPage = 2))
    }

    @Test
    fun onlyTheMediaPageHidesItsChromeUntilTouched() {
        assertTrue(chromeVisible(onMediaPage = false, mediaRevealed = false))
        assertTrue(chromeVisible(onMediaPage = false, mediaRevealed = true))
        assertFalse(chromeVisible(onMediaPage = true, mediaRevealed = false))
        assertTrue(chromeVisible(onMediaPage = true, mediaRevealed = true))
    }

    @Test
    fun anOutOfRangePageNeverMapsOutsideTheBar() {
        assertEquals(NavDestination.PANEL, full.destinationOf(-3))
        assertEquals(NavDestination.PANEL, full.destinationOf(99))
        assertTrue(full.isShortcutsPage(1) && full.isShortcutsPage(3))
        assertFalse(full.isShortcutsPage(0) || full.isShortcutsPage(4))
    }
}
