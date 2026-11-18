package com.panelos.panelhome.ui.ambient

import com.panelos.panelhome.domain.model.MediaProgress
import com.panelos.panelhome.domain.model.MediaState
import com.panelos.panelhome.domain.model.PanelData

internal const val AMBIENT_WINDOW_BRIGHTNESS = 0.35f
internal const val AMBIENT_BURN_IN_INTERVAL_MILLIS = 4 * 60 * 1_000L

internal data class AmbientPresentation(
    val media: AmbientMediaPresentation?,
    val metrics: AmbientMetricsPresentation,
)

internal data class AmbientMediaPresentation(
    val title: String,
    val artist: String,
    val progress: Float?,
)

internal data class AmbientMetricsPresentation(
    val cpuTemperatureCelsius: Double?,
    val gpuTemperatureCelsius: Double?,
    val ramUsagePercent: Double?,
)

internal data class AmbientBurnInOffset(
    val xDp: Int,
    val yDp: Int,
)

internal val AMBIENT_BURN_IN_OFFSETS = listOf(
    AmbientBurnInOffset(xDp = 0, yDp = 0),
    AmbientBurnInOffset(xDp = -4, yDp = -3),
    AmbientBurnInOffset(xDp = 3, yDp = -4),
    AmbientBurnInOffset(xDp = 5, yDp = 2),
    AmbientBurnInOffset(xDp = -3, yDp = 4),
)

internal fun ambientPresentation(data: PanelData): AmbientPresentation = AmbientPresentation(
    media = ambientMediaPresentation(data.media),
    metrics = AmbientMetricsPresentation(
        cpuTemperatureCelsius = data.cpu.temperatureCelsius.finiteOrNull(),
        gpuTemperatureCelsius = data.gpu.temperatureCelsius.finiteOrNull(),
        ramUsagePercent = data.ram.usagePercent.finiteOrNull(),
    ),
)

internal fun ambientMediaPresentation(media: MediaState): AmbientMediaPresentation? {
    val track = media.track ?: return null
    if (!media.available) return null

    return AmbientMediaPresentation(
        title = track.title.trim(),
        artist = track.artist.trim(),
        progress = normalizedAmbientProgress(media.progress),
    )
}

internal fun normalizedAmbientProgress(progress: MediaProgress): Float? {
    val durationMs = progress.durationMs
    if (durationMs <= 0L) return null

    val positionMs = progress.positionMs.coerceIn(0L, durationMs)
    return positionMs.toFloat() / durationMs.toFloat()
}

internal fun ambientBurnInOffsetForStep(step: Int): AmbientBurnInOffset =
    AMBIENT_BURN_IN_OFFSETS[Math.floorMod(step, AMBIENT_BURN_IN_OFFSETS.size)]

private fun Double?.finiteOrNull(): Double? = this?.takeIf(Double::isFinite)
