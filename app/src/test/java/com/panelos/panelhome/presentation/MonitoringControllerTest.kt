package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*
import org.junit.Assert.*
import org.junit.Test

class MonitoringControllerTest {
    private var now = 0L
    private val controller = MonitoringController(now = { now }, unixNow = { 1_800_000_000_000L + now })

    @Test fun oldHostDoesNotExposeNewPagesOrAcceptSamples() {
        controller.onConnected(emptySet())
        controller.applyTelemetry(TelemetryMessage(cpu = CpuTelemetry(usagePercent = 50.0)))
        assertFalse(controller.state.computerAvailable)
        assertFalse(controller.state.processesAvailable)
        assertTrue(controller.state.telemetry.isEmpty())
    }

    @Test fun historyRetainsNullSensorsAndGapAcrossReconnect() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_COMPUTER_STATS))
        controller.applyTelemetry(TelemetryMessage(cpu = CpuTelemetry(usagePercent = 40.0)))
        now = 1_000
        controller.onDisconnected()
        now = 10_000
        controller.onConnected(setOf(PanelProtocol.FEATURE_COMPUTER_STATS))
        controller.applyTelemetry(TelemetryMessage(cpu = CpuTelemetry(usagePercent = 80.0)))
        assertEquals(listOf(40.0, null, 80.0), controller.state.telemetry.map { it.cpu })
        assertNull(controller.state.telemetry.last().gpu)
        assertEquals(10_000L, controller.state.telemetry.last().at)
    }

    @Test fun historyIsBoundedToFifteenMinutesInMemory() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_COMPUTER_STATS))
        repeat(1_000) {
            now = it * 1_000L
            controller.applyTelemetry(TelemetryMessage())
        }
        assertTrue(controller.state.telemetry.size <= 902)
        assertTrue(controller.state.telemetry.first().at >= now - 900_000)
    }

    @Test fun orderStaysFrozenDuringTouchAndForTwoSecondsAfterIt() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_PROCESSES))
        controller.applyProcesses(sample("a", "b"))
        controller.setInteracting(true)
        now = 2_000
        controller.applyProcesses(sample("b", "a"))
        assertEquals(listOf("a", "b"), names())
        assertEquals(2.0, controller.state.processRows.first().item.cpuPercent!!, 0.0)
        controller.setInteracting(false)
        now = 3_999
        controller.tick()
        assertTrue(controller.state.orderPaused)
        assertEquals(listOf("a", "b"), names())
        now = 4_000
        controller.tick()
        assertFalse(controller.state.orderPaused)
    }

    @Test fun changedRankNeedsTwoConsecutiveSamplesAndPidReuseGetsNewIdentity() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_PROCESSES))
        controller.applyProcesses(sample("a", "b"))
        controller.applyProcesses(sample("b", "a"))
        assertEquals(listOf("a", "b"), names())
        controller.applyProcesses(sample("b", "a"))
        assertEquals(listOf("b", "a"), names())
        val first = controller.state.processRows.first().key
        controller.applyProcesses(sample("b", "a").let {
            it.copy(items = it.items.map { item -> item.copy(startTimeUtcTicks = 999) })
        })
        assertNotEquals(first, controller.state.processRows.first().key)
    }

    @Test fun optionsAreRememberedAndUnacknowledgedOldOrderingIsIgnored() {
        controller.onConnected(setOf(PanelProtocol.FEATURE_PROCESSES))
        controller.setPreferences(controller.state.preferences.copy(sort = ProcessSort.MEMORY, grouped = true))
        controller.applyProcesses(sample("a"))
        assertTrue(controller.state.processRows.isEmpty())
        controller.applyProcesses(sample("a").copy(sort = ProcessSort.MEMORY, grouped = true))
        assertEquals(listOf("a"), names())
        controller.onDisconnected()
        assertEquals(ProcessSort.MEMORY, controller.state.preferences.sort)
        assertEquals(listOf("a"), names())
    }

    private fun names() = controller.state.processRows.map { it.item.name }
    private fun sample(vararg names: String) = ProcessesMessage(
        sampledAtUnixMs = now,
        items = names.mapIndexed { index, name ->
            ProcessItem(name, pid = name.first().code, startTimeUtcTicks = 1, cpuPercent = 3.0 - index)
        },
    )
}
