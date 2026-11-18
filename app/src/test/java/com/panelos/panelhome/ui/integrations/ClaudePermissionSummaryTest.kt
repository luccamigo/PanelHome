package com.panelos.panelhome.ui.integrations

import com.panelos.panelhome.domain.protocol.*
import com.panelos.panelhome.presentation.LocalIntegrationsController
import org.junit.Assert.*
import org.junit.Test

class ClaudePermissionSummaryTest {
    private fun request(extra: String = "") = """{"type":"claude_permissions","sampledAtUnixMs":1800000000000,"requests":[{"id":"0123456789abcdef0123456789abcdef","project":"PanelOS","tool":"Edit","head":"C:/x/a.txt","tail":"","omitted":0,"expiresAtUnixMs":1800000060000$extra}]}"""
    private fun parse(source: String) = (PanelProtocolJson.decode(source) as ClaudePermissionsMessage).requests.single()

    @Test fun absentContentHiddenMeansFalseForOlderHosts() {
        val parsed = parse(request())
        assertFalse(parsed.contentHidden)
        assertTrue(incompleteSummaryWarnings(parsed).isEmpty())
    }

    @Test fun contentHiddenAndOmittedEachGetTheirOwnPlainWarning() {
        val hidden = parse(request(""","contentHidden":true"""))
        assertEquals(listOf("O conteúdo da edição não está visível neste resumo"), incompleteSummaryWarnings(hidden))
        val both = hidden.copy(omitted = 40)
        assertEquals(listOf("Texto cortado · 40 caracteres omitidos", "O conteúdo da edição não está visível neste resumo"),
            incompleteSummaryWarnings(both))
        assertTrue(both.summaryIncomplete)
    }

    @Test fun contentHiddenMustBeABoolean() {
        assertThrows(Exception::class.java) { parse(request(""","contentHidden":"yes"""")) }
        assertThrows(Exception::class.java) { parse(request(""","contentHidden":1""")) }
    }

    @Test fun contentHiddenRoundTrips() {
        val message = PanelProtocolJson.decode(request(""","contentHidden":true""")) as ClaudePermissionsMessage
        assertEquals(message, PanelProtocolJson.decode(PanelProtocolJson.encode(message)))
    }

    @Test fun incompleteSummaryKeepsEveryDecisionAvailableAfterTheTouchGuard() {
        for (hidden in listOf(false, true)) for (omitted in listOf(0, 40)) for (decision in listOf("allow", "deny", "defer")) {
            var now = 1_000L
            val sent = mutableListOf<PanelCommand>()
            val controller = LocalIntegrationsController(now = { now }, send = { sent.add(it); true }, onChanged = {})
            controller.onConnected(setOf(PanelProtocol.FEATURE_CLAUDE_PERMISSIONS))
            val request = parse(request()).copy(contentHidden = hidden, omitted = omitted)
            controller.apply(ClaudePermissionsMessage(1_800_000_000_000L, listOf(request)))
            controller.permissionShown(request.id)
            if (decision != "defer") {
                controller.respond(request.id, decision)
                assertEquals(listOf(ClaudeShownCommand(request.id)), sent)
            }
            now += 700
            controller.respond(request.id, decision)
            assertEquals(listOf(ClaudeShownCommand(request.id), ClaudeRespondCommand(request.id, decision)), sent)
            controller.respond(request.id, decision)
            assertEquals(2, sent.size)
        }
    }
}
