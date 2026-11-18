package com.panelos.panelhome.domain.protocol

import kotlin.io.encoding.Base64
import org.json.JSONException
import org.json.JSONArray
import org.json.JSONObject

object PanelProtocolJson {
    fun encode(message: PanelMessage): String {
        val json = JSONObject().put("type", message.type.wireValue)

        when (message) {
            is HelloMessage -> json
                .put("protocolVersion", message.protocolVersion)
                .put("deviceName", message.deviceName)
                .put("features", JSONArray(message.features))
                .apply {
                    message.buildIdentity?.let { put("buildIdentity", it) }
                    message.buildNumber?.let { put("buildNumber", it) }
                }

            is HelloAckMessage -> json
                .put("protocolVersion", message.protocolVersion)
                .put("hostName", message.hostName)
                .put("features", JSONArray(message.features))
                .apply {
                    message.buildIdentity?.let { put("buildIdentity", it) }
                    message.buildNumber?.let { put("buildNumber", it) }
                }

            is ErrorMessage -> json.put("code", message.code)
            is NetworkRatesMessage -> encodeNetwork(json, message)
            is ProcessesMessage -> encodeProcesses(json, message)
            is ProcessTerminateResultMessage -> json.putNullable("requestId", message.requestId)
                .putNullable("pid", message.pid).put("result", message.result)
            is LocalNoticesMessage -> LocalIntegrationJson.encode(json, message)
            is ClaudePermissionsMessage -> LocalIntegrationJson.encode(json, message)
            is TelemetryMessage -> json
                .put("cpu", encodeCpu(message.cpu))
                .put("gpu", encodeGpu(message.gpu))
                .put("memory", encodeMemory(message.memory))
                .put("storage", encodeStorage(message.storage))
                .apply { message.fps?.let { put("fps", encodeFps(it)) } }

            is MediaStateMessage -> json
                .put("available", message.available)
                .putNullable("mediaId", message.mediaId)
                .putNullable("sourceApp", message.sourceApp)
                .put("title", message.title)
                .put("artist", message.artist)
                .putNullable("album", message.album)
                .put("isPlaying", message.isPlaying)
                .put("positionMs", message.positionMs)
                .put("durationMs", message.durationMs)
                .put("canPlayPause", message.canPlayPause)
                .put("canPrevious", message.canPrevious)
                .put("canNext", message.canNext)

            is MediaArtworkMessage -> json
                .put("mediaId", message.mediaId)
                .put("available", message.available)
                .putNullable("mimeType", message.mimeType)
                .putNullable("dataBase64", message.dataBase64)

            is VolumeStateMessage -> json
                .put("available", message.available)
                .put("volumePercent", message.volumePercent)
                .put("isMuted", message.isMuted)

            is DesktopStateMessage -> json
                .put("state", message.status.toWireValue())
                .apply {
                    message.errorCode?.let { put("errorCode", it) }
                    message.generation?.let { put("generation", it) }
                }

            is CommandMessage -> json
                .put("command", message.command.wireValue)
                .apply {
                    (message.command as? SetVolumeCommand)?.let { command ->
                        put("value", command.value)
                    }
                    (message.command as? ProcessesOptionsCommand)?.let { command ->
                        put("sort", command.sort.wireValue)
                        put("grouped", command.grouped)
                    }
                    (message.command as? ProcessTerminateCommand)?.let { command ->
                        put("requestId", command.requestId)
                        put("pid", command.pid)
                        put("startTimeUtcTicks", command.startTimeUtcTicks)
                    }
                    (message.command as? ClaudeShownCommand)?.let { put("requestId", it.requestId) }
                    (message.command as? ClaudeRespondCommand)?.let { put("requestId", it.requestId); put("decision", it.decision) }
                    (message.command as? TouchCommand)?.let { command ->
                        put("pointerId", command.pointerId)
                        put("generation", command.generation)
                        command.x?.let { put("x", it) }
                        command.y?.let { put("y", it) }
                    }
                    (message.command as? StreamDeckInvokeCommand)?.let { command ->
                        put("buttonId", command.buttonId)
                        put("revision", command.revision)
                    }
                    (message.command as? StreamDeckIconsRequestCommand)?.let { command ->
                        put("iconIds", JSONArray(command.iconIds))
                    }
                    (message.command as? LoopMediaRequestCommand)?.let { command ->
                        put("mediaId", command.mediaId)
                        put("offset", command.offset)
                    }
                }

            is StreamDeckLayoutMessage -> json
                .put("revision", message.revision)
                .apply {
                    if (message.profileId != null) {
                        put(
                            "profile",
                            JSONObject()
                                .put("id", message.profileId)
                                .put("name", message.profileName ?: ""),
                        )
                    }
                    put("pages", encodeStreamDeckPages(message.pages))
                }

            is StreamDeckInvokeResultMessage -> json
                .put("buttonId", message.buttonId)
                .put("success", message.success)
                .apply {
                    message.errorCode?.let { put("errorCode", it) }
                    message.message?.let { put("message", it) }
                }

            is StreamDeckIconMessage -> json
                .put("iconId", message.iconId)
                .put("available", message.available)
                .apply {
                    message.mime?.let { put("mime", it) }
                    message.dataBase64?.let { put("data", it) }
                }

            is LoopMediaStateMessage -> json
                .put("available", message.available)
                .apply {
                    if (message.available) {
                        put("mediaId", message.mediaId)
                        put("kind", message.kind?.wireValue)
                        put("mime", message.kind?.mime)
                        put("sizeBytes", message.sizeBytes)
                    }
                }

            is LoopMediaChunkMessage -> json
                .put("mediaId", message.mediaId)
                .put("offset", message.offset)
                .apply {
                    if (message.error != null) {
                        put("error", message.error)
                    } else {
                        put("totalBytes", message.totalBytes)
                        put("data", message.dataBase64)
                        put("last", message.last)
                    }
                }

            PingMessage,
            PongMessage,
            -> Unit
        }

        return json.toString()
    }

    fun encodeLine(message: PanelMessage): String = "${encode(message)}\n"

    fun decode(line: String): PanelMessage {
        try {
            val json = JSONObject(line)
            return when (PanelMessageType.fromWireValue(json.getString("type"))) {
                PanelMessageType.HELLO -> HelloMessage(
                    protocolVersion = json.getInt("protocolVersion"),
                    deviceName = json.getString("deviceName").requireLength(
                        "deviceName",
                        PanelProtocolLimits.MAX_NAME_LENGTH,
                    ),
                    features = json.optJSONArray("features").toStringList(),
                    buildIdentity = json.optNullableString("buildIdentity"),
                    buildNumber = json.optNullableLong("buildNumber"),
                )

                PanelMessageType.HELLO_ACK -> HelloAckMessage(
                    protocolVersion = json.getInt("protocolVersion"),
                    hostName = json.getString("hostName").requireLength(
                        "hostName",
                        PanelProtocolLimits.MAX_NAME_LENGTH,
                    ),
                    features = json.optJSONArray("features").toStringList(),
                    buildIdentity = json.optNullableString("buildIdentity"),
                    buildNumber = json.optNullableLong("buildNumber"),
                )

                PanelMessageType.ERROR -> ErrorMessage(code = json.getString("code"))
                PanelMessageType.NETWORK_RATES -> decodeNetwork(json)
                PanelMessageType.PROCESSES -> decodeProcesses(json)
                PanelMessageType.PROCESS_TERMINATE_RESULT -> ProcessTerminateResultMessage(
                    requestId = json.optNullableString("requestId")?.also {
                        if (it.length != 32) throw JSONException("Invalid requestId")
                    },
                    pid = json.nullableNonNegativeLong("pid")?.also {
                        if (it > Int.MAX_VALUE) throw JSONException("Invalid pid")
                    }?.toInt(),
                    result = json.getString("result").also {
                        if (it !in ProcessTerminateResultMessage.RESULTS) throw JSONException("Invalid result")
                    },
                )
                PanelMessageType.LOCAL_NOTICES -> LocalIntegrationJson.notices(json)
                PanelMessageType.CLAUDE_PERMISSIONS -> LocalIntegrationJson.permissions(json)
                PanelMessageType.TELEMETRY -> TelemetryMessage(
                    cpu = json.optJSONObject("cpu")?.let(::decodeCpu) ?: CpuTelemetry(),
                    gpu = json.optJSONObject("gpu")?.let(::decodeGpu) ?: GpuTelemetry(),
                    memory = json.optJSONObject("memory")
                        ?.let(::decodeMemory)
                        ?: MemoryTelemetry(),
                    storage = decodeStorage(json.optJSONArray("storage")),
                    fps = json.optJSONObject("fps")?.let(::decodeFps),
                )

                PanelMessageType.MEDIA_STATE -> MediaStateMessage(
                    available = json.optBoolean("available", true),
                    mediaId = json.optNullableString("mediaId"),
                    sourceApp = json.optNullableString("sourceApp"),
                    title = json.optString("title", ""),
                    artist = json.optString("artist", ""),
                    album = json.optNullableString("album"),
                    isPlaying = json.optBoolean("isPlaying", false),
                    positionMs = json.optLong("positionMs", 0),
                    durationMs = json.optLong("durationMs", 0),
                    canPlayPause = json.optBoolean("canPlayPause", true),
                    canPrevious = json.optBoolean("canPrevious", true),
                    canNext = json.optBoolean("canNext", true),
                )

                PanelMessageType.MEDIA_ARTWORK -> MediaArtworkMessage(
                    mediaId = json.optString("mediaId", ""),
                    available = json.optBoolean("available", false),
                    mimeType = json.optNullableString("mimeType"),
                    dataBase64 = json.optNullableString("dataBase64"),
                )

                PanelMessageType.VOLUME_STATE -> VolumeStateMessage(
                    available = json.optBoolean("available", false),
                    volumePercent = json.optInt("volumePercent", 0),
                    isMuted = json.optBoolean("isMuted", false),
                )

                PanelMessageType.DESKTOP_STATE -> DesktopStateMessage(
                    status = desktopStatusFromWireValue(json.getString("state")),
                    errorCode = json.optNullableString("errorCode"),
                    generation = json.optNullableLong("generation"),
                )

                PanelMessageType.COMMAND -> json.getString("command").let { commandName ->
                    CommandMessage(
                        command = when (commandName) {
                            ClaudeShownCommand.WIRE_VALUE -> ClaudeShownCommand(json.getString("requestId").requireLength("requestId", 128))
                            ClaudeRespondCommand.WIRE_VALUE -> ClaudeRespondCommand(
                                json.getString("requestId").requireLength("requestId", 128),
                                json.getString("decision").also { if (it !in setOf("allow", "deny", "defer")) throw JSONException("Invalid decision") })
                            ProcessTerminateCommand.WIRE_VALUE -> ProcessTerminateCommand(
                                requestId = json.getString("requestId").also {
                                    if (it.length != 32) throw JSONException("Invalid requestId")
                                },
                                pid = json.getInt("pid").also { if (it <= 0) throw JSONException("Invalid pid") },
                                startTimeUtcTicks = json.getLong("startTimeUtcTicks").also {
                                    if (it <= 0) throw JSONException("Invalid start time")
                                },
                            )
                            ProcessesOptionsCommand.WIRE_VALUE -> ProcessesOptionsCommand(
                                sort = ProcessSort.fromWire(json.getString("sort")),
                                grouped = json.strictBoolean("grouped"),
                            )
                            StreamDeckInvokeCommand.WIRE_VALUE -> StreamDeckInvokeCommand(
                                buttonId = json.getString("buttonId").requireLength(
                                    "buttonId",
                                    PanelProtocolLimits.MAX_IDENTIFIER_LENGTH,
                                ),
                                revision = json.getString("revision").requireLength(
                                    "revision",
                                    PanelProtocolLimits.MAX_REVISION_LENGTH,
                                ),
                            )
                            StreamDeckIconsRequestCommand.WIRE_VALUE ->
                                StreamDeckIconsRequestCommand(
                                    json.getJSONArray("iconIds").toStringList(
                                        PanelProtocolLimits.MAX_ICON_REQUEST_COUNT,
                                    ),
                                )
                            LoopMediaRequestCommand.WIRE_VALUE -> LoopMediaRequestCommand(
                                mediaId = json.getString("mediaId").also {
                                    if (!LoopMediaLimits.isValidMediaId(it)) {
                                        throw JSONException("invalid mediaId")
                                    }
                                },
                                offset = json.getIntegerIn("offset", 0..Int.MAX_VALUE),
                            )
                            else -> TouchCommandAction.fromWireValue(commandName)?.let { action ->
                                TouchCommand(
                                    action = action,
                                    pointerId = json.getInt("pointerId"),
                                    x = json.optNullableDouble("x"),
                                    y = json.optNullableDouble("y"),
                                    generation = json.getLong("generation"),
                                )
                            } ?: PanelCommand.fromWireValue(
                                value = commandName,
                                commandValue = if (commandName == SetVolumeCommand.WIRE_VALUE) {
                                    json.optNullableInt("value")
                                } else {
                                    null
                                },
                            )
                        },
                    )
                }

                PanelMessageType.STREAM_DECK_LAYOUT -> json.optJSONObject("profile")
                    .let { profile ->
                        StreamDeckLayoutMessage(
                            revision = json.optString("revision", "").requireLength(
                                "revision",
                                PanelProtocolLimits.MAX_REVISION_LENGTH,
                            ),
                            profileId = profile?.optNullableString("id")?.requireLength(
                                "profileId",
                                PanelProtocolLimits.MAX_IDENTIFIER_LENGTH,
                            ),
                            profileName = profile?.optNullableString("name")?.requireLength(
                                "profileName",
                                PanelProtocolLimits.MAX_NAME_LENGTH,
                            ),
                            pages = decodeStreamDeckPages(json.optJSONArray("pages")),
                        )
                    }

                PanelMessageType.STREAM_DECK_INVOKE_RESULT -> StreamDeckInvokeResultMessage(
                    buttonId = json.optString("buttonId", ""),
                    success = json.optBoolean("success", false),
                    errorCode = json.optNullableString("errorCode"),
                    message = json.optNullableString("message"),
                )

                PanelMessageType.STREAM_DECK_ICON -> StreamDeckIconMessage(
                    iconId = json.optString("iconId", ""),
                    available = json.optBoolean("available", false),
                    mime = json.optNullableString("mime"),
                    dataBase64 = json.optNullableString("data"),
                )

                PanelMessageType.LOOP_MEDIA_STATE -> rejectingInvalid { decodeLoopMediaState(json) }
                PanelMessageType.LOOP_MEDIA_CHUNK -> rejectingInvalid { decodeLoopMediaChunk(json) }

                PanelMessageType.PING -> PingMessage
                PanelMessageType.PONG -> PongMessage
            }
        } catch (error: JSONException) {
            throw PanelProtocolException("Invalid protocol message", error)
        }
    }

    private fun encodeNetwork(json: JSONObject, message: NetworkRatesMessage) {
        json.put("sampledAtUnixMs", message.sampledAtUnixMs)
            .putNullable("downloadBytesPerSecond", message.downloadBytesPerSecond)
            .putNullable("uploadBytesPerSecond", message.uploadBytesPerSecond)
            .put("adapters", JSONArray().apply {
                message.adapters.forEach { rate -> put(JSONObject().put("id", rate.id).put("name", rate.name)
                    .putNullable("downloadBytesPerSecond", rate.downloadBytesPerSecond)
                    .putNullable("uploadBytesPerSecond", rate.uploadBytesPerSecond)) }
            })
    }

    private fun decodeNetwork(json: JSONObject): NetworkRatesMessage {
        val adapters = json.getJSONArray("adapters")
        if (adapters.length() > 128) throw JSONException("Too many adapters")
        return NetworkRatesMessage(
            sampledAtUnixMs = json.positiveLong("sampledAtUnixMs"),
            downloadBytesPerSecond = json.nonNegativeDouble("downloadBytesPerSecond"),
            uploadBytesPerSecond = json.nonNegativeDouble("uploadBytesPerSecond"),
            adapters = (0 until adapters.length()).map { i ->
                val adapter = adapters.getJSONObject(i)
                NetworkAdapterRate(
                    id = adapter.getString("id").requireLength("adapter.id", 256),
                    name = adapter.getString("name").requireLength("adapter.name", 256),
                    downloadBytesPerSecond = adapter.nonNegativeDouble("downloadBytesPerSecond"),
                    uploadBytesPerSecond = adapter.nonNegativeDouble("uploadBytesPerSecond"),
                )
            }.also { if (it.map { a -> a.id }.distinct().size != it.size) throw JSONException("Duplicate adapter") },
        )
    }

    private fun encodeProcesses(json: JSONObject, message: ProcessesMessage) {
        json.put("sampledAtUnixMs", message.sampledAtUnixMs).put("sort", message.sort.wireValue)
            .put("grouped", message.grouped).put("terminateEnabled", message.terminateEnabled)
            .put("items", JSONArray().apply {
                message.items.forEach { item -> put(JSONObject().put("name", item.name)
                    .putNullable("pid", item.pid).putNullable("startTimeUtcTicks", item.startTimeUtcTicks)
                    .putNullable("cpuPercent", item.cpuPercent).putNullable("memoryBytes", item.memoryBytes)
                    .put("owner", item.owner).put("protected", item.isProtected).put("count", item.count)) }
            })
    }

    private fun decodeProcesses(json: JSONObject): ProcessesMessage {
        val items = json.getJSONArray("items")
        if (items.length() > 15) throw JSONException("Too many processes")
        return ProcessesMessage(
            sampledAtUnixMs = json.positiveLong("sampledAtUnixMs"),
            sort = ProcessSort.fromWire(json.getString("sort")),
            grouped = json.strictBoolean("grouped"),
            terminateEnabled = json.strictBoolean("terminateEnabled"),
            items = (0 until items.length()).map { i ->
                val item = items.getJSONObject(i)
                ProcessItem(
                    name = item.getString("name").requireLength("process.name", 260),
                    pid = item.nullableNonNegativeLong("pid")?.also {
                        if (it > Int.MAX_VALUE) throw JSONException("Invalid pid")
                    }?.toInt(),
                    startTimeUtcTicks = item.nullableNonNegativeLong("startTimeUtcTicks"),
                    cpuPercent = item.nonNegativeDouble("cpuPercent")?.also {
                        if (it > 100) throw JSONException("Invalid cpuPercent")
                    },
                    memoryBytes = item.nullableNonNegativeLong("memoryBytes"),
                    owner = item.getString("owner").also {
                        if (it !in setOf("current_user", "other_user", "system", "unknown")) throw JSONException("Invalid owner")
                    },
                    isProtected = item.strictBoolean("protected"),
                    count = item.positiveLong("count").also {
                        if (it !in 1..Int.MAX_VALUE.toLong()) throw JSONException("Invalid count")
                    }.toInt(),
                )
            },
        )
    }

    private fun JSONObject.strictBoolean(name: String): Boolean =
        (get(name) as? Boolean) ?: throw JSONException("$name must be boolean")

    private fun JSONObject.nullableNonNegativeLong(name: String): Long? {
        if (!has(name) || isNull(name)) return null
        val value = get(name)
        if (value !is Int && value !is Long) throw JSONException("$name must be an integer")
        return (value as Number).toLong().also { if (it < 0) throw JSONException("$name must be nonnegative") }
    }

    private fun JSONObject.positiveLong(name: String): Long =
        nullableNonNegativeLong(name) ?: throw JSONException("Missing $name")

    private fun JSONObject.nonNegativeDouble(name: String): Double? = optFiniteDouble(name)?.also {
        if (it < 0) throw JSONException("$name must be nonnegative")
    }

    /** loop_media.* content errors drop the message; the session stays up. */
    private inline fun <T> rejectingInvalid(block: () -> T): T = try {
        block()
    } catch (error: JSONException) {
        throw PanelMessageRejectedException("Invalid loop_media message: ${error.message}", error)
    }

    private fun decodeLoopMediaState(json: JSONObject): LoopMediaStateMessage {
        if (!json.getBoolean("available")) return LoopMediaStateMessage(available = false)
        val mediaId = json.getString("mediaId")
        if (!LoopMediaLimits.isValidMediaId(mediaId)) throw JSONException("invalid mediaId")
        val kind = LoopMediaKind.fromWireValue(json.getString("kind"))
            ?: throw JSONException("unknown kind")
        if (json.getString("mime") != kind.mime) throw JSONException("mime does not match kind")
        val sizeBytes = json.getIntegerIn("sizeBytes", 1..LoopMediaLimits.MAX_MEDIA_BYTES)
        return LoopMediaStateMessage(true, mediaId, kind, sizeBytes)
    }

    private fun decodeLoopMediaChunk(json: JSONObject): LoopMediaChunkMessage {
        val mediaId = json.getString("mediaId")
        if (!LoopMediaLimits.isValidMediaId(mediaId)) throw JSONException("invalid mediaId")
        val offset = json.getIntegerIn("offset", 0..LoopMediaLimits.MAX_MEDIA_BYTES)
        json.optNullableString("error")?.let { error ->
            if (error.isBlank() || error.length > LoopMediaLimits.MAX_ERROR_LENGTH) {
                throw JSONException("invalid error")
            }
            if (json.has("data")) throw JSONException("error chunk carries data")
            return LoopMediaChunkMessage(mediaId, offset, error = error)
        }
        val totalBytes = json.getIntegerIn("totalBytes", 1..LoopMediaLimits.MAX_MEDIA_BYTES)
        val data = json.getString("data")
        // Checked before decoding so an oversized field never gets allocated.
        if (data.length > ((LoopMediaLimits.MAX_CHUNK_BYTES + 2) / 3) * 4) {
            throw JSONException("data is too large")
        }
        val size = runCatching { Base64.Default.decode(data).size }
            .getOrElse { throw JSONException("data is not base64") }
        if (size !in 1..LoopMediaLimits.MAX_CHUNK_BYTES) throw JSONException("invalid data size")
        val last = json.getBoolean("last")
        if (offset + size > totalBytes || last != (offset + size == totalBytes)) {
            throw JSONException("chunk does not fit totalBytes")
        }
        return LoopMediaChunkMessage(mediaId, offset, totalBytes, data, last)
    }

    private fun encodeFps(value: FpsTelemetry): JSONObject = JSONObject()
        .put("state", value.state)
        .putNullable("presentsPerSecond", value.presentsPerSecond)
        .putNullable("appName", value.appName)

    private fun decodeFps(json: JSONObject) = FpsTelemetry(
        state = json.optString("state", "unavailable"),
        presentsPerSecond = json.optFiniteDouble("presentsPerSecond"),
        appName = json.optNullableString("appName")?.take(80),
    )

    private fun encodeCpu(cpu: CpuTelemetry): JSONObject =
        JSONObject()
            .putNullable("name", cpu.name)
            .putNullable("temperatureC", cpu.temperatureC)
            .putNullable("usagePercent", cpu.usagePercent)
            .putNullable("clockGhz", cpu.clockGhz)
            .putNullable("powerW", cpu.powerW)

    private fun encodeGpu(gpu: GpuTelemetry): JSONObject =
        JSONObject()
            .putNullable("name", gpu.name)
            .putNullable("temperatureC", gpu.temperatureC)
            .putNullable("usagePercent", gpu.usagePercent)
            .putNullable("clockGhz", gpu.clockGhz)
            .putNullable("memoryUsagePercent", gpu.memoryUsagePercent)
            .putNullable("memoryUsedGb", gpu.memoryUsedGb)
            .putNullable("memoryTotalGb", gpu.memoryTotalGb)
            .putNullable("powerW", gpu.powerW)

    private fun encodeMemory(memory: MemoryTelemetry): JSONObject =
        JSONObject()
            .putNullable("usedGb", memory.usedGb)
            .putNullable("totalGb", memory.totalGb)
            .putNullable("usagePercent", memory.usagePercent)

    private fun encodeStorage(storage: List<StorageTelemetry>): JSONArray =
        JSONArray().apply {
            storage.forEach { drive ->
                put(
                    JSONObject()
                        .putNullable("name", drive.name)
                        .putNullable("temperatureC", drive.temperatureC)
                        .putNullable("usagePercent", drive.usagePercent),
                )
            }
        }

    private fun decodeCpu(json: JSONObject) = CpuTelemetry(
        name = json.optNullableString("name"),
        temperatureC = json.optFiniteDouble("temperatureC"),
        usagePercent = json.optFiniteDouble("usagePercent"),
        clockGhz = json.optFiniteDouble("clockGhz"),
        powerW = json.optFiniteDouble("powerW"),
    )

    private fun decodeGpu(json: JSONObject) = GpuTelemetry(
        name = json.optNullableString("name"),
        temperatureC = json.optFiniteDouble("temperatureC"),
        usagePercent = json.optFiniteDouble("usagePercent"),
        clockGhz = json.optFiniteDouble("clockGhz"),
        memoryUsagePercent = json.optFiniteDouble("memoryUsagePercent"),
        memoryUsedGb = json.optFiniteDouble("memoryUsedGb"),
        memoryTotalGb = json.optFiniteDouble("memoryTotalGb"),
        powerW = json.optFiniteDouble("powerW"),
    )

    private fun decodeMemory(json: JSONObject) = MemoryTelemetry(
        usedGb = json.optFiniteDouble("usedGb"),
        totalGb = json.optFiniteDouble("totalGb"),
        usagePercent = json.optFiniteDouble("usagePercent"),
    )

    private fun decodeStorage(json: JSONArray?): List<StorageTelemetry> {
        if (json == null) return emptyList()
        if (json.length() > PanelProtocolLimits.MAX_TELEMETRY_STORAGE_ITEMS) {
            throw JSONException("storage has too many items")
        }
        return buildList {
            for (index in 0 until json.length()) {
                val drive = json.optJSONObject(index) ?: continue
                add(
                    StorageTelemetry(
                        name = drive.optNullableString("name"),
                        temperatureC = drive.optFiniteDouble("temperatureC"),
                        usagePercent = drive.optFiniteDouble("usagePercent"),
                    ),
                )
            }
        }
    }

    private fun encodeStreamDeckPages(pages: List<StreamDeckPageMessage>): JSONArray =
        JSONArray().apply {
            pages.forEach { page ->
                put(
                    JSONObject()
                        .put("id", page.id)
                        .put("name", page.name)
                        .put("rows", page.rows)
                        .put("columns", page.columns)
                        .put("buttons", encodeStreamDeckButtons(page.buttons)),
                )
            }
        }

    private fun encodeStreamDeckButtons(
        buttons: List<StreamDeckButtonMessage>,
    ): JSONArray = JSONArray().apply {
        buttons.forEach { button ->
            put(
                JSONObject()
                    .put("id", button.id)
                    .put("row", button.row)
                    .put("column", button.column)
                    .put("label", button.label)
                    .put("enabled", button.enabled)
                    .apply {
                        button.iconId?.let { put("iconId", it) }
                        button.glyph?.let { put("glyph", it) }
                    },
            )
        }
    }

    private fun decodeStreamDeckPages(json: JSONArray?): List<StreamDeckPageMessage> {
        if (json == null) return emptyList()
        if (json.length() > PanelProtocolLimits.MAX_PAGES_PER_PROFILE) {
            throw JSONException("streamdeck layout has too many pages")
        }
        var totalButtons = 0
        val pages = buildList {
            for (index in 0 until json.length()) {
                val page = json.optJSONObject(index) ?: continue
                add(
                    StreamDeckPageMessage(
                        id = page.optString("id", "").requireLength(
                            "page.id",
                            PanelProtocolLimits.MAX_IDENTIFIER_LENGTH,
                        ),
                        name = page.optString("name", "").requireLength(
                            "page.name",
                            PanelProtocolLimits.MAX_NAME_LENGTH,
                        ),
                        rows = page.optInt("rows", 0).also { rows ->
                            if (rows !in 2..PanelProtocolLimits.MAX_ROWS) {
                                throw JSONException("invalid streamdeck rows")
                            }
                        },
                        columns = page.optInt("columns", 0).also { columns ->
                            if (columns !in 2..PanelProtocolLimits.MAX_COLUMNS) {
                                throw JSONException("invalid streamdeck columns")
                            }
                        },
                        buttons = decodeStreamDeckButtons(page.optJSONArray("buttons")).also {
                            totalButtons += it.size
                            if (totalButtons > PanelProtocolLimits.MAX_BUTTONS) {
                                throw JSONException("streamdeck layout has too many buttons")
                            }
                        },
                    ),
                )
            }
        }
        val iconCount = pages.asSequence()
            .flatMap { it.buttons.asSequence() }
            .mapNotNull { it.iconId }
            .distinct()
            .count()
        if (iconCount > PanelProtocolLimits.MAX_ICON_COUNT) {
            throw JSONException("streamdeck layout has too many icons")
        }
        return pages
    }

    private fun decodeStreamDeckButtons(
        json: JSONArray?,
    ): List<StreamDeckButtonMessage> {
        if (json == null) return emptyList()
        if (json.length() > PanelProtocolLimits.MAX_ROWS * PanelProtocolLimits.MAX_COLUMNS) {
            throw JSONException("streamdeck page has too many buttons")
        }
        return buildList {
            for (index in 0 until json.length()) {
                val button = json.optJSONObject(index) ?: continue
                add(
                    StreamDeckButtonMessage(
                        id = button.optString("id", "").requireLength(
                            "button.id",
                            PanelProtocolLimits.MAX_IDENTIFIER_LENGTH,
                        ),
                        row = button.optInt("row", -1),
                        column = button.optInt("column", -1),
                        label = button.optString("label", "").requireLength(
                            "button.label",
                            PanelProtocolLimits.MAX_LABEL_LENGTH,
                        ),
                        iconId = button.optNullableString("iconId"),
                        glyph = button.optNullableString("glyph"),
                        enabled = button.optBoolean("enabled", false),
                    ),
                )
            }
        }
    }

    private fun JSONObject.putNullable(name: String, value: Any?): JSONObject =
        put(name, value ?: JSONObject.NULL)

    private fun JSONObject.optNullableString(name: String): String? =
        if (has(name) && !isNull(name)) getString(name) else null

    private fun JSONObject.optFiniteDouble(name: String): Double? {
        if (!has(name) || isNull(name)) return null
        val value = when (val raw = opt(name)) {
            is Number -> raw.toDouble()
            else -> throw JSONException("$name must be a number")
        }
        if (!value.isFinite()) throw JSONException("$name must be finite")
        return value
    }

    private fun JSONObject.optNullableInt(name: String): Int? =
        if (has(name) && !isNull(name)) getInt(name) else null

    /** A JSON integer within [range]; getInt/getLong would truncate 1.5 or parse "5". */
    private fun JSONObject.getIntegerIn(name: String, range: IntRange): Int {
        val value = when (val raw = get(name)) {
            is Int -> raw.toLong()
            is Long -> raw
            else -> throw JSONException("$name must be an integer")
        }
        if (value !in range.first..range.last) throw JSONException("invalid $name")
        return value.toInt()
    }

    private fun JSONObject.optNullableLong(name: String): Long? =
        if (has(name) && !isNull(name)) getLong(name) else null

    private fun JSONObject.optNullableDouble(name: String): Double? =
        if (has(name) && !isNull(name)) {
            getDouble(name).also {
                if (!it.isFinite()) throw JSONException("$name must be finite")
            }
        } else {
            null
        }

    private fun JSONArray?.toStringList(maximumItems: Int = Int.MAX_VALUE): List<String> {
        if (this == null) return emptyList()
        if (length() > maximumItems) throw JSONException("array has too many items")
        return buildList {
            for (index in 0 until length()) {
                optString(index).takeIf(String::isNotBlank)?.let(::add)
            }
        }
    }

    private fun String.requireLength(name: String, maximumLength: Int): String {
        if (length > maximumLength) throw JSONException("$name is too long")
        return this
    }
}
