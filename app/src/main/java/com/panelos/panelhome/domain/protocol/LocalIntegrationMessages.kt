package com.panelos.panelhome.domain.protocol

data class LocalNotice(
    val id: String, val title: String, val text: String, val level: String,
    val key: String?, val page: String?, val durationSeconds: Int,
    val receivedAtUnixMs: Long, val expiresAtUnixMs: Long,
)
data class LocalPublishedValue(
    val name: String, val value: String, val unit: String?, val level: String,
    val updatedAtUnixMs: Long, val expiresAtUnixMs: Long?,
)
data class LocalNoticesMessage(
    val sampledAtUnixMs: Long, val enabled: Boolean,
    val notices: List<LocalNotice>, val values: List<LocalPublishedValue>,
) : PanelMessage { override val type = PanelMessageType.LOCAL_NOTICES }

data class ClaudePermissionRequest(
    val id: String, val project: String, val tool: String,
    val head: String, val tail: String, val omitted: Int, val expiresAtUnixMs: Long,
    /** Edit/Write/MultiEdit: the summary shows only the path, never the content of the edit. */
    val contentHidden: Boolean = false,
) {
    val summaryIncomplete: Boolean get() = omitted > 0 || contentHidden
}
data class ClaudePermissionsMessage(val sampledAtUnixMs: Long, val requests: List<ClaudePermissionRequest>) : PanelMessage {
    override val type = PanelMessageType.CLAUDE_PERMISSIONS
}
data class ClaudeShownCommand(val requestId: String) : PanelCommand {
    override val wireValue = WIRE_VALUE
    companion object { const val WIRE_VALUE = "claude.shown" }
}
data class ClaudeRespondCommand(val requestId: String, val decision: String) : PanelCommand {
    override val wireValue = WIRE_VALUE
    companion object { const val WIRE_VALUE = "claude.respond" }
}
