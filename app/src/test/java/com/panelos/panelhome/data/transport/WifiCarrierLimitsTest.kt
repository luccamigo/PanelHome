package com.panelos.panelhome.data.transport

import org.junit.Assert.*
import org.junit.Test

class WifiCarrierLimitsTest {
    @Test fun liveMemorySessionClosesOnOversizeDuplicateAndSlowConsumerWithoutLeaking() {
        for (scenario in listOf("oversize", "duplicate", "slow")) {
            val id = PlmxSessionId.create()
            val source = java.io.ByteArrayOutputStream()
            fun frame(channel: LogicalChannel, type: PlmxFrameType, length: Int, sequence: Int) {
                source.write(PlmxFrame.encodeHeader(channel, type, length, sequence, id.tag))
            }
            frame(LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, 32, 1)
            source.write(PlmxFrame.encodeHello(1, id))
            if (scenario == "oversize") frame(LogicalChannel.VIDEO, PlmxFrameType.DATA, 65537, 1)
            else {
                frame(LogicalChannel.CONTROL, PlmxFrameType.OPEN, 0, 1)
                if (scenario == "duplicate") frame(LogicalChannel.CONTROL, PlmxFrameType.OPEN, 0, 2)
                else for (sequence in 2..66) {
                    frame(LogicalChannel.CONTROL, PlmxFrameType.DATA, 1, sequence); source.write(0)
                }
            }
            val closed = java.util.concurrent.CountDownLatch(1)
            var failure: Throwable? = null
            val session = MultiplexedTransportSession(java.io.ByteArrayInputStream(source.toByteArray()),
                java.io.ByteArrayOutputStream(), {}, TransportMetrics(), fragmentBytes = 1024 * 1024,
                onClosed = { failure = it; closed.countDown() }, limits = CarrierLimits.WIFI_V1, threadId = { 0 })
            session.use {
                session.start()
                assertTrue(scenario, closed.await(3, java.util.concurrent.TimeUnit.SECONDS))
                assertTrue("$scenario: $failure", generateSequence(failure) { it.cause }.any { it is TransportProtocolException })
                val expected = when (scenario) { "oversize" -> "WiFi carrier limit exceeded"; "duplicate" -> "Duplicate WiFi OPEN"; else -> "WiFi carrier budget exceeded" }
                assertTrue("$scenario: $failure", generateSequence(failure) { it.cause }.any { it.message == expected })
                assertEquals(65536, session.fragmentBytes)
                assertEquals(CarrierLimits.WORKSPACE_BYTES.toLong(), session.carrierLiveBytes)
            }
        }
    }
    @Test fun inboundQueueHasItsOwnControlAndVideoLimitsIndependentOfTheBudget() {
        for ((channel, maximum) in listOf(LogicalChannel.CONTROL to 64, LogicalChannel.VIDEO to 32)) {
            val queue = CarrierLimits.inboundQueue<Int>(channel)
            repeat(maximum) { assertTrue(queue.offer(it)) }
            assertFalse(queue.offer(maximum))
        }
    }
    @Test fun videoReopensAfterFinAndOpenRateRemainsBounded() {
        val id = PlmxSessionId.create(); val bytes = java.io.ByteArrayOutputStream()
        fun frame(type: PlmxFrameType, sequence: Int) = bytes.write(PlmxFrame.encodeHeader(LogicalChannel.VIDEO, type, 0, sequence, id.tag))
        bytes.write(PlmxFrame.encodeHeader(LogicalChannel.TRANSPORT, PlmxFrameType.HELLO, 32, 1, id.tag))
        bytes.write(PlmxFrame.encodeHello(1, id))
        repeat(13) { frame(PlmxFrameType.OPEN, it * 2 + 1); frame(PlmxFrameType.FIN, it * 2 + 2) }
        val closed = java.util.concurrent.CountDownLatch(1); var error: Throwable? = null
        MultiplexedTransportSession(java.io.ByteArrayInputStream(bytes.toByteArray()), java.io.ByteArrayOutputStream(),
            {}, TransportMetrics(), limits = CarrierLimits.WIFI_V1, threadId = { 0 },
            onClosed = { error = it; closed.countDown() }).use { session ->
            session.start(); assertTrue(closed.await(3, java.util.concurrent.TimeUnit.SECONDS))
            assertTrue("$error", generateSequence(error) { it.cause }.any { it.message == "WiFi channel reopen rate exceeded" })
        }
        var now = 0L; val rate = CarrierOpenRate { now }
        repeat(12) { rate.record(LogicalChannel.VIDEO) }
        assertThrows(TransportProtocolException::class.java) { rate.record(LogicalChannel.VIDEO) }
        now = 60_000_000_000L; rate.record(LogicalChannel.VIDEO)
    }
    @Test fun bytesItemsAndAggregateBudgetAreBoundedAndReleaseIsIdempotent() {
        val budget = CarrierBudget(CarrierLimits.WIFI_V1)
        val items = (0 until 64).map { budget.reserve(false, LogicalChannel.CONTROL, 0)!! }
        assertThrows(TransportProtocolException::class.java) { budget.reserve(false, LogicalChannel.CONTROL, 0) }
        items.forEach { it.close(); it.close() }
        assertEquals(CarrierLimits.WORKSPACE_BYTES.toLong(), budget.liveBytes)
        assertThrows(TransportProtocolException::class.java) { budget.reserve(false, LogicalChannel.VIDEO, 65537) }
        assertThrows(TransportProtocolException::class.java) { budget.reserve(false, LogicalChannel.ASSETS, 0) }
        val random = java.util.Random(504)
        val leases = mutableListOf<AutoCloseable>()
        repeat(10000) {
            if (leases.isNotEmpty() && random.nextInt(3) == 0) leases.removeAt(random.nextInt(leases.size)).close()
            else try { leases += budget.reserve(random.nextBoolean(),
                if (random.nextBoolean()) LogicalChannel.CONTROL else LogicalChannel.VIDEO, random.nextInt(65537))!!
            } catch (_: TransportProtocolException) { }
            assertTrue(budget.liveBytes in CarrierLimits.WORKSPACE_BYTES.toLong()..CarrierLimits.SESSION_BYTES)
        }
        leases.forEach { it.close() }
        assertEquals(CarrierLimits.WORKSPACE_BYTES.toLong(), budget.liveBytes)
    }
    @Test fun cableProfileKeepsExistingLargerPayloadPolicy() {
        CarrierLimits.CABLE.validate(LogicalChannel.VIDEO, 1024 * 1024)
        assertNull(CarrierBudget(CarrierLimits.CABLE).reserve(true, LogicalChannel.VIDEO, 1024 * 1024))
    }
}
