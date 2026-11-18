package com.panelos.panelhome.ui.home

/** The five places the bottom bar can take the user. */
internal enum class NavDestination { PANEL, SHORTCUTS, COMPUTER, ALERTS, MEDIA }

/**
 * Where each destination lives in the carousel. Pure on purpose: the order
 * (Painel, Atalhos 1..N, Computador, Avisos, Mídia) is the contract between
 * the pager, the bottom bar and the pages that jump around.
 *
 * Processes is not a page: it is a tab inside Computador, so it adds none.
 */
internal data class HomeLayout(
    val streamDeckPageCount: Int,
    val hasComputer: Boolean = false,
    val hasAlerts: Boolean = false,
    val hasMedia: Boolean = false,
) {
    private val shortcuts = streamDeckPageCount.coerceAtLeast(0)
    private val extraCount = (if (hasComputer) 1 else 0) + (if (hasAlerts) 1 else 0)

    val pageCount: Int = homePageCount(shortcuts, extraCount, hasMedia)
    val computerPage: Int? = if (hasComputer) 1 + shortcuts else null
    val alertsPage: Int? = if (hasAlerts) 1 + shortcuts + (if (hasComputer) 1 else 0) else null
    val mediaPage: Int? = loopMediaPageIndex(shortcuts, hasMedia, extraCount)

    /** Destinations that exist, in bar order. Unavailable ones are left out, not greyed. */
    val destinations: List<NavDestination> = buildList {
        add(NavDestination.PANEL)
        if (shortcuts > 0) add(NavDestination.SHORTCUTS)
        if (hasComputer) add(NavDestination.COMPUTER)
        if (hasAlerts) add(NavDestination.ALERTS)
        if (hasMedia) add(NavDestination.MEDIA)
    }

    /** The bar item that is active on [page]; every Atalhos page keeps "Atalhos" active. */
    fun destinationOf(page: Int): NavDestination = when {
        page <= 0 -> NavDestination.PANEL
        page <= shortcuts -> NavDestination.SHORTCUTS
        page == computerPage -> NavDestination.COMPUTER
        page == alertsPage -> NavDestination.ALERTS
        page == mediaPage -> NavDestination.MEDIA
        else -> NavDestination.PANEL
    }

    /** The first page of [destination], or null when it is not in the carousel. */
    fun firstPageOf(destination: NavDestination): Int? = when (destination) {
        NavDestination.PANEL -> DASHBOARD_PAGE_INDEX
        NavDestination.SHORTCUTS -> if (shortcuts > 0) 1 else null
        NavDestination.COMPUTER -> computerPage
        NavDestination.ALERTS -> alertsPage
        NavDestination.MEDIA -> mediaPage
    }

    /** Pages of the Atalhos range, for remembering the one the user was on. */
    fun isShortcutsPage(page: Int): Boolean = page in 1..shortcuts

    /** Page a bar item jumps to; for Atalhos it is the last one visited, kept inside the range. */
    fun targetPage(destination: NavDestination, lastShortcutsPage: Int): Int? =
        if (destination == NavDestination.SHORTCUTS && shortcuts > 0) lastShortcutsPage.coerceIn(1, shortcuts)
        else firstPageOf(destination)
}

/**
 * The Mídia page shows only the GIF or video. Its chrome (bottom bar, connection
 * chip, menu) is hidden on entry; a tap reveals it and a tap or a few seconds
 * without touch hides it again. Every other page always shows its chrome.
 */
internal fun chromeVisible(onMediaPage: Boolean, mediaRevealed: Boolean): Boolean =
    !onMediaPage || mediaRevealed
