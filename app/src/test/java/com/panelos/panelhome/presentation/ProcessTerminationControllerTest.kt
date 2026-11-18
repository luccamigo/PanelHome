package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*
import org.json.JSONObject
import org.junit.Assert.*
import org.junit.Test

class ProcessTerminationControllerTest {
    private var now = 0L
    private val sent = mutableListOf<PanelCommand>()
    private var accept = true
    private val controller = MonitoringController(
        now = { now }, unixNow = { 1_800_000_000_000L + now },
        send = { sent.add(it); accept }, newRequestId = { "a".repeat(32) },
    )

    private fun connect(enabled: Boolean = true, grouped: Boolean = false, items: List<ProcessItem> = listOf(own())) {
        controller.onConnected(setOf(PanelProtocol.FEATURE_PROCESSES))
        if (grouped) controller.setPreferences(controller.state.preferences.copy(grouped = true))
        controller.applyProcesses(ProcessesMessage(1_800_000_000_000L, grouped = grouped, terminateEnabled = enabled, items = items))
    }

    private fun own(name: String = "node.exe", pid: Int = 18420) = ProcessItem(
        name, pid = pid, startTimeUtcTicks = 638_000_000_000_000_000L, cpuPercent = 4.1, memoryBytes = 410_000_000,
        owner = "current_user", isProtected = false)

    private fun row() = controller.state.processRows.single()
    private fun reply(result: String, requestId: String? = "a".repeat(32)) =
        controller.applyTerminateResult(ProcessTerminateResultMessage(requestId, 18420, result))

    @Test fun nothingCanBeEndedWhileTheGateIsOff() {
        connect(enabled = false)
        assertFalse(controller.state.canTerminate(row()))
        controller.requestTerminate(row().key)
        assertTrue(sent.isEmpty())
        assertNull(controller.state.termination)
    }

    @Test fun onlyAnUngroupedReadableProcessOfTheCurrentUserIsOffered() {
        connect(items = listOf(own("a.exe", 10), own("b.exe", 11).copy(owner = "other_user"), own("c.exe", 12).copy(isProtected = true),
            own("d.exe", 13).copy(cpuPercent = null, memoryBytes = null), own("e.exe", 14).copy(startTimeUtcTicks = null),
            own("f.exe", 15).copy(pid = null), own("g.exe", 16).copy(owner = "system"), own("h.exe", 17).copy(owner = "unknown")))
        assertEquals(listOf("a.exe"), controller.state.processRows.filter { controller.state.canTerminate(it) }.map { it.item.name })
        controller.setPreferences(controller.state.preferences.copy(grouped = true))
        controller.applyProcesses(ProcessesMessage(1_800_000_000_001L, grouped = true, terminateEnabled = true, items = listOf(own().copy(count = 3))))
        assertTrue(controller.state.processRows.none { controller.state.canTerminate(it) })
    }

    @Test fun commandCarriesPidAndStartTimeAndNothingElse() {
        connect()
        controller.requestTerminate(row().key)
        val command = sent.single() as ProcessTerminateCommand
        assertEquals("a".repeat(32), command.requestId)
        assertEquals(18420, command.pid)
        assertEquals(638_000_000_000_000_000L, command.startTimeUtcTicks)
        val json = JSONObject(PanelProtocolJson.encode(CommandMessage(command)))
        assertEquals(setOf("type", "command", "requestId", "pid", "startTimeUtcTicks"), json.keys().asSequence().toSet())
        assertEquals(TerminationStage.SENDING, controller.state.termination?.stage)
    }

    @Test fun aSecondRequestWaitsForTheFirstAnswer() {
        connect()
        controller.requestTerminate(row().key)
        assertFalse(controller.state.canTerminate(row()))
        controller.requestTerminate(row().key)
        assertEquals(1, sent.size)
    }

    @Test fun pcAnswersBecomeSpecificTexts() {
        val expected = mapOf(
            "terminated" to "node.exe encerrado.",
            "denied" to "Não consegui encerrar node.exe: o Windows negou o acesso.",
            "protected" to "O PC recusou: node.exe é um processo protegido.",
            "gone" to "node.exe já tinha fechado.",
        )
        expected.forEach { (result, text) ->
            sent.clear()
            connect()
            controller.requestTerminate(row().key)
            reply(result)
            assertEquals(text, controller.state.termination?.message)
            assertEquals(result != "terminated", controller.state.termination?.failed)
        }
    }

    @Test fun answerForAnotherRequestIsIgnored() {
        connect()
        controller.requestTerminate(row().key)
        reply("terminated", requestId = "b".repeat(32))
        assertEquals(TerminationStage.SENDING, controller.state.termination?.stage)
    }

    @Test fun disabledAnswerTurnsTheGateOffUntilTheNextSample() {
        connect()
        controller.requestTerminate(row().key)
        reply("disabled")
        assertFalse(controller.state.terminateEnabled)
    }

    @Test fun noAnswerInFiveSecondsAsksToCheckTheListAndNeverClaimsSuccess() {
        connect()
        controller.requestTerminate(row().key)
        now = 4_999
        controller.tick()
        assertEquals(TerminationStage.SENDING, controller.state.termination?.stage)
        now = 5_000
        controller.tick()
        assertEquals("O PC não respondeu. Confira a lista antes de tentar de novo.", controller.state.termination?.message)
        assertTrue(controller.state.termination?.failed == true)
        now = 20_000
        controller.tick()
        assertNull(controller.state.termination)
    }

    @Test fun losingTheConnectionMidRequestSaysNothingWasEnded() {
        connect()
        controller.requestTerminate(row().key)
        controller.onDisconnected()
        assertEquals("Sem conexão com o PC. Nada foi encerrado.", controller.state.termination?.message)
        assertFalse(controller.state.terminateEnabled)
        reply("terminated")
        assertTrue(controller.state.termination?.failed == true)
    }

    @Test fun aQueueRefusalIsReportedAndNothingIsPending() {
        accept = false
        connect()
        controller.requestTerminate(row().key)
        assertEquals("Sem conexão com o PC. Nada foi encerrado.", controller.state.termination?.message)
        assertEquals(TerminationStage.FINISHED, controller.state.termination?.stage)
    }

    @Test fun processesTheHostMarksProtectedNeverGetAButtonEvenForTheCurrentUser() {
        connect(items = listOf(own("OpenBot.exe", 20).copy(isProtected = true), own("cua-driver.exe", 21).copy(isProtected = true), own()))
        val offered = controller.state.processRows.filter { controller.state.canTerminate(it) }.map { it.item.name }
        assertEquals(listOf("node.exe"), offered)
        val protectedRow = controller.state.processRows.first { it.item.name == "OpenBot.exe" }
        controller.requestTerminate(protectedRow.key)
        assertTrue(sent.isEmpty())
    }

    @Test fun anEndedRowCannotBeTargeted() {
        connect()
        val key = row().key
        controller.setInteracting(true)
        controller.applyProcesses(ProcessesMessage(1_800_000_001_000L, terminateEnabled = true, items = emptyList()))
        controller.requestTerminate(key)
        assertTrue(sent.isEmpty())
    }
}
