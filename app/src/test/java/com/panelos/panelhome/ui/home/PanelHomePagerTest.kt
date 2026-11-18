package com.panelos.panelhome.ui.home

import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.presentation.StreamDeckUiState
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * The carousel's page arithmetic. It is deliberately pure: a page index that
 * came from a configuration edited on another machine must be clamped, not
 * trusted, and that has to be provable without a device.
 */
class PanelHomePagerTest {
    @Test
    fun dashboardIsAlwaysTheFirstPage() {
        assertEquals(0, DASHBOARD_PAGE_INDEX)
        assertNull(streamDeckPageIndex(DASHBOARD_PAGE_INDEX))
    }

    @Test
    fun withoutStreamDeckPagesTheCarouselIsOnlyTheDashboard() {
        assertEquals(1, homePageCount(streamDeckPageCount = 0))
    }

    @Test
    fun oneStreamDeckPageAddsOnePageAfterTheDashboard() {
        assertEquals(2, homePageCount(streamDeckPageCount = 1))
        assertEquals(0, streamDeckPageIndex(1))
    }

    @Test
    fun multiplePagesKeepTheirOrderAfterTheDashboard() {
        assertEquals(4, homePageCount(streamDeckPageCount = 3))
        assertEquals(0, streamDeckPageIndex(1))
        assertEquals(1, streamDeckPageIndex(2))
        assertEquals(2, streamDeckPageIndex(3))
    }

    @Test
    fun monitoringPagesFollowAllShortcutsAndAreNeverMappedToADeckIndex() {
        assertEquals(6, homePageCount(streamDeckPageCount = 3, extraPageCount = 2))
        assertEquals(2, streamDeckPageIndex(3, streamDeckPageCount = 3))
        assertNull(streamDeckPageIndex(4, streamDeckPageCount = 3))
        assertNull(streamDeckPageIndex(5, streamDeckPageCount = 3))
        assertEquals(1, homePageCount(streamDeckPageCount = 0, extraPageCount = 0))
    }

    @Test
    fun removingThePageBeingShownClampsToAValidPage() {
        // Three Stream Deck pages, the last one visible, then the Control
        // Center deletes two of them.
        assertEquals(1, clampHomePage(currentPage = 3, pageCount = 2))
        assertEquals(0, clampHomePage(currentPage = 3, pageCount = 1))
    }

    @Test
    fun anImpossiblePageIndexNeverEscapesTheRange() {
        assertEquals(0, clampHomePage(currentPage = -5, pageCount = 1))
        assertEquals(0, clampHomePage(currentPage = 7, pageCount = 0))
        assertEquals(2, clampHomePage(currentPage = 2, pageCount = 3))
    }

    @Test
    fun withoutLoopMediaTheCarouselIsUnchanged() {
        assertEquals(homePageCount(2), homePageCount(2, hasLoopMediaPage = false))
        assertNull(loopMediaPageIndex(streamDeckPageCount = 2, hasLoopMediaPage = false))
    }

    @Test
    fun theMediaPageComesAfterTheLastStreamDeckPage() {
        // Painel, two Stream Deck pages, Mídia.
        assertEquals(4, homePageCount(streamDeckPageCount = 2, hasLoopMediaPage = true))
        assertEquals(3, loopMediaPageIndex(streamDeckPageCount = 2, hasLoopMediaPage = true))
        assertEquals(1, streamDeckPageIndex(2))
    }

    @Test
    fun theMediaPageFollowsTheDashboardWhenThereIsNoStreamDeck() {
        assertEquals(2, homePageCount(streamDeckPageCount = 0, hasLoopMediaPage = true))
        assertEquals(1, loopMediaPageIndex(streamDeckPageCount = 0, hasLoopMediaPage = true))
    }

    @Test
    fun aNegotiatedStreamDeckWithoutProfileGivesPainelThenMidia() {
        // The real state PanelApp reads: feature negotiated, no layout yet.
        val deck = StreamDeckUiState().forNewConnection(available = true)
        val count = deck.carouselPageCount(hasLoopMediaPage = true)

        assertEquals(2, homePageCount(count, hasLoopMediaPage = true))
        assertEquals(1, loopMediaPageIndex(count, hasLoopMediaPage = true))
    }

    @Test
    fun withoutMediaTheEmptyStreamDeckPlaceholderStays() {
        val deck = StreamDeckUiState().forNewConnection(available = true)

        assertEquals(deck.carouselPageCount, deck.carouselPageCount(hasLoopMediaPage = false))
        assertEquals(2, homePageCount(deck.carouselPageCount(hasLoopMediaPage = false)))
    }

    @Test
    fun removingTheMediaPageWhileShownClampsToTheLastStreamDeckPage() {
        val withMedia = homePageCount(2, hasLoopMediaPage = true)
        val withoutMedia = homePageCount(2, hasLoopMediaPage = false)

        assertEquals(2, clampHomePage(currentPage = withMedia - 1, pageCount = withoutMedia))
    }

    @Test
    fun onlyTheMainModeIsPaged() {
        // Desktop forwards horizontal drags to Windows as touch, so it must
        // never sit inside a carousel that would consume them; Ambient and
        // Settings are full-screen modes of their own.
        assertEquals(setOf(PanelMode.DASHBOARD), PAGED_PANEL_MODES)
        assertEquals(
            setOf(PanelMode.DESKTOP, PanelMode.AMBIENT, PanelMode.SETTINGS),
            PanelMode.entries.toSet() - PAGED_PANEL_MODES,
        )
    }

    @Test
    fun phase1PagesKeepTheirIndicesAndMediaIsLast() {
        // Painel, two shortcuts, Computador, Processos, Avisos, Mídia.
        assertEquals(6, homePageCount(2, extraPageCount = 3))
        assertEquals(7, homePageCount(2, extraPageCount = 3, hasLoopMediaPage = true))
        assertEquals(6, loopMediaPageIndex(2, hasLoopMediaPage = true, extraPageCount = 3))
        for (page in 3..6) assertNull(streamDeckPageIndex(page, streamDeckPageCount = 2))
        assertEquals(5, clampHomePage(6, homePageCount(2, extraPageCount = 3)))
    }

    @Test
    fun mediaOnlyDropsTheEmptyDeckPlaceholderAndNeverThePhase1Pages() {
        val deck = StreamDeckUiState().forNewConnection(available = true)
        val count = deck.carouselPageCount(hasLoopMediaPage = true)
        assertEquals(0, count)
        assertEquals(5, homePageCount(count, extraPageCount = 3, hasLoopMediaPage = true))
        assertEquals(4, loopMediaPageIndex(count, hasLoopMediaPage = true, extraPageCount = 3))
        assertEquals(1, deck.carouselPageCount(hasLoopMediaPage = false))
    }
}
