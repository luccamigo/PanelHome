package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*
import org.junit.Assert.*
import org.junit.Test

/**
 * Permanent version of the phase-one review probes: every way a Claude permission request can
 * end without an answer from the tablet must finish the card (never leave it open, never send a late
 * allow) and, when the user still has to act, point to the terminal. Plus the notice queue and dedupe.
 */
class LocalIntegrationsControllerTest {
    private val sampled = 1_800_000_000_000L
    private val id = "0123456789abcdef0123456789abcdef"

    private class Harness(sendResult: (PanelCommand) -> Boolean = { true }) {
        var now = 1_000L
        val sent = mutableListOf<PanelCommand>()
        val controller = LocalIntegrationsController(now = { now }, send = { sent.add(it); sendResult(it) }, onChanged = {})
        val state get() = controller.state
    }

    private fun request(id: String = this.id, expiresInMs: Long = 60_000) =
        ClaudePermissionRequest(id, "PanelOS", "Bash", "ls", "", 0, sampled + expiresInMs)

    private fun permissions(vararg requests: ClaudePermissionRequest) = ClaudePermissionsMessage(sampled, requests.toList())

    private fun Harness.connected(vararg requests: ClaudePermissionRequest): Harness {
        controller.onConnected(setOf(PanelProtocol.FEATURE_CLAUDE_PERMISSIONS, PanelProtocol.FEATURE_LOCAL_NOTICES))
        controller.apply(permissions(*requests))
        return this
    }

    private fun Harness.active(): Harness {
        connected(request())
        controller.permissionShown(id)
        assertEquals(PermissionStage.ACTIVE, state.permission?.stage)
        now += 700
        return this
    }

    private fun Harness.sending(decision: String = "allow"): Harness {
        active()
        controller.respond(id, decision)
        assertEquals(PermissionStage.SENDING, state.permission?.stage)
        return this
    }

    private fun Harness.assertFinishedPointingToTerminal() {
        assertEquals(PermissionStage.FINISHED, state.permission?.stage)
        assertTrue(state.permission?.status.orEmpty(), state.permission?.status.orEmpty().contains("terminal", ignoreCase = true))
    }

    private fun Harness.assertNothingElseSent(expected: List<PanelCommand>) = assertEquals(expected, sent)

    @Test fun withoutTheNegotiatedFeatureNoPermissionCardShows() {
        val h = Harness()
        h.controller.onConnected(setOf(PanelProtocol.FEATURE_LOCAL_NOTICES))
        h.controller.apply(permissions(request()))
        assertNull(h.state.permission)
        assertEquals(0, h.state.permissionCount)
    }

    @Test fun realHostSerializationParses() {
        val perm = PanelProtocolJson.decode("""{"sampledAtUnixMs":1794910609615,"requests":[{"id":"8a47356966564ea6bb92f139f6372d0d","project":"PanelOS","tool":"Bash","head":"aaaa","tail":"zzzz","omitted":40,"expiresAtUnixMs":1794910669611,"contentHidden":false},{"id":"c47188bbe8064f90bacf039755ac546f","project":"PanelOS","tool":"Edit","head":"C:/x/a.txt","tail":"","omitted":0,"expiresAtUnixMs":1794910639615,"contentHidden":true}],"type":"claude_permissions"}""") as ClaudePermissionsMessage
        assertEquals(40, perm.requests[0].omitted)
        assertTrue(perm.requests[1].contentHidden)
        val procs = PanelProtocolJson.decode("""{"sampledAtUnixMs":1800000000000,"sort":"cpu","grouped":false,"items":[{"name":"svc","pid":null,"startTimeUtcTicks":null,"cpuPercent":null,"memoryBytes":null,"owner":"system","protected":true,"count":3}],"type":"processes","terminateEnabled":false}""") as ProcessesMessage
        assertFalse(procs.terminateEnabled)
        assertNull(procs.items[0].pid)
        assertTrue(procs.items[0].isProtected)
    }

    // --- terminal fallbacks -------------------------------------------------------------------

    @Test fun failingToSendShownFinishesAndPointsToTerminal() {
        val h = Harness { it !is ClaudeShownCommand }.connected(request())
        h.controller.permissionShown(id)
        h.assertFinishedPointingToTerminal()
    }

    @Test fun failingToSendTheResponseFinishesAndPointsToTerminal() {
        val h = Harness { it !is ClaudeRespondCommand }.connected(request())
        h.controller.permissionShown(id)
        h.now += 700
        h.controller.respond(id, "allow")
        h.assertFinishedPointingToTerminal()
    }

    @Test fun disconnectWhileTheCardIsOpenFinishesAndPointsToTerminal() {
        val h = Harness().active()
        h.controller.onDisconnected()
        h.assertFinishedPointingToTerminal()
        h.controller.respond(id, "allow")
        h.assertNothingElseSent(listOf(ClaudeShownCommand(id)))
    }

    @Test fun disconnectAfterTheResponseWasSentFinishesAndPointsToTerminal() {
        val h = Harness().sending()
        h.controller.onDisconnected()
        h.assertFinishedPointingToTerminal()
    }

    @Test fun expiryFinishesAndNeverSendsALateAllow() {
        val h = Harness().active()
        h.now = 1_000L + 60_000
        h.controller.respond(id, "allow") // the tap lands after expiry, before the next tick
        h.assertNothingElseSent(listOf(ClaudeShownCommand(id)))
        h.controller.tick()
        h.assertFinishedPointingToTerminal()
        h.controller.respond(id, "allow")
        h.controller.respond(id, "deny")
        h.assertNothingElseSent(listOf(ClaudeShownCommand(id)))
    }

    @Test fun withdrawnByThePcBeforeAnyAnswerFinishesAndPointsToTerminal() {
        val h = Harness().active()
        h.controller.apply(permissions())
        h.assertFinishedPointingToTerminal()
        h.controller.respond(id, "allow")
        h.assertNothingElseSent(listOf(ClaudeShownCommand(id)))
    }

    @Test fun noConfirmationFiveSecondsAfterTheResponseFinishesAndPointsToTerminal() {
        val h = Harness().sending("deny")
        h.now += 4_999
        h.controller.tick()
        assertEquals(PermissionStage.SENDING, h.state.permission?.stage)
        h.now += 1
        h.controller.tick()
        h.assertFinishedPointingToTerminal()
    }

    @Test fun finishedCardDisappearsAndTheRetiredRequestIsNotShownAgain() {
        val h = Harness().active()
        h.controller.apply(permissions())
        h.now += 3_000
        h.controller.tick()
        assertNull(h.state.permission)
        h.controller.apply(permissions(request())) // the PC lists the same id again
        assertNull(h.state.permission)
    }

    // --- sending an answer does not prove it was applied -----------------------------------------

    @Test fun requestLeavingTheListAfterASentAnswerOnlySaysSent() {
        for (decision in listOf("allow", "deny")) {
            val h = Harness().sending(decision)
            h.controller.apply(permissions())
            val status = h.state.permission?.status.orEmpty()
            assertEquals(PermissionStage.FINISHED, h.state.permission?.stage)
            assertEquals("Resposta enviada. O PC já encerrou este pedido.", status)
            assertFalse(status, status.contains("retirou"))
        }
    }

    // --- notice queue and dedupe ----------------------------------------------------------------

    private fun notice(id: String, key: String? = null, receivedAt: Long = sampled, expiresInMs: Long = 600_000) =
        LocalNotice(id, "Título $id", "Texto", "info", key, null, 5, receivedAt, sampled + expiresInMs)

    private fun notices(vararg items: LocalNotice, enabled: Boolean = true) =
        LocalNoticesMessage(sampled, enabled, items.toList(), emptyList())

    private fun Harness.withNotices(vararg items: LocalNotice): Harness {
        controller.onConnected(setOf(PanelProtocol.FEATURE_LOCAL_NOTICES))
        controller.apply(notices(*items))
        return this
    }

    @Test fun theSameNoticeIdIsAppliedOnlyOnce() {
        val h = Harness().withNotices(notice("a"))
        h.controller.markNoticesSeen()
        h.controller.apply(notices(notice("a"), notice("a")))
        assertEquals(1, h.state.history.size)
        assertFalse(h.state.unseen)
        assertEquals("a", h.state.banner?.notice?.id)
    }

    @Test fun burstOfFiveShowsOneQueuesThreeAndCountsTheRestAsOverflow() {
        val items = (1..5).map { notice("n$it", receivedAt = sampled + it) }.toTypedArray()
        val h = Harness().withNotices(*items)
        assertEquals("n1", h.state.banner?.notice?.id)
        assertEquals(2, h.state.overflow)
        assertEquals(5, h.state.history.size)
        assertEquals("n5", h.state.history.first().notice.id) // history is newest first
        assertTrue(h.state.unseen)
    }

    @Test fun aNoticeWithTheSameKeyReplacesTheVisibleBannerAndDropsTheQueuedOne() {
        val h = Harness().withNotices(notice("a", key = "build"))
        h.controller.apply(notices(notice("b", key = "build", receivedAt = sampled + 1)))
        assertEquals("b", h.state.banner?.notice?.id)
        h.controller.apply(notices(notice("c", key = "deploy", receivedAt = sampled + 2),
            notice("d", key = "deploy", receivedAt = sampled + 3)))
        // c was queued, then d (same key) replaced it in the queue
        h.controller.dismissBanner()
        assertEquals("d", h.state.banner?.notice?.id)
        h.controller.dismissBanner()
        assertNull(h.state.banner)
    }

    @Test fun expiredNoticesAreIgnoredAndDisablingClearsTheQueue() {
        val h = Harness().withNotices(notice("old", expiresInMs = -1))
        assertTrue(h.state.history.isEmpty())
        assertNull(h.state.banner)
        h.controller.apply(notices(notice("a"), notice("b", receivedAt = sampled + 1)))
        h.controller.apply(notices(enabled = false))
        assertNull(h.state.banner)
        h.controller.dismissBanner()
        assertNull(h.state.banner)
    }

    @Test fun anOpenPermissionHoldsBackBannersUntilItIsGone() {
        val h = Harness().connected(request())
        h.controller.apply(notices(notice("a")))
        assertNull(h.state.banner)
        assertNotNull(h.state.permission)
        h.controller.apply(permissions())
        h.now += 3_000
        h.controller.tick()
        assertEquals("a", h.state.banner?.notice?.id)
    }
}
