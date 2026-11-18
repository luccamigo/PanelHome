package com.panelos.panelhome.ui.components

import java.util.Locale

internal const val METRIC_UNAVAILABLE = "—"

internal fun formatTemperatureCelsius(
    value: Double?,
    locale: Locale = Locale.getDefault(),
): String = formatFinite(value, locale, "%.1f °C")

internal fun formatFrequencyGhz(
    value: Double?,
    locale: Locale = Locale.getDefault(),
): String = formatFinite(value, locale, "%.2f GHz")

internal fun formatUsagePercent(
    value: Double?,
    locale: Locale = Locale.getDefault(),
): String = formatFinite(value, locale, "%.1f%%")

internal fun formatMemoryUsage(
    usedGb: Double?,
    totalGb: Double?,
    locale: Locale = Locale.getDefault(),
): String {
    if (!usedGb.isAvailable() && !totalGb.isAvailable()) return METRIC_UNAVAILABLE
    val used = formatNumberOrUnavailable(usedGb, locale)
    val total = formatNumberOrUnavailable(totalGb, locale)
    return "$used / $total GB"
}

private fun formatFinite(value: Double?, locale: Locale, format: String): String =
    if (value.isAvailable()) String.format(locale, format, value) else METRIC_UNAVAILABLE

private fun formatNumberOrUnavailable(value: Double?, locale: Locale): String =
    if (value.isAvailable()) String.format(locale, "%.1f", value) else METRIC_UNAVAILABLE

private fun Double?.isAvailable(): Boolean = this != null && isFinite()
