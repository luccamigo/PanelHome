package com.panelos.panelhome.ui.dashboard

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.FpsState
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.domain.model.VolumeState
import com.panelos.panelhome.presentation.MonitoringState
import com.panelos.panelhome.ui.components.ConnectionBanner
import com.panelos.panelhome.ui.components.DesktopCard
import com.panelos.panelhome.ui.components.FpsCard
import com.panelos.panelhome.ui.components.FpsSummaryStyle
import com.panelos.panelhome.ui.components.IdleMediaCard
import com.panelos.panelhome.ui.components.MediaCard
import com.panelos.panelhome.ui.components.MetricCard
import com.panelos.panelhome.ui.components.MetricCardContent
import com.panelos.panelhome.ui.components.formatMemoryUsage
import com.panelos.panelhome.ui.components.hasMedia
import com.panelos.panelhome.ui.components.hasVramCapacity
import com.panelos.panelhome.ui.components.vramUsagePercent
import com.panelos.panelhome.ui.integrations.relativeAge
import com.panelos.panelhome.ui.monitoring.ComputerResource
import com.panelos.panelhome.ui.monitoring.chartPoints
import com.panelos.panelhome.ui.theme.PanelSpacing
import com.panelos.panelhome.ui.theme.PanelType
import java.util.Locale
import kotlinx.coroutines.delay

/** Above this load, held for [HIGH_LOAD_HOLD_MILLIS], a number turns amber (never red). */
internal const val HIGH_LOAD_PERCENT = 90.0
internal const val HIGH_LOAD_HOLD_MILLIS = 10_000L

/**
 * The Painel, in one of two structures: with media, the big media card on the
 * left and a 2x3 grid on the right; without, four metric cards, the FPS (or
 * idle media) row and a volume line. The FPS card is simply absent when the PC
 * offers none, and the page reflows around it.
 */
@Composable
fun DashboardScreen(
    data: PanelData,
    monitoring: MonitoringState,
    fps: FpsState,
    connectionStatus: ConnectionStatus,
    onOpenDesktop: () -> Unit,
    onOpenSettings: () -> Unit,
    onRetryConnection: () -> Unit,
    onPreviousTrack: () -> Unit,
    onPlayPause: () -> Unit,
    onNextTrack: () -> Unit,
    onVolumeChange: (Int) -> Unit,
    onVolumeChangeFinished: (Int) -> Unit,
    onToggleVolumeMute: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val connected = connectionStatus == ConnectionStatus.CONNECTED
    val volumeEnabled = isVolumeControlEnabled(connectionStatus, data.volume)
    val windowMinutes = monitoring.preferences.windowMinutes
    val staleLabel = if (connected) null else monitoring.lastTelemetryAt?.let {
        relativeAge((monitoring.now - it).coerceAtLeast(0))
    }
    val cards = rememberPanelMetrics(data, monitoring)
    val showFps = fps != FpsState.Unavailable
    val media = hasMedia(data.media)

    BoxWithConstraints(
        modifier.fillMaxSize().padding(horizontal = PanelSpacing.page).padding(top = PanelSpacing.sm, bottom = PanelSpacing.lg),
    ) {
        val wide = maxWidth >= 800.dp && maxHeight >= 360.dp
        Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.spacedBy(PanelSpacing.md)) {
            ConnectionBanner(connectionStatus, onRetryConnection, onOpenSettings)
            val body = Modifier.weight(1f).fillMaxWidth()
            val card: @Composable (PanelMetric, Modifier) -> Unit = { metric, m ->
                MetricCard(metric.content, m, highLoad = metric.high, stale = !connected, staleLabel = staleLabel)
            }
            if (!wide) {
                // Not the tablet's size: a plain scrolling column of the same cards.
                Column(body.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(PanelSpacing.md)) {
                    val m = Modifier.fillMaxWidth().height(180.dp)
                    cards.forEach { card(it, m) }
                    FpsCard(fps, windowMinutes, m, stale = !connected, staleLabel = staleLabel)
                    DesktopCard(onOpenDesktop, Modifier.fillMaxWidth().height(96.dp), horizontal = true, stale = !connected)
                    if (media) {
                        MediaCard(
                            data.media, onPreviousTrack, onPlayPause, onNextTrack, data.volume, volumeEnabled,
                            onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute, Modifier.fillMaxWidth().height(460.dp),
                        )
                    } else {
                        IdleMediaCard(
                            data.volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute,
                            Modifier.fillMaxWidth().height(72.dp),
                        )
                    }
                }
            } else if (media) {
                Row(body, horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                    MediaCard(
                        data.media, onPreviousTrack, onPlayPause, onNextTrack, data.volume, volumeEnabled,
                        onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute,
                        Modifier.width(372.dp).fillMaxHeight(),
                    )
                    Column(Modifier.weight(1f).fillMaxHeight(), verticalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                        val cell = Modifier.weight(1f).fillMaxHeight()
                        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                            card(cards[0], cell); card(cards[1], cell)
                        }
                        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                            card(cards[2], cell); card(cards[3], cell)
                        }
                        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                            if (showFps) {
                                FpsCard(
                                    fps, windowMinutes, cell, numeral = PanelType.numeralM,
                                    summaryStyle = FpsSummaryStyle.None, stale = !connected, staleLabel = staleLabel,
                                )
                                DesktopCard(onOpenDesktop, cell, stale = !connected)
                            } else {
                                // No FPS card: Desktop takes both columns.
                                DesktopCard(onOpenDesktop, Modifier.fillMaxSize(), horizontal = true, stale = !connected)
                            }
                        }
                    }
                }
            } else {
                Column(body, verticalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                    Row(Modifier.weight(196f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                        cards.forEach { card(it, Modifier.weight(1f).fillMaxHeight()) }
                    }
                    Row(Modifier.weight(156f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                        if (showFps) {
                            FpsCard(fps, windowMinutes, Modifier.weight(3f).fillMaxHeight(), stale = !connected, staleLabel = staleLabel)
                        } else {
                            // No FPS card: the idle media card, larger, takes its place.
                            IdleMediaCard(
                                data.volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute,
                                Modifier.weight(3f).fillMaxHeight(), compact = false,
                            )
                        }
                        DesktopCard(onOpenDesktop, Modifier.weight(1f).fillMaxHeight(), stale = !connected)
                    }
                    if (showFps) {
                        IdleMediaCard(
                            data.volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute,
                            Modifier.fillMaxWidth().height(72.dp),
                        )
                    }
                }
            }
        }
    }
}

internal fun isVolumeControlEnabled(
    connectionStatus: ConnectionStatus,
    volume: VolumeState,
): Boolean = connectionStatus == ConnectionStatus.CONNECTED && volume.available

/** One of the four metric cards plus whether its number should show the high-load amber. */
internal class PanelMetric(val content: MetricCardContent, val high: Boolean)

@Composable
private fun rememberPanelMetrics(data: PanelData, monitoring: MonitoringState): List<PanelMetric> {
    val cpuSeries = remember(monitoring.telemetry, monitoring.chartNow, monitoring.preferences.windowMinutes) { seriesOf(monitoring, ComputerResource.CPU) }
    val gpuSeries = remember(monitoring.telemetry, monitoring.chartNow, monitoring.preferences.windowMinutes) { seriesOf(monitoring, ComputerResource.GPU) }
    val ramSeries = remember(monitoring.telemetry, monitoring.chartNow, monitoring.preferences.windowMinutes) { seriesOf(monitoring, ComputerResource.MEMORY) }
    val locale = LocalConfiguration.current.locales[0]
    val noReading = stringResource(R.string.metric_no_reading)
    val vramPercent = vramUsagePercent(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb)
    val highCpu = rememberHighLoad(data.cpu.usagePercent)
    val highGpu = rememberHighLoad(data.gpu.usagePercent)
    val highRam = rememberHighLoad(data.ram.usagePercent)
    val highVram = rememberHighLoad(vramPercent)

    fun card(label: String, percent: Double?, secondary: String?, series: List<Float?>, high: Boolean): PanelMetric {
        val value = percent?.takeIf { it.isFinite() }?.let { decimal(it, locale) }
        return PanelMetric(
            MetricCardContent(
                label = label,
                value = value,
                unit = "%",
                secondary = secondary,
                series = if (value == null) emptyList() else series,
                description = if (value == null) "$label, $noReading"
                else "$label, $value por cento" + (secondary?.let { ", $it" } ?: ""),
            ),
            high,
        )
    }
    val ramSecondary = if (data.ram.usedGb != null && data.ram.totalGb != null) formatMemoryUsage(data.ram.usedGb, data.ram.totalGb, locale) else null
    val vramSecondary = if (hasVramCapacity(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb)) {
        formatMemoryUsage(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb, locale)
    } else {
        null
    }
    return listOf(
        card(stringResource(R.string.metric_cpu), data.cpu.usagePercent, joinReadings(temperature(data.cpu.temperatureCelsius, locale), ghz(data.cpu.frequencyGhz, locale)), cpuSeries, highCpu),
        card(stringResource(R.string.metric_gpu), data.gpu.usagePercent, joinReadings(temperature(data.gpu.temperatureCelsius, locale), ghz(data.gpu.frequencyGhz, locale)), gpuSeries, highGpu),
        card(stringResource(R.string.metric_ram), data.ram.usagePercent, ramSecondary, ramSeries, highRam),
        card(stringResource(R.string.metric_vram), vramPercent, vramSecondary, emptyList(), highVram),
    )
}

private fun seriesOf(monitoring: MonitoringState, resource: ComputerResource): List<Float?> =
    chartPoints(monitoring, resource, null).map { it.value?.toFloat() }

/** True once [percent] has stayed at or above [HIGH_LOAD_PERCENT] for [HIGH_LOAD_HOLD_MILLIS]. */
@Composable
internal fun rememberHighLoad(percent: Double?): Boolean {
    val high = percent != null && percent.isFinite() && percent >= HIGH_LOAD_PERCENT
    var sustained by remember { mutableStateOf(false) }
    LaunchedEffect(high) {
        if (high) {
            delay(HIGH_LOAD_HOLD_MILLIS)
            sustained = true
        } else {
            sustained = false
        }
    }
    return sustained && high
}

internal fun decimal(value: Double, locale: Locale): String = String.format(locale, "%.1f", value)

private fun temperature(value: Double?, locale: Locale): String? =
    value?.takeIf { it.isFinite() }?.let { String.format(locale, "%.0f °C", it) }

private fun ghz(value: Double?, locale: Locale): String? =
    value?.takeIf { it.isFinite() }?.let { String.format(locale, "%.2f GHz", it) }

/** Joins the readings that exist; none left means no line at all, never a dash. */
internal fun joinReadings(vararg readings: String?): String? =
    readings.filterNotNull().joinToString(" · ").ifEmpty { null }
