package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.*

data class MonitoringPreferences(
    val computerPage: Boolean = true,
    val processesPage: Boolean = true,
    val windowMinutes: Int = 5,
    val sort: ProcessSort = ProcessSort.CPU,
    val grouped: Boolean = false,
    val noticesPage: Boolean = true,
    val noticesOnDesktop: Boolean = true,
    val permissionSound: Boolean = false,
)

data class TelemetrySample(
    val at: Long,
    val unixMs: Long,
    val cpu: Double? = null,
    val memory: Double? = null,
    val gpu: Double? = null,
    val gap: Boolean = false,
)

data class NetworkSample(val at: Long, val unixMs: Long, val rates: NetworkRatesMessage?)
enum class TerminationStage { SENDING, FINISHED }
data class TerminationUi(
    val requestId: String,
    val key: String,
    val name: String,
    val pid: Int,
    val stage: TerminationStage = TerminationStage.SENDING,
    val sentAt: Long = 0,
    val message: String? = null,
    val failed: Boolean = false,
    val clearAt: Long? = null,
)
data class ProcessRow(val key: String, val item: ProcessItem, val ended: Boolean = false)
data class HistoryGap(val start: Long, val end: Long? = null)

data class MonitoringState(
    val computerAvailable: Boolean = false,
    val processesAvailable: Boolean = false,
    val preferences: MonitoringPreferences = MonitoringPreferences(),
    val telemetry: List<TelemetrySample> = emptyList(),
    val network: List<NetworkSample> = emptyList(),
    val gaps: List<HistoryGap> = emptyList(),
    val processRows: List<ProcessRow> = emptyList(),
    val receivedProcesses: Boolean = false,
    val processSampleUnixMs: Long = 0,
    val processError: Boolean = false,
    val terminateEnabled: Boolean = false,
    val termination: TerminationUi? = null,
    val telemetryError: Boolean = false,
    val networkError: Boolean = false,
    val orderPaused: Boolean = false,
    val now: Long = 0,
    val connected: Boolean = false,
    val hostName: String? = null,
    val lastTelemetryAt: Long? = null,
    val lastNetworkAt: Long? = null,
) {
    /**
     * Only a stable, ungrouped, readable row of the current user's own process can be offered.
     * The PC still revalidates identity, owner and protection before it ends anything.
     */
    fun canTerminate(row: ProcessRow): Boolean {
        val item = row.item
        return connected && processesAvailable && terminateEnabled && !preferences.grouped &&
            !row.ended && item.readable && item.owner == "current_user" && !item.isProtected &&
            (item.pid ?: 0) > 0 && (item.startTimeUtcTicks ?: 0L) > 0 &&
            termination?.stage != TerminationStage.SENDING
    }

    // Freeze the chart on disconnect. On reconnect the time axis exposes the gap.
    val chartNow: Long get() = if (connected) now else
        maxOf(lastTelemetryAt ?: now, lastNetworkAt ?: lastTelemetryAt ?: now)

    fun disconnected(at: Long, unixMs: Long = System.currentTimeMillis()): MonitoringState = copy(
        connected = false, orderPaused = false,
        gaps = if (connected && computerAvailable) (gaps + HistoryGap(at)).takeLast(902) else gaps,
        telemetry = if (connected && computerAvailable)
            (telemetry + TelemetrySample(at, unixMs, gap = true)).takeLast(902) else telemetry,
        network = if (connected && computerAvailable)
            (network + NetworkSample(at, unixMs, null)).takeLast(902) else network,
    )
}
