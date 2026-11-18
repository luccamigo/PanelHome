package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*

data class NoticeUi(val notice: LocalNotice, val receivedAt: Long, val expiresAt: Long)
data class PublishedValueUi(val value: LocalPublishedValue, val updatedAt: Long, val expiresAt: Long?)
enum class PermissionStage { WAITING, ACTIVE, SENDING, FINISHED }
data class PermissionUi(
    val request: ClaudePermissionRequest,
    val expiresAt: Long,
    val shownAt: Long? = null,
    val stage: PermissionStage = PermissionStage.WAITING,
    val status: String? = null,
    val finishAt: Long? = null,
    val sentAt: Long? = null,
)
data class LocalIntegrationsState(
    val noticesAvailable: Boolean = false,
    val permissionsAvailable: Boolean = false,
    val enabled: Boolean = false,
    val connected: Boolean = false,
    val values: List<PublishedValueUi> = emptyList(),
    val history: List<NoticeUi> = emptyList(),
    val banner: NoticeUi? = null,
    val overflow: Int = 0,
    val unseen: Boolean = false,
    val touchActive: Boolean = false,
    val permission: PermissionUi? = null,
    val permissionCount: Int = 0,
    val now: Long = 0,
) {
    fun disconnected(at: Long) = copy(connected = false, banner = null, touchActive = false,
        permission = permission?.copy(stage = PermissionStage.FINISHED, finishAt = at + 4_000,
            status = "Sem conexão com o PC. O Claude Code vai perguntar no terminal."))
}

/** No requests, notices or values are persisted, logged, or interpreted as code. */
internal class LocalIntegrationsController(
    private val now: () -> Long = { System.nanoTime() / 1_000_000 },
    private val send: (PanelCommand) -> Boolean,
    private val onPermissionReady: () -> Unit = {},
    initialState: LocalIntegrationsState? = null,
    private val onChanged: (LocalIntegrationsState) -> Unit,
) {
    var state = initialState ?: LocalIntegrationsState()
        private set
    private val seen = state.history.mapTo(linkedSetOf()) { it.notice.id }
    private val retiredPermissions = linkedSetOf<String>()
    private val banners = ArrayDeque<NoticeUi>()
    private var bannerRemaining = 0L
    private var bannerShown = 0L
    private var lastTick = now()
    private var incomingPermissions = emptyList<PermissionUi>()

    fun onConnected(features: Set<String>) {
        incomingPermissions = emptyList()
        retiredPermissions.clear()
        publish(state.copy(connected = true, noticesAvailable = PanelProtocol.FEATURE_LOCAL_NOTICES in features,
            permissionsAvailable = PanelProtocol.FEATURE_CLAUDE_PERMISSIONS in features, permission = null, touchActive = false))
    }
    fun onDisconnected() {
        banners.clear()
        incomingPermissions = emptyList()
        publish(state.disconnected(now()))
    }
    fun apply(message: LocalNoticesMessage) {
        if (!state.connected || !state.noticesAvailable) return
        val at = now()
        val values = message.values.map { v -> PublishedValueUi(v, at - (message.sampledAtUnixMs - v.updatedAtUnixMs).coerceAtLeast(0),
            v.expiresAtUnixMs?.let { at + (it - message.sampledAtUnixMs) }) }.filter { it.expiresAt == null || it.expiresAt > at }
        var history = state.history
        var unseen = state.unseen
        message.notices.sortedBy { it.receivedAtUnixMs }.forEach { notice ->
            if (!seen.add(notice.id)) return@forEach
            while (seen.size > 256) seen.remove(seen.first())
            val value = NoticeUi(notice, at - (message.sampledAtUnixMs - notice.receivedAtUnixMs).coerceAtLeast(0),
                at + (notice.expiresAtUnixMs - message.sampledAtUnixMs))
            if (value.expiresAt <= at) return@forEach
            history = (listOf(value) + history.filter { it.notice.id != notice.id }).take(20)
            unseen = true
            if (notice.key != null && state.banner?.notice?.key == notice.key) {
                publish(state.copy(banner = value))
                bannerRemaining = notice.durationSeconds * 1_000L
                bannerShown = at
            } else {
                if (notice.key != null) banners.removeAll { it.notice.key == notice.key }
                if (banners.size < 3) banners.addLast(value) else publish(state.copy(overflow = state.overflow + 1))
            }
        }
        if (!message.enabled) { banners.clear(); publish(state.copy(banner = null)) }
        publish(state.copy(enabled = message.enabled, values = values, history = history, unseen = unseen, now = at))
        showNextBanner()
    }
    fun apply(message: ClaudePermissionsMessage) {
        if (!state.connected || !state.permissionsAvailable) return
        val at = now()
        incomingPermissions = message.requests.map { PermissionUi(it, at + (it.expiresAtUnixMs - message.sampledAtUnixMs)) }
            .filter { it.expiresAt > at }
        val current = state.permission
        if (current != null && current.stage != PermissionStage.FINISHED && incomingPermissions.none { it.request.id == current.request.id }) {
            finishPermission(if (current.stage == PermissionStage.SENDING) "Resposta enviada. O PC já encerrou este pedido."
                else "Este pedido terminou ou foi cancelado no PC. Confira o terminal.")
        }
        publish(state.copy(permissionCount = incomingPermissions.size, now = at))
        showNextPermission()
    }
    fun setTouchActive(active: Boolean) {
        publish(state.copy(touchActive = active))
        if (!active) { showNextPermission(); showNextBanner() }
    }
    fun markNoticesSeen() = publish(state.copy(unseen = false))
    fun dismissBanner() {
        publish(state.copy(banner = null))
        showNextBanner()
    }
    fun permissionShown(id: String) {
        val current = state.permission ?: return
        if (current.request.id != id || current.stage != PermissionStage.WAITING || !state.connected) return
        if (!send(ClaudeShownCommand(id))) { finishPermission("Não consegui abrir o pedido. Confira o terminal."); return }
        publish(state.copy(permission = current.copy(shownAt = now(), stage = PermissionStage.ACTIVE), now = now()))
    }
    fun respond(id: String, decision: String) {
        val current = state.permission ?: return
        if (!state.connected || current.request.id != id || current.stage != PermissionStage.ACTIVE || now() >= current.expiresAt) return
        if (decision !in setOf("allow", "deny", "defer")) return
        if (decision != "defer" && now() - (current.shownAt ?: return) < 700) return
        if (!send(ClaudeRespondCommand(id, decision))) {
            finishPermission("Não consegui enviar a resposta. O Claude Code vai perguntar no terminal."); return
        }
        publish(state.copy(permission = current.copy(stage = PermissionStage.SENDING,
            status = "Resposta enviada. Aguardando o PC…", sentAt = now())))
    }
    fun tick() {
        val at = now()
        val elapsed = (at - lastTick).coerceAtLeast(0)
        lastTick = at
        if (!state.touchActive && state.permission == null && state.banner != null) {
            bannerRemaining -= elapsed
            if (bannerRemaining <= 0 || (banners.isNotEmpty() && at - bannerShown >= 2_000)) publish(state.copy(banner = null))
        }
        val permission = state.permission
        when {
            permission == null -> Unit
            permission.stage == PermissionStage.FINISHED && at >= (permission.finishAt ?: at) -> publish(state.copy(permission = null))
            permission.stage != PermissionStage.FINISHED && at >= permission.expiresAt -> finishPermission("Prazo encerrado. Responda no terminal.")
            permission.stage == PermissionStage.SENDING && permission.sentAt?.let { at - it >= 5_000 } == true ->
                finishPermission("O PC não confirmou a resposta. Confira no terminal.")
        }
        val values = state.values.filter { it.expiresAt == null || it.expiresAt > at }
        if (state.permission != null || state.banner != null || at / 1_000 != state.now / 1_000 || values != state.values) {
            publish(state.copy(now = at, values = values))
        }
        showNextPermission()
        showNextBanner()
    }
    private fun finishPermission(message: String) {
        state.permission?.request?.id?.let { retiredPermissions.add(it) }
        while (retiredPermissions.size > 64) retiredPermissions.remove(retiredPermissions.first())
        publish(state.copy(permission = state.permission?.copy(stage = PermissionStage.FINISHED, status = message, finishAt = now() + 3_000)))
    }
    private fun showNextPermission() {
        if (state.permission != null || state.touchActive || !state.connected) return
        val next = incomingPermissions.firstOrNull { it.expiresAt > now() && it.request.id !in retiredPermissions } ?: return
        publish(state.copy(permission = next))
        onPermissionReady()
    }
    private fun showNextBanner() {
        if (state.banner != null || state.touchActive || state.permission != null || !state.connected) return
        while (banners.isNotEmpty()) {
            val next = banners.removeFirst()
            if (next.expiresAt <= now()) continue
            bannerShown = now()
            bannerRemaining = next.notice.durationSeconds * 1_000L
            publish(state.copy(banner = next))
            return
        }
    }
    private fun publish(next: LocalIntegrationsState) { state = next; onChanged(next) }
}
