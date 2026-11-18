package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.LoopMediaRequestCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeCommand
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PanelOutgoingCommandQueueTest {
    @Test
    fun mediaRequestsStayOrderedWithExistingCommandsAndRejectWhenFull() = runBlocking {
        val queue = PanelOutgoingCommandQueue(capacity = 3)
        val first = LoopMediaRequestCommand("a".repeat(64), 0)
        val next = first.copy(offset = 262144)
        assertEquals(PanelOutgoingOffer.ENQUEUED, queue.offer(first, 7L))
        assertEquals(PanelOutgoingOffer.ENQUEUED, queue.offer(MediaCommand.NEXT, 7L))
        assertEquals(PanelOutgoingOffer.ENQUEUED, queue.offer(next, 7L))
        assertEquals(PanelOutgoingOffer.REJECTED, queue.offer(next.copy(offset = 524288), 7L))
        assertEquals(first, queue.take()!!.command)
        assertEquals(MediaCommand.NEXT, queue.take()!!.command)
        assertEquals(next, queue.take()!!.command)
    }

    @Test
    fun tenThousandMovesStayCoalescedAndBounded() = runBlocking {
        val queue = PanelOutgoingCommandQueue(capacity = 8)
        queue.offer(touch(TouchCommandAction.DOWN, 0.1), 1)
        repeat(10_000) { index ->
            queue.offer(touch(TouchCommandAction.MOVE, index / 10_000.0), 1)
        }
        queue.offer(touch(TouchCommandAction.UP, 1.0), 1)

        assertEquals(3, queue.size())
        assertEquals(TouchCommandAction.DOWN, (queue.take()!!.command as TouchCommand).action)
        assertEquals(0.9999, (queue.take()!!.command as TouchCommand).x!!, 0.0)
        assertEquals(TouchCommandAction.UP, (queue.take()!!.command as TouchCommand).action)
        assertTrue(queue.metrics().queueDrop >= 9_999)
        assertTrue(queue.metrics().queueHighWatermark <= 8)
    }

    @Test
    fun orderedCommandsRejectExplicitlyWhenSaturated() {
        val queue = PanelOutgoingCommandQueue(capacity = 2)
        assertEquals(PanelOutgoingOffer.ENQUEUED, queue.offer(MediaCommand.NEXT, 1))
        assertEquals(PanelOutgoingOffer.ENQUEUED, queue.offer(MediaCommand.PREVIOUS, 1))

        val result = queue.offer(
            StreamDeckInvokeCommand("button", "revision"),
            connectionId = 1,
        )

        assertEquals(PanelOutgoingOffer.REJECTED, result)
        assertEquals(1, queue.metrics().queueReject)
        assertEquals(2, queue.size())
    }

    @Test
    fun criticalBoundaryEvictsCoalescibleStateBeforeRejecting() = runBlocking {
        val queue = PanelOutgoingCommandQueue(capacity = 2)
        queue.offer(touch(TouchCommandAction.MOVE, 0.4), 1)
        queue.offer(MediaCommand.NEXT, 1)

        assertEquals(
            PanelOutgoingOffer.ENQUEUED,
            queue.offer(touch(TouchCommandAction.CANCEL, null), 1),
        )

        assertEquals(MediaCommand.NEXT, queue.take()!!.command)
        assertEquals(TouchCommandAction.CANCEL, (queue.take()!!.command as TouchCommand).action)
    }

    private fun touch(action: TouchCommandAction, x: Double?) = TouchCommand(
        action = action,
        pointerId = 0,
        x = x,
        y = x,
        generation = 7,
    )
}
