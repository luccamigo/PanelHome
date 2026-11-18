package com.panelos.panelhome.data.streamdeck

import com.panelos.panelhome.domain.model.StreamDeckButton
import com.panelos.panelhome.domain.model.StreamDeckLayout
import com.panelos.panelhome.domain.model.StreamDeckPage
import com.panelos.panelhome.domain.model.StreamDeckProfile
import com.panelos.panelhome.domain.protocol.StreamDeckButtonMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.StreamDeckPageMessage

/**
 * Turns a received layout into something the grid can draw without checking
 * anything again.
 *
 * PanelHost already validates the configuration it publishes, but the panel
 * still refuses what it cannot render: a grid outside the supported range, a
 * button outside its own grid, two buttons in one slot or a repeated id. The
 * bad entry is dropped and the rest of the page is kept, so one broken button
 * never costs the user the whole panel.
 */
internal object StreamDeckLayoutMapper {
    /** Same range the Control Center validates on the PC side. */
    const val MIN_GRID = 2
    const val MAX_GRID = 6

    fun toLayout(message: StreamDeckLayoutMessage): StreamDeckLayout {
        val seenButtonIds = mutableSetOf<String>()
        val pages = message.pages
            .mapNotNull { page -> toPage(page, seenButtonIds) }
        return StreamDeckLayout(
            revision = message.revision,
            profile = message.profileId
                ?.takeIf(String::isNotBlank)
                ?.let { id ->
                    StreamDeckProfile(
                        id = id,
                        name = message.profileName.orEmpty(),
                    )
                },
            pages = pages,
        )
    }

    private fun toPage(
        message: StreamDeckPageMessage,
        seenButtonIds: MutableSet<String>,
    ): StreamDeckPage? {
        if (message.id.isBlank()) return null
        if (message.rows !in MIN_GRID..MAX_GRID) return null
        if (message.columns !in MIN_GRID..MAX_GRID) return null

        val occupied = mutableSetOf<Int>()
        val buttons = message.buttons.mapNotNull { button ->
            toButton(button, message.rows, message.columns, occupied, seenButtonIds)
        }
        return StreamDeckPage(
            id = message.id,
            name = message.name,
            rows = message.rows,
            columns = message.columns,
            buttons = buttons,
        )
    }

    private fun toButton(
        message: StreamDeckButtonMessage,
        rows: Int,
        columns: Int,
        occupied: MutableSet<Int>,
        seenButtonIds: MutableSet<String>,
    ): StreamDeckButton? {
        if (message.id.isBlank()) return null
        if (message.row !in 0 until rows || message.column !in 0 until columns) {
            return null
        }
        if (!occupied.add(message.row * columns + message.column)) return null

        // Ids address a button across the whole configuration; a repeat would
        // make an invocation ambiguous on the PC.
        if (!seenButtonIds.add(message.id)) return null

        return StreamDeckButton(
            id = message.id,
            row = message.row,
            column = message.column,
            label = message.label,
            iconId = message.iconId?.takeIf(StreamDeckIconIds::isValid),
            glyph = message.glyph?.takeIf(String::isNotBlank),
            enabled = message.enabled,
        )
    }
}
