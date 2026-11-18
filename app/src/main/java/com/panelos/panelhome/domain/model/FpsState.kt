package com.panelos.panelhome.domain.model

import kotlin.math.ceil

/**
 * Frames per second of the game or app in front on the PC.
 *
 * The source is still being studied, so the state is born [Unavailable] and
 * nothing in the app produces the other two yet: the UI is ready for them so
 * it does not have to change when the data arrives.
 */
sealed interface FpsState {
    /** The host does not offer FPS (older PanelHost, no capability). The card does not exist. */
    data object Unavailable : FpsState

    /** The host offers FPS but nothing is being measured (no game open). The card stays, empty. */
    data object NoApp : FpsState

    /** An app is being measured. [series] is oldest first; a null sample is a gap. */
    data class Measuring(
        val appName: String?,
        val current: Int,
        val series: List<Float?> = emptyList(),
    ) : FpsState
}

/** min / average / max of a series, computed on the tablet; null when no sample is real. */
data class FpsSummary(val minimum: Int, val average: Int, val maximum: Int)

fun fpsSummary(series: List<Float?>): FpsSummary? {
    val values = series.filterNotNull().filter { it.isFinite() && it >= 0f }
    if (values.isEmpty()) return null
    return FpsSummary(
        minimum = values.min().toInt(),
        average = values.average().toInt(),
        maximum = values.max().toInt(),
    )
}

/** Chart ceiling: the next multiple of 30 above the window's maximum (at least 60). */
fun fpsChartCeiling(series: List<Float?>): Float {
    val maximum = series.filterNotNull().filter { it.isFinite() }.maxOrNull() ?: return 60f
    return (ceil((maximum + 0.001f) / 30f) * 30f).coerceAtLeast(60f)
}
