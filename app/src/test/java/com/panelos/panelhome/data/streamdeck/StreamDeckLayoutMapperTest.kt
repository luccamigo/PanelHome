package com.panelos.panelhome.data.streamdeck

import com.panelos.panelhome.domain.protocol.StreamDeckButtonMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.StreamDeckPageMessage
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class StreamDeckLayoutMapperTest {
    @Test
    fun aPublishedLayoutBecomesPagesTheGridCanDraw() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "abc123",
                profileId = "profile-a",
                profileName = "Principal",
                pages = listOf(
                    page("page-a", "Principal", button("btn-a", 0, 0, "Play")),
                    page("page-b", "Mídia", button("btn-b", 1, 1, "Mute")),
                ),
            ),
        )

        assertEquals("abc123", layout.revision)
        assertEquals("Principal", layout.profile?.name)
        assertEquals(2, layout.pages.size)
        assertEquals("Play", layout.pages[0].buttonAt(0, 0)?.label)
        assertNull(layout.pages[0].buttonAt(1, 1))
        assertEquals(setOf<String>(), layout.iconIds())
    }

    @Test
    fun anEmptyProfileProducesALayoutWithoutPages() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(revision = "0"),
        )

        assertNull(layout.profile)
        assertTrue(layout.pages.isEmpty())
        assertEquals(false, layout.hasPages)
    }

    @Test
    fun aGridOutsideTheSupportedRangeIsDropped() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page("page-small", "Pequena", rows = 1, columns = 4),
                    page("page-big", "Grande", rows = 7, columns = 4),
                    page("page-ok", "Certa", button("btn-a", 0, 0, "Play")),
                ),
            ),
        )

        assertEquals(listOf("page-ok"), layout.pages.map { it.id })
    }

    @Test
    fun aButtonOutsideItsOwnGridIsDroppedWithoutLosingThePage() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page(
                        "page-a",
                        "Principal",
                        button("btn-good", 1, 1, "Ok"),
                        button("btn-far", 5, 0, "Fora"),
                        button("btn-negative", -1, 0, "Fora"),
                    ),
                ),
            ),
        )

        assertEquals(listOf("btn-good"), layout.pages[0].buttons.map { it.id })
    }

    @Test
    fun twoButtonsInOneSlotKeepOnlyTheFirst() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page(
                        "page-a",
                        "Principal",
                        button("btn-first", 0, 0, "Primeiro"),
                        button("btn-second", 0, 0, "Segundo"),
                    ),
                ),
            ),
        )

        assertEquals(listOf("btn-first"), layout.pages[0].buttons.map { it.id })
    }

    @Test
    fun aRepeatedButtonIdIsDroppedBecauseInvocationWouldBeAmbiguous() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page("page-a", "A", button("btn-a", 0, 0, "Um")),
                    page("page-b", "B", button("btn-a", 0, 0, "Dois")),
                ),
            ),
        )

        assertEquals(1, layout.pages[0].buttons.size)
        assertTrue(layout.pages[1].buttons.isEmpty())
        assertEquals("Um", layout.findButton("btn-a")?.label)
    }

    @Test
    fun onlyWellFormedIconIdsSurvive() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page(
                        "page-a",
                        "A",
                        button("btn-a", 0, 0, "Ok", iconId = "icon-aaaaaaaaaa"),
                        button("btn-b", 0, 1, "Mau", iconId = "..\\..\\x"),
                    ),
                ),
            ),
        )

        assertEquals("icon-aaaaaaaaaa", layout.findButton("btn-a")?.iconId)
        assertNull(layout.findButton("btn-b")?.iconId)
        assertEquals(setOf("icon-aaaaaaaaaa"), layout.iconIds())
    }

    @Test
    fun aDisabledButtonIsKeptSoItCanBeDrawnAsUnavailable() {
        val layout = StreamDeckLayoutMapper.toLayout(
            StreamDeckLayoutMessage(
                revision = "r",
                pages = listOf(
                    page(
                        "page-a",
                        "A",
                        button("btn-off", 0, 0, "Desligado", enabled = false),
                    ),
                ),
            ),
        )

        val button = layout.findButton("btn-off")
        assertNotNull(button)
        assertEquals(false, button!!.enabled)
    }

    private fun page(
        id: String,
        name: String,
        vararg buttons: StreamDeckButtonMessage,
        rows: Int = 2,
        columns: Int = 2,
    ) = StreamDeckPageMessage(
        id = id,
        name = name,
        rows = rows,
        columns = columns,
        buttons = buttons.toList(),
    )

    private fun button(
        id: String,
        row: Int,
        column: Int,
        label: String,
        iconId: String? = null,
        enabled: Boolean = true,
    ) = StreamDeckButtonMessage(
        id = id,
        row = row,
        column = column,
        label = label,
        iconId = iconId,
        enabled = enabled,
    )
}
