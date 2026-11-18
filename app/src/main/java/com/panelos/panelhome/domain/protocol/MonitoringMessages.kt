package com.panelos.panelhome.domain.protocol

enum class ProcessSort(val wireValue: String) {
    CPU("cpu"), MEMORY("memory");

    companion object {
        fun fromWire(value: String): ProcessSort = entries.firstOrNull { it.wireValue == value }
            ?: throw PanelProtocolException("Invalid process sort")
    }
}

data class NetworkAdapterRate(
    val id: String,
    val name: String,
    val downloadBytesPerSecond: Double? = null,
    val uploadBytesPerSecond: Double? = null,
)

data class NetworkRatesMessage(
    val sampledAtUnixMs: Long,
    val downloadBytesPerSecond: Double? = null,
    val uploadBytesPerSecond: Double? = null,
    val adapters: List<NetworkAdapterRate> = emptyList(),
) : PanelMessage {
    override val type = PanelMessageType.NETWORK_RATES
}

data class ProcessItem(
    val name: String,
    val pid: Int? = null,
    val startTimeUtcTicks: Long? = null,
    val cpuPercent: Double? = null,
    val memoryBytes: Long? = null,
    val owner: String = "unknown",
    val isProtected: Boolean = true,
    val count: Int = 1,
) {
    val readable: Boolean get() = cpuPercent != null || memoryBytes != null
}

data class ProcessesMessage(
    val sampledAtUnixMs: Long,
    val sort: ProcessSort = ProcessSort.CPU,
    val grouped: Boolean = false,
    val terminateEnabled: Boolean = false,
    val items: List<ProcessItem> = emptyList(),
) : PanelMessage {
    override val type = PanelMessageType.PROCESSES
}

data class ProcessTerminateResultMessage(
    val requestId: String?,
    val pid: Int?,
    val result: String,
) : PanelMessage {
    override val type = PanelMessageType.PROCESS_TERMINATE_RESULT
    companion object {
        val RESULTS = setOf("disabled", "invalid_request", "gone", "denied", "protected", "terminated")
    }
}

/** Asks the PC to end one process. The PC revalidates identity, owner and protection. */
data class ProcessTerminateCommand(
    val requestId: String,
    val pid: Int,
    val startTimeUtcTicks: Long,
) : PanelCommand {
    override val wireValue = WIRE_VALUE
    companion object { const val WIRE_VALUE = "processes.terminate" }
}

/** Selecting the sample is the only other process command. */
data class ProcessesOptionsCommand(
    val sort: ProcessSort,
    val grouped: Boolean,
) : PanelCommand {
    override val wireValue = WIRE_VALUE
    companion object { const val WIRE_VALUE = "processes.options" }
}
