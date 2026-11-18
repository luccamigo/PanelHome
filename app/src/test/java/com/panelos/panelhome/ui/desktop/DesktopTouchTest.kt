package com.panelos.panelhome.ui.desktop

import com.panelos.panelhome.domain.model.DesktopTouchEvent

import com.panelos.panelhome.domain.protocol.TouchCommandAction
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class DesktopTouchTest {
    @Test
    fun contentRectMatchesMediaCodecScaleToFitInsideSafeDrawingSurface() {
        val rect = desktopContentRect(
            sourceWidth = 1280,
            sourceHeight = 800,
            viewWidth = 1280,
            viewHeight = 748,
        )

        assertEquals(41.6f, rect.left, 0.01f)
        assertEquals(0f, rect.top, 0f)
        assertEquals(1237.4f, rect.right, 0.01f)
        assertEquals(747f, rect.bottom, 0.01f)
    }

    @Test
    fun downOutsideVisibleDesktopIsNotForwarded() {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events)

        val handled = mapper.handle(
            DesktopMotionAction.DOWN,
            pointerCount = 1,
            x = 10f,
            y = 300f,
            viewWidth = 1280,
            viewHeight = 748,
        )

        assertFalse(handled)
        assertTrue(events.isEmpty())
    }

    @Test
    fun downMoveAndUpNormalizeAgainstVisibleContentAndClampActiveDrag()
    {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events)
        val rect = desktopContentRect(1280, 800, 1280, 748)

        assertTrue(mapper.handle(DesktopMotionAction.DOWN, 1, rect.left, rect.top, 1280, 748))
        assertTrue(mapper.handle(DesktopMotionAction.MOVE, 1, 5000f, -50f, 1280, 748))
        assertTrue(mapper.handle(DesktopMotionAction.UP, 1, rect.right, rect.bottom, 1280, 748))

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.MOVE, TouchCommandAction.UP),
            events.map(DesktopTouchEvent::action),
        )
        assertNormalized(events[0], 0.0, 0.0)
        assertNormalized(events[1], 1.0, 0.0)
        assertNormalized(events[2], 1.0, 1.0)
    }

    @Test
    fun invalidOrderingNeverEmitsMoveUpOrDuplicateDown()
    {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events)

        assertFalse(mapper.handle(DesktopMotionAction.MOVE, 1, 640f, 374f, 1280, 748))
        assertFalse(mapper.handle(DesktopMotionAction.UP, 1, 640f, 374f, 1280, 748))
        assertTrue(mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748))
        assertFalse(mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748))

        assertEquals(listOf(TouchCommandAction.DOWN), events.map(DesktopTouchEvent::action))
    }

    @Test
    fun cancelAndSurfaceRecreationEndTheActiveContact()
    {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events)
        mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748)

        assertTrue(mapper.handle(DesktopMotionAction.CANCEL, 1, 640f, 374f, 1280, 748))
        assertFalse(mapper.handle(DesktopMotionAction.UP, 1, 640f, 374f, 1280, 748))

        mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748)
        mapper.cancelActive()

        assertEquals(
            listOf(
                TouchCommandAction.DOWN,
                TouchCommandAction.CANCEL,
                TouchCommandAction.DOWN,
                TouchCommandAction.CANCEL,
            ),
            events.map(DesktopTouchEvent::action),
        )
    }

    @Test
    fun additionalPointerCancelsSingleTouchStateCleanly()
    {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events)
        mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748)

        assertTrue(mapper.handle(DesktopMotionAction.MOVE, 2, 650f, 380f, 1280, 748))
        assertFalse(mapper.handle(DesktopMotionAction.UP, 1, 650f, 380f, 1280, 748))

        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.CANCEL),
            events.map(DesktopTouchEvent::action),
        )
    }

    @Test
    fun generationChangeCancelsOldContactAndRequiresANewDown()
    {
        val events = mutableListOf<DesktopTouchEvent>()
        val mapper = boundMapper(events, generation = 3)
        mapper.handle(DesktopMotionAction.DOWN, 1, 640f, 374f, 1280, 748)

        mapper.bind(generation = 4, sourceWidth = 1280, sourceHeight = 800)
        assertFalse(mapper.handle(DesktopMotionAction.MOVE, 1, 650f, 380f, 1280, 748))
        assertTrue(mapper.handle(DesktopMotionAction.DOWN, 1, 650f, 380f, 1280, 748))

        assertEquals(
            listOf(3L, 3L, 4L),
            events.map(DesktopTouchEvent::generation),
        )
        assertEquals(
            listOf(TouchCommandAction.DOWN, TouchCommandAction.CANCEL, TouchCommandAction.DOWN),
            events.map(DesktopTouchEvent::action),
        )
    }

    private fun boundMapper(
        events: MutableList<DesktopTouchEvent>,
        generation: Long = 3,
    ) = DesktopTouchGestureMapper(events::add).also {
        it.bind(generation = generation, sourceWidth = 1280, sourceHeight = 800)
    }

    private fun assertNormalized(event: DesktopTouchEvent, x: Double, y: Double) {
        assertEquals(x, requireNotNull(event.x), 0.000_001)
        assertEquals(y, requireNotNull(event.y), 0.000_001)
        assertEquals(3L, event.generation)
    }
}
