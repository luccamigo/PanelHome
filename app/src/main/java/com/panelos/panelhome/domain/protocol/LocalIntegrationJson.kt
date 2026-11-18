package com.panelos.panelhome.domain.protocol

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/** Text remains text; no markup, URLs or executable actions are interpreted. */
internal object LocalIntegrationJson {
    fun encode(json: JSONObject, message: LocalNoticesMessage) {
        json.put("sampledAtUnixMs", message.sampledAtUnixMs).put("enabled", message.enabled)
            .put("notices", JSONArray().apply { message.notices.forEach { n -> put(JSONObject()
                .put("id", n.id).put("title", n.title).put("text", n.text).put("level", n.level)
                .nullable("key", n.key).nullable("page", n.page).put("durationSeconds", n.durationSeconds)
                .put("receivedAtUnixMs", n.receivedAtUnixMs).put("expiresAtUnixMs", n.expiresAtUnixMs)) } })
            .put("values", JSONArray().apply { message.values.forEach { v -> put(JSONObject()
                .put("name", v.name).put("value", v.value).nullable("unit", v.unit).put("level", v.level)
                .put("updatedAtUnixMs", v.updatedAtUnixMs).nullable("expiresAtUnixMs", v.expiresAtUnixMs)) } })
    }
    fun encode(json: JSONObject, message: ClaudePermissionsMessage) {
        json.put("sampledAtUnixMs", message.sampledAtUnixMs).put("requests", JSONArray().apply {
            message.requests.forEach { r -> put(JSONObject().put("id", r.id).put("project", r.project)
                .put("tool", r.tool).put("head", r.head).put("tail", r.tail).put("omitted", r.omitted)
                .put("expiresAtUnixMs", r.expiresAtUnixMs).put("contentHidden", r.contentHidden)) }
        })
    }
    fun notices(json: JSONObject) = LocalNoticesMessage(
        sampledAtUnixMs = json.integer("sampledAtUnixMs"), enabled = json.boolean("enabled"),
        notices = json.objects("notices", 20).map { n -> LocalNotice(
            n.text("id", 128), n.text("title", 80), n.text("text", 280), n.level(),
            n.optionalText("key", 40), n.optionalText("page", 40), n.integer("durationSeconds").also {
                if (it !in 3L..30L) throw JSONException("Invalid notice duration")
            }.toInt(), n.integer("receivedAtUnixMs"), n.integer("expiresAtUnixMs")) },
        values = json.objects("values", 12).map { v -> LocalPublishedValue(
            v.text("name", 40), v.text("value", 140), v.optionalText("unit", 16), v.level(),
            v.integer("updatedAtUnixMs"), if (v.isNull("expiresAtUnixMs") || !v.has("expiresAtUnixMs")) null else v.integer("expiresAtUnixMs")) },
    )
    fun permissions(json: JSONObject) = ClaudePermissionsMessage(json.integer("sampledAtUnixMs"),
        json.objects("requests", 20).map { r -> ClaudePermissionRequest(
            r.text("id", 128), r.text("project", 80), r.text("tool", 80), r.text("head", 360),
            r.text("tail", 180), r.integer("omitted").also {
                if (it > Int.MAX_VALUE) throw JSONException("Invalid omitted count")
            }.toInt(), r.integer("expiresAtUnixMs"),
            // Additive field: absent means false; present must be a real boolean.
            contentHidden = if (r.has("contentHidden")) r.boolean("contentHidden") else false) })

    fun plain(text: String, multiline: Boolean = false): String = buildString {
        var index = 0
        while (index < text.length) {
            val code = text.codePointAt(index)
            if (code == 10 && multiline || Character.getType(code) !in setOf(Character.CONTROL.toInt(), Character.FORMAT.toInt(),
                    Character.SURROGATE.toInt(), Character.LINE_SEPARATOR.toInt(), Character.PARAGRAPH_SEPARATOR.toInt())) appendCodePoint(code)
            index += Character.charCount(code)
        }
    }
    private fun JSONObject.text(key: String, maximum: Int): String {
        val raw = get(key) as? String ?: throw JSONException("$key must be text")
        if (raw.codePointCount(0, raw.length) > maximum) throw JSONException("$key too long")
        return plain(raw, key in setOf("text", "head", "tail"))
    }
    private fun JSONObject.optionalText(key: String, maximum: Int): String? = if (!has(key) || isNull(key)) null else text(key, maximum)
    private fun JSONObject.level() = text("level", 7).also {
        if (it !in setOf("info", "success", "warning", "error")) throw JSONException("Invalid level")
    }
    private fun JSONObject.objects(key: String, maximum: Int): List<JSONObject> {
        val array = getJSONArray(key)
        if (array.length() > maximum) throw JSONException("$key too long")
        return (0 until array.length()).map { array.getJSONObject(it) }
    }
    private fun JSONObject.integer(key: String): Long {
        val raw = get(key)
        if (raw !is Int && raw !is Long) throw JSONException("$key must be an integer")
        return (raw as Number).toLong().also { if (it < 0) throw JSONException("$key must be nonnegative") }
    }
    private fun JSONObject.boolean(key: String) = get(key) as? Boolean ?: throw JSONException("$key must be boolean")
    private fun JSONObject.nullable(key: String, value: Any?): JSONObject = put(key, value ?: JSONObject.NULL)
}
