package com.panelos.panelhome.data.video

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LatestVideoFrameStoreTest {
    private val store = LatestVideoFrameStore()

    @Test
    fun latestFrameReplacesOlderFrameAndStaleFrameIsDropped() {
        assertTrue(store.publish(frame(sequence = 2, value = 2)))
        assertFalse(store.publish(frame(sequence = 1, value = 1)))
        assertTrue(store.publish(frame(sequence = 3, value = 3)))

        val observed = store.withLatest { it.pixels.copyOf() }
        assertArrayEquals(byteArrayOf(3, 3, 3, 3), observed)
        assertEquals(3L, store.withLatest { it.sequence })
    }

    @Test
    fun expectedSequenceAndClearPreventRenderingObsoletePixels() {
        store.publish(frame(sequence = 8, value = 8))

        assertNull(store.withLatest(expectedSequence = 7) { it.sequence })
        assertEquals(8L, store.withLatest(expectedSequence = 8) { it.sequence })

        store.clear()
        assertNull(store.withLatest { it.sequence })
    }

    private fun frame(sequence: Long, value: Byte) = DecodedVideoFrame(
        sequence = sequence,
        captureTimestampMicros = sequence * 100,
        width = 1,
        height = 1,
        pixels = byteArrayOf(value, value, value, value),
        pixelCountBytes = 4,
    )
}
