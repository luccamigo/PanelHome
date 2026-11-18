package com.panelos.panelhome.ui.monitoring

import com.panelos.panelhome.ui.components.METRIC_UNAVAILABLE
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

internal fun rateText(value: Double?): String = when {
    value == null || !value.isFinite() -> METRIC_UNAVAILABLE
    value >= 1_000_000 -> String.format(Locale.getDefault(), "%.1f MB/s", value / 1_000_000)
    else -> String.format(Locale.getDefault(), "%.1f KB/s", value / 1_000)
}

internal fun memoryText(value: Long?): String = when {
    value == null -> METRIC_UNAVAILABLE
    value >= 1_000_000_000 -> String.format(Locale.getDefault(), "%.1f GB", value / 1_000_000_000.0)
    else -> String.format(Locale.getDefault(), "%.0f MB", value / 1_000_000.0)
}

internal fun clockText(unixMs: Long, seconds: Boolean = false): String =
    SimpleDateFormat(if (seconds) "HH:mm:ss" else "HH:mm", Locale.getDefault()).format(Date(unixMs))

internal fun ageText(milliseconds: Long): String {
    val seconds = (milliseconds.coerceAtLeast(0) / 1_000)
    return if (seconds < 3_600) "${seconds / 60}:${(seconds % 60).toString().padStart(2, '0')}"
    else "${seconds / 3_600} h ${(seconds % 3_600) / 60} min"
}
