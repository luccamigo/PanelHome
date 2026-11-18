package com.panelos.panelhome.data.transport

import java.io.ByteArrayInputStream
import java.io.EOFException
import java.io.InputStream
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotSame
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Guards the accessory read path and payload ownership rules the transport CPU
 * work depends on. The old path issued one syscall for every 16-byte header;
 * these tests pin the staged behaviour that replaced it. Resynchronization
 * behaviour on the same reader is covered by [PlmxV2Test].
 */
class TransportIoTest {
    @Test
    fun oneStagedReadServesHeaderAndPayload() {
        val wire = ByteArray(24) { it.toByte() }
        val source = CountingInputStream(ByteArrayInputStream(wire))
        val profile = TransportIoProfile()
        val reader = PlmxStreamReader(source, profile, STAGING)

        val header = ByteArray(16)
        val payload = ByteArray(8)
        reader.readFully(header, 0, 16)
        reader.readFully(payload, 0, 8)

        assertEquals(1, source.reads)
        assertEquals(1L, profile.readCalls)
        assertArrayEquals(wire.copyOfRange(0, 16), header)
        assertArrayEquals(wire.copyOfRange(16, 24), payload)
    }

    @Test
    fun refillAlwaysRequestsAWholeStagingBufferSoTheGadgetPostsWholePackets() {
        // f_accessory sets its USB request length from the read() count. A
        // request smaller than a max packet risks a babble error once the host
        // coalesces a header and payload into one transfer.
        val source = CountingInputStream(ByteArrayInputStream(ByteArray(40)))
        val reader = PlmxStreamReader(source, TransportIoProfile(), STAGING)

        reader.readFully(ByteArray(16), 0, 16)

        assertEquals(listOf(STAGING), source.requestedLengths)
    }

    @Test
    fun aPayloadLargerThanTheStagingBufferIsReadStraightIntoTheDestination() {
        val wire = ByteArray(STAGING * 2) { (it * 31 + 17).toByte() }
        val source = CountingInputStream(ByteArrayInputStream(wire))
        val profile = TransportIoProfile()
        val reader = PlmxStreamReader(source, profile, STAGING)

        val target = ByteArray(wire.size)
        reader.readFully(target, 0, wire.size)

        assertArrayEquals(wire, target)
        assertTrue("A large tail must bypass staging", profile.directReads > 0)
        source.requestedLengths.forEach {
            assertTrue("Every posted request must be at least one staging buffer", it >= STAGING)
        }
    }

    @Test
    fun partialUnderlyingReadsAreReassembledWithoutLoss() {
        val wire = ByteArray(1000) { (it % 251).toByte() }
        val source = CountingInputStream(ByteArrayInputStream(wire), maximumRead = 7)
        val reader = PlmxStreamReader(source, TransportIoProfile(), STAGING)

        val target = ByteArray(wire.size)
        reader.readFully(target, 0, wire.size)

        assertArrayEquals(wire, target)
    }

    @Test(expected = EOFException::class)
    fun endOfStreamMidFrameIsReportedRatherThanReturningShort() {
        val source = CountingInputStream(ByteArrayInputStream(ByteArray(4)))
        val reader = PlmxStreamReader(source, TransportIoProfile(), STAGING)

        reader.readFully(ByteArray(16), 0, 16)
    }

    @Test
    fun aReleasedPayloadIsHandedOutAgainInsteadOfAllocating() {
        val profile = TransportIoProfile()
        val pool = PayloadBufferPool(bufferBytes = 1024, maximumBuffers = 4, profile = profile)

        val first = pool.rent(512)
        first.release()
        val second = pool.rent(700)

        assertSame("A recycled buffer must be reused", first.bytes, second.bytes)
        assertEquals(700, second.length)
        assertEquals(1L, profile.payloadReturns)
    }

    @Test
    fun releasingTwiceCannotHandTheSameBufferToTwoOwners() {
        val profile = TransportIoProfile()
        val pool = PayloadBufferPool(bufferBytes = 1024, maximumBuffers = 4, profile = profile)

        val payload = pool.rent(512)
        payload.release()
        payload.release()

        assertEquals("A double release must not duplicate the buffer", 1L, profile.payloadReturns)
        val first = pool.rent(16)
        val second = pool.rent(16)
        assertNotSame(first.bytes, second.bytes)
    }

    @Test
    fun emptyAndOversizedPayloadsBypassThePool() {
        val profile = TransportIoProfile()
        val pool = PayloadBufferPool(bufferBytes = 64, maximumBuffers = 2, profile = profile)

        val empty = pool.rent(0)
        val oversized = pool.rent(256)
        empty.release()
        oversized.release()

        assertEquals(0, empty.length)
        assertEquals(256, oversized.bytes.size)
        assertEquals("Neither buffer may be recycled into the fixed-size pool", 0L, profile.payloadReturns)
    }

    @Test
    fun theBoundedPoolStopsRetainingBuffersAtItsLimit() {
        val profile = TransportIoProfile()
        val pool = PayloadBufferPool(bufferBytes = 32, maximumBuffers = 2, profile = profile)

        // Rent everything first: renting and releasing one at a time would
        // never put more than a single buffer back into the pool.
        val outstanding = (0 until 5).map { pool.rent(32) }
        outstanding.forEach { it.release() }

        assertEquals("The pool must not grow past its bound", 2L, profile.payloadReturns)
    }

    @Test
    fun fragmentSizeStaysAWholeNumberOfMaxPackets() {
        assertEquals(512, MultiplexFrame.sanitizeFragmentBytes(1))
        assertEquals(512, MultiplexFrame.sanitizeFragmentBytes(1000))
        assertEquals(65_536, MultiplexFrame.sanitizeFragmentBytes(65_536))
        assertEquals(
            MultiplexFrame.MAX_VIDEO_PAYLOAD,
            MultiplexFrame.sanitizeFragmentBytes(Int.MAX_VALUE),
        )
    }

    @Test
    fun headerEncodesIntoACallerBufferWithoutAllocating() {
        val target = ByteArray(MultiplexFrame.HEADER_SIZE + 4)
        MultiplexFrame.encodeHeader(target, LogicalChannel.CONTROL, flags = 2, payloadLength = 33, sequence = 9)

        val decoded = MultiplexFrame.decodeHeader(target.copyOf(MultiplexFrame.HEADER_SIZE))
        assertEquals(LogicalChannel.CONTROL, decoded.channel)
        assertEquals(2, decoded.flags)
        assertEquals(33, decoded.payloadLength)
        assertEquals(9L, decoded.sequence)
    }

    private companion object {
        const val STAGING = 16 * 1024
    }

    private class CountingInputStream(
        private val inner: InputStream,
        private val maximumRead: Int = Int.MAX_VALUE,
    ) : InputStream() {
        val requestedLengths = mutableListOf<Int>()
        val reads get() = requestedLengths.size

        override fun read(): Int = inner.read()

        override fun read(buffer: ByteArray, offset: Int, length: Int): Int {
            requestedLengths += length
            return inner.read(buffer, offset, minOf(length, maximumRead))
        }
    }
}
