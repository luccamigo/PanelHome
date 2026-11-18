package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*

/** Service-owned, bounded history. Only preferences may be written to disk. */
internal class MonitoringController(
    preferences: MonitoringPreferences = MonitoringPreferences(),
    initialState: MonitoringState? = null,
    private val now: () -> Long = { System.nanoTime() / 1_000_000 },
    private val unixNow: () -> Long = System::currentTimeMillis,
    private val send: (PanelCommand) -> Boolean = { false },
    private val newRequestId: () -> String = ::randomRequestId,
    private val onChanged: (MonitoringState) -> Unit = {},
) {
    var state = (initialState ?: MonitoringState()).copy(preferences = preferences)
        private set
    private var touching = false
    private var resumeAt = Long.MIN_VALUE
    private var lastProcessesAt: Long? = null
    private var awaitingProcessesSince: Long? = null
    private var latestRows = emptyList<ProcessRow>()
    private var ranks = emptyMap<String, Pair<Int, Int>>()

    fun onConnected(features: Set<String>, hostName: String? = null) {
        if (state.hostName != null && hostName != null && state.hostName != hostName) {
            state = state.copy(telemetry = emptyList(), network = emptyList(), gaps = emptyList())
        }
        touching = false
        resumeAt = Long.MIN_VALUE
        lastProcessesAt = null
        awaitingProcessesSince = now()
        latestRows = emptyList()
        ranks = emptyMap()
        publish(state.copy(
            computerAvailable = PanelProtocol.FEATURE_COMPUTER_STATS in features,
            processesAvailable = PanelProtocol.FEATURE_PROCESSES in features,
            connected = true, now = now(), receivedProcesses = false,
            hostName = hostName ?: state.hostName,
            processRows = emptyList(), processError = false, orderPaused = false,
            terminateEnabled = false, termination = null,
            lastTelemetryAt = null, lastNetworkAt = null,
            telemetryError = false, networkError = false,
            gaps = state.gaps.map { if (it.end == null) it.copy(end = now()) else it }
                .filter { (it.end ?: now()) >= now() - 900_000 }.takeLast(902),
        ))
    }

    fun onDisconnected() {
        touching = false
        resumeAt = Long.MIN_VALUE
        val pending = state.termination?.takeIf { it.stage == TerminationStage.SENDING }
        publish(state.disconnected(now(), unixNow()).copy(terminateEnabled = false,
            termination = pending?.finish("Sem conexão com o PC. Nada foi encerrado.", failed = true) ?: state.termination))
    }

    fun applyTelemetry(message: TelemetryMessage) {
        if (!state.connected || !state.computerAvailable) return
        val at = now()
        val sample = TelemetrySample(at, unixNow(), message.cpu.usagePercent,
            message.memory.usagePercent, message.gpu.usagePercent)
        publish(state.copy(telemetry = append(state.telemetry, sample) { it.at }, now = at, lastTelemetryAt = at,
            telemetryError = false))
    }

    fun applyNetwork(message: NetworkRatesMessage) {
        if (!state.connected || !state.computerAvailable) return
        val at = now()
        publish(state.copy(network = append(state.network, NetworkSample(at, unixNow(), message)) { it.at },
            now = at, lastNetworkAt = at, networkError = false))
    }

    fun onInvalidData(type: PanelMessageType) {
        if (!state.connected) return
        val at = now()
        when (type) {
            PanelMessageType.TELEMETRY -> if (state.computerAvailable) publish(state.copy(
                telemetry = append(state.telemetry, TelemetrySample(at, unixNow())) { it.at },
                telemetryError = true, now = at))
            PanelMessageType.NETWORK_RATES -> if (state.computerAvailable) publish(state.copy(
                network = append(state.network, NetworkSample(at, unixNow(), null)) { it.at },
                networkError = true, now = at))
            PanelMessageType.PROCESSES -> if (state.processesAvailable) publish(state.copy(processError = true))
            else -> Unit
        }
    }

    fun applyProcesses(message: ProcessesMessage) {
        if (!state.connected || !state.processesAvailable) return
        if (message.sort != state.preferences.sort || message.grouped != state.preferences.grouped) return
        lastProcessesAt = now()
        latestRows = message.items.map { item ->
            ProcessRow(if (message.grouped) "group:${item.name}" else
                "${item.pid}:${item.startTimeUtcTicks}:${item.name}", item)
        }.distinctBy { it.key }.sortedBy { !it.item.readable }
        ranks = latestRows.mapIndexed { index, row ->
            val previous = ranks[row.key]
            row.key to (index to if (previous?.first == index) previous.second + 1 else 1)
        }.toMap()
        publish(state.copy(processRows = reconcileRows(), receivedProcesses = true, processError = false,
            terminateEnabled = message.terminateEnabled,
            processSampleUnixMs = message.sampledAtUnixMs, now = now()))
    }

    fun requestTerminate(key: String) {
        val row = state.processRows.firstOrNull { it.key == key } ?: return
        if (!state.canTerminate(row)) return
        val requestId = newRequestId()
        val item = row.item
        val pid = item.pid ?: return
        val ticks = item.startTimeUtcTicks ?: return
        val at = now()
        if (!send(ProcessTerminateCommand(requestId, pid, ticks))) {
            publish(state.copy(termination = TerminationUi(requestId, key, item.name, pid, TerminationStage.FINISHED, at,
                "Sem conexão com o PC. Nada foi encerrado.", failed = true, clearAt = at + 4_000)))
            return
        }
        publish(state.copy(termination = TerminationUi(requestId, key, item.name, pid, sentAt = at)))
    }

    fun applyTerminateResult(message: ProcessTerminateResultMessage) {
        val pending = state.termination ?: return
        if (!state.connected || !state.processesAvailable || pending.stage != TerminationStage.SENDING ||
            message.requestId != pending.requestId) return
        val name = pending.name
        val text = when (message.result) {
            "terminated" -> "$name encerrado."
            "denied" -> "Não consegui encerrar $name: o Windows negou o acesso."
            "protected" -> "O PC recusou: $name é um processo protegido."
            "gone" -> "$name já tinha fechado."
            "disabled" -> "Encerrar pelo tablet está desligado no Control Center."
            else -> "O PC não aceitou o pedido para encerrar $name."
        }
        publish(state.copy(termination = pending.finish(text, failed = message.result != "terminated"),
            terminateEnabled = state.terminateEnabled && message.result != "disabled"))
    }

    fun dismissTermination() {
        if (state.termination?.stage == TerminationStage.FINISHED) publish(state.copy(termination = null))
    }

    fun setPreferences(preferences: MonitoringPreferences) {
        val next = preferences.copy(windowMinutes = preferences.windowMinutes.takeIf { it in listOf(1, 5, 15) } ?: 5)
        val selectionChanged = next.sort != state.preferences.sort || next.grouped != state.preferences.grouped
        if (selectionChanged) {
            latestRows = emptyList()
            ranks = emptyMap()
            lastProcessesAt = null
            awaitingProcessesSince = now()
        }
        publish(state.copy(preferences = next,
            processRows = if (selectionChanged) emptyList() else state.processRows,
            receivedProcesses = if (selectionChanged) false else state.receivedProcesses,
            processError = false))
    }

    fun setInteracting(active: Boolean) {
        touching = active
        if (!active) resumeAt = now() + 2_000
        publish(state.copy(orderPaused = active || now() < resumeAt))
    }

    fun tick() {
        val at = now()
        val paused = touching || at < resumeAt
        state.termination?.let { termination ->
            when {
                termination.stage == TerminationStage.SENDING && at - termination.sentAt >= 5_000 ->
                    publish(state.copy(termination = termination.finish(
                        "O PC não respondeu. Confira a lista antes de tentar de novo.", failed = true)))
                termination.stage == TerminationStage.FINISHED && (termination.clearAt ?: Long.MAX_VALUE) <= at ->
                    publish(state.copy(termination = null))
            }
        }
        publish(state.copy(now = if (state.connected) at else state.now, orderPaused = paused,
            processRows = if (state.orderPaused && !paused) reconcileRows(paused = false) else state.processRows,
            processError = state.processError || state.connected && state.processesAvailable &&
                ((lastProcessesAt ?: awaitingProcessesSince)?.let { at - it > 6_000 } ?: false)))
    }

    private fun reconcileRows(paused: Boolean = touching || now() < resumeAt): List<ProcessRow> {
        val byKey = latestRows.associateBy { it.key }
        if (paused) return state.processRows.map { byKey[it.key] ?: it.copy(ended = true) }
        val remaining = state.processRows.filter { it.key in byKey }.map { byKey.getValue(it.key) }
        if (remaining.isEmpty()) return latestRows
        val oldRanks = remaining.mapIndexed { i, row -> row.key to i }.toMap()
        return latestRows.sortedWith(compareBy<ProcessRow> { !it.item.readable }.thenBy { row ->
            val rank = ranks.getValue(row.key)
            if (rank.second >= 2) rank.first else oldRanks[row.key] ?: rank.first
        }.thenBy { oldRanks[it.key] ?: Int.MAX_VALUE })
    }

    private fun TerminationUi.finish(text: String, failed: Boolean) = copy(
        stage = TerminationStage.FINISHED, message = text, failed = failed, clearAt = now() + if (failed) 6_000 else 3_000)

    private fun <T> append(history: List<T>, point: T, time: (T) -> Long): List<T> =
        (history.filter { time(it) >= now() - 900_000 } + point).takeLast(902)

    private fun publish(next: MonitoringState) {
        state = next
        onChanged(next)
    }
}

private fun randomRequestId(): String {
    val bytes = ByteArray(16)
    java.security.SecureRandom().nextBytes(bytes)
    return bytes.joinToString("") { "%02x".format(it) }
}
