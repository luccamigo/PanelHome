package com.panelos.panelhome.domain.model

/**
 * The Stream Deck as the tablet knows it.
 *
 * These models describe what to draw and which id to send back when a button is
 * touched. There is deliberately nothing here that describes *what happens* on
 * the PC: no executable, arguments, working directory, URL or script. The panel
 * asks for a button by id and PanelHost decides what that means, so a
 * compromised or confused tablet cannot express an action the user never
 * configured. Keeping this separate from the Windows `PanelAction` domain is
 * what makes that boundary visible.
 */
data class StreamDeckLayout(
    val revision: String,
    val profile: StreamDeckProfile? = null,
    val pages: List<StreamDeckPage> = emptyList(),
) {
    val hasPages: Boolean get() = pages.isNotEmpty()

    /** Icon ids every page refers to, without duplicates. */
    fun iconIds(): Set<String> = pages
        .asSequence()
        .flatMap { page -> page.buttons.asSequence() }
        .mapNotNull { button -> button.iconId }
        .toSet()

    fun findButton(buttonId: String): StreamDeckButton? = pages
        .asSequence()
        .flatMap { page -> page.buttons.asSequence() }
        .firstOrNull { button -> button.id == buttonId }

    companion object {
        /** Nothing configured yet; the panel shows its empty page. */
        val EMPTY = StreamDeckLayout(revision = "")
    }
}

data class StreamDeckProfile(
    val id: String,
    val name: String,
)

data class StreamDeckPage(
    val id: String,
    val name: String,
    val rows: Int,
    val columns: Int,
    val buttons: List<StreamDeckButton> = emptyList(),
) {
    fun buttonAt(row: Int, column: Int): StreamDeckButton? =
        buttons.firstOrNull { button -> button.row == row && button.column == column }
}

data class StreamDeckButton(
    val id: String,
    val row: Int,
    val column: Int,
    val label: String,

    /** Key into the PC icon cache, mirrored into this app's own cache. */
    val iconId: String? = null,

    /** Name of a shape the panel draws itself when there is no image. */
    val glyph: String? = null,

    /** Whether the PC can run it now. A disabled button never sends anything. */
    val enabled: Boolean = true,
)
