package com.panelos.panelhome.ui.monitoring

import com.panelos.panelhome.presentation.MonitoringState
import kotlin.math.floor
import kotlin.math.log10
import kotlin.math.pow

internal enum class ComputerResource(val label: String) { CPU("CPU"), MEMORY("Memória"), GPU("GPU"), NETWORK("Rede") }
internal data class ChartPoint(val at: Long, val unixMs: Long, val value: Double?, val secondary: Double? = null)
internal data class ChartSummary(val average: Double?, val minimum: Double?, val maximum: Double?, val maximumAt: Long?)

internal fun chartPoints(state: MonitoringState, resource: ComputerResource, adapterId: String?): List<ChartPoint> {
    val cutoff = state.chartNow - state.preferences.windowMinutes * 60_000L
    return if (resource == ComputerResource.NETWORK) state.network.filter { it.at in cutoff..state.chartNow }.map { sample ->
        val rates = sample.rates
        val adapter = rates?.adapters?.find { it.id == adapterId }
        ChartPoint(sample.at, sample.unixMs,
            if (adapterId == null) rates?.downloadBytesPerSecond else adapter?.downloadBytesPerSecond,
            if (adapterId == null) rates?.uploadBytesPerSecond else adapter?.uploadBytesPerSecond)
    } else state.telemetry.filter { it.at in cutoff..state.chartNow }.map {
        ChartPoint(it.at, it.unixMs, when (resource) {
            ComputerResource.CPU -> it.cpu
            ComputerResource.MEMORY -> it.memory
            ComputerResource.GPU -> it.gpu
            ComputerResource.NETWORK -> null
        })
    }
}

internal fun summarize(points: List<ChartPoint>): ChartSummary {
    val valid = points.filter { it.value != null && it.value.isFinite() }
    val maximum = valid.maxByOrNull { it.value!! }
    return ChartSummary(valid.takeIf { it.isNotEmpty() }?.map { it.value!! }?.average(),
        valid.minOfOrNull { it.value!! }, maximum?.value, maximum?.at)
}

/** Shared scale for download and upload, rounded to 1, 2, 5 × 10^n. */
internal fun networkCeiling(points: List<ChartPoint>): Double {
    val maximum = points.flatMap { listOfNotNull(it.value, it.secondary) }.maxOrNull() ?: 0.0
    if (maximum <= 0) return 1_000.0
    val magnitude = 10.0.pow(floor(log10(maximum)))
    return (listOf(1.0, 2.0, 5.0, 10.0).first { maximum <= it * magnitude }) * magnitude
}

/** Each pixel preserves the extrema, splitting both absent values and time gaps. */
internal fun chartEnvelope(points: List<ChartPoint>, width: Int, start: Long, end: Long): List<List<ChartPoint>> {
    if (width <= 0 || end <= start) return emptyList()
    val result = mutableListOf<List<ChartPoint>>()
    var segment = mutableListOf<ChartPoint>()
    var bucket = mutableListOf<ChartPoint>()
    var bucketX = Int.MIN_VALUE
    var previousAt: Long? = null
    fun flushBucket() {
        if (bucket.isEmpty()) return
        val minimum = bucket.minBy { it.value!! }
        val maximum = bucket.maxBy { it.value!! }
        segment.addAll(listOf(minimum, maximum).distinct().sortedBy { it.at })
        bucket.clear()
    }
    fun flushSegment() {
        flushBucket()
        if (segment.isNotEmpty()) result.add(segment)
        segment = mutableListOf()
        bucketX = Int.MIN_VALUE
    }
    points.forEach { point ->
        if (point.value == null || !point.value.isFinite() || previousAt?.let { point.at - it > 2_500 } == true) {
            flushSegment()
        }
        if (point.value != null && point.value.isFinite()) {
            val x = floor((point.at - start).toDouble() / (end - start) * width).toInt()
            if (x != bucketX) flushBucket()
            bucketX = x
            bucket.add(point)
        }
        previousAt = point.at
    }
    flushSegment()
    return result
}

internal fun selectedPoint(points: List<ChartPoint>, at: Long): ChartPoint? =
    points.minByOrNull { kotlin.math.abs(it.at - at) }?.takeIf { kotlin.math.abs(it.at - at) <= 1_250 }
