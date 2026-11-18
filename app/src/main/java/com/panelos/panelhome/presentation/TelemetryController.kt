package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.model.FpsState
import kotlin.math.roundToInt
import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.GpuMetrics
import com.panelos.panelhome.domain.model.RamMetrics
import com.panelos.panelhome.domain.model.StorageMetrics
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

internal data class TelemetryState(
    val fps: FpsState = FpsState.Unavailable,
    val cpu: CpuMetrics = CpuMetrics(),
    val gpu: GpuMetrics = GpuMetrics(),
    val ram: RamMetrics = RamMetrics(),
    val storage: List<StorageMetrics> = emptyList(),
)

/** Maps wire telemetry to nullable domain metrics without inventing zeroes. */
internal class TelemetryController(
    private val onStateChanged: (TelemetryState) -> Unit = {},
) {
    private val _state = MutableStateFlow(TelemetryState())
    val state: StateFlow<TelemetryState> = _state.asStateFlow()

    fun apply(message: TelemetryMessage) {
        val sample = message.fps
        val rate = sample?.presentsPerSecond?.takeIf { it.isFinite() && it > 0 && it <= 10_000 }
        val previous = (_state.value.fps as? FpsState.Measuring)
        val fps = when {
            sample?.state == "no_app" -> FpsState.NoApp
            sample?.state == "measuring" && rate != null -> {
                val history = if (previous != null && previous.appName == sample.appName) previous.series else emptyList()
                FpsState.Measuring(sample.appName, rate.roundToInt(), (history + rate.toFloat()).takeLast(900))
            }
            else -> FpsState.Unavailable
        }
        update(
            TelemetryState(
                fps = fps,
                cpu = CpuMetrics(
                    name = message.cpu.name?.takeIf(String::isNotBlank),
                    temperatureCelsius = message.cpu.temperatureC?.takeIf { it.isFinite() && it > 0 && it < 125 },
                    usagePercent = message.cpu.usagePercent,
                    frequencyGhz = message.cpu.clockGhz,
                    powerWatts = message.cpu.powerW,
                ),
                gpu = GpuMetrics(
                    name = message.gpu.name?.takeIf(String::isNotBlank),
                    temperatureCelsius = message.gpu.temperatureC,
                    usagePercent = message.gpu.usagePercent,
                    frequencyGhz = message.gpu.clockGhz,
                    memoryUsagePercent = message.gpu.memoryUsagePercent,
                    memoryUsedGb = message.gpu.memoryUsedGb,
                    memoryTotalGb = message.gpu.memoryTotalGb,
                    powerWatts = message.gpu.powerW,
                ),
                ram = RamMetrics(
                    usedGb = message.memory.usedGb,
                    totalGb = message.memory.totalGb,
                    usagePercent = message.memory.usagePercent,
                ),
                storage = message.storage.map { drive ->
                    StorageMetrics(
                        name = drive.name?.takeIf(String::isNotBlank),
                        temperatureCelsius = drive.temperatureC,
                        usagePercent = drive.usagePercent,
                    )
                },
            ),
        )
    }

    fun reset() = update(TelemetryState())

    private fun update(value: TelemetryState) {
        _state.value = value
        onStateChanged(value)
    }
}
