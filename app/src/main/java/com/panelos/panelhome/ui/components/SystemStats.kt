package com.panelos.panelhome.ui.components

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.GpuMetrics
import com.panelos.panelhome.domain.model.RamMetrics
import java.util.Locale

@Composable
fun SystemStatsPanel(
    cpu: CpuMetrics,
    gpu: GpuMetrics,
    ram: RamMetrics,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            ProcessorMetric(
                label = stringResource(R.string.metric_cpu),
                name = cpu.name,
                temperatureCelsius = cpu.temperatureCelsius,
                usagePercent = cpu.usagePercent,
                frequencyGhz = cpu.frequencyGhz,
                modifier = Modifier.weight(1f),
            )
            ProcessorMetric(
                label = stringResource(R.string.metric_gpu),
                name = gpu.name,
                temperatureCelsius = gpu.temperatureCelsius,
                usagePercent = gpu.usagePercent,
                frequencyGhz = gpu.frequencyGhz,
                modifier = Modifier.weight(1f),
            )
        }
        MemoryMetrics(ram = ram, gpu = gpu)
    }
}

@Composable
private fun MemoryMetrics(
    ram: RamMetrics,
    gpu: GpuMetrics,
) {
    // The bar and the percentage are both derived from the reported capacity, so
    // the label, the number and the fill can never contradict each other.
    val vramPercent = vramUsagePercent(
        usedGb = gpu.memoryUsedGb,
        totalGb = gpu.memoryTotalGb,
    )

    MetricSurface(modifier = Modifier.fillMaxWidth()) {
        CompactMemoryMetric(
            label = stringResource(R.string.metric_ram),
            usage = formatMemoryUsage(ram.usedGb, ram.totalGb),
            usagePercent = ram.usagePercent,
        )
        CompactMemoryMetric(
            label = stringResource(R.string.metric_vram),
            usage = formatVramUsage(gpu.memoryUsedGb, gpu.memoryTotalGb),
            usagePercent = vramPercent,
            showUsagePercent = vramPercent != null,
        )
    }
}

@Composable
private fun CompactMemoryMetric(
    label: String,
    usage: String,
    usagePercent: Double?,
    showUsagePercent: Boolean = true,
) {
    val progress = normalizedUsageProgress(usagePercent)
    Column(verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = label,
                modifier = Modifier.width(64.dp),
                style = MaterialTheme.typography.titleMedium,
                color = MaterialTheme.colorScheme.primary,
            )
            Text(
                text = usage,
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyLarge,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (showUsagePercent) {
                Text(
                    text = formatUsagePercent(usagePercent),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        progress?.let { progressValue ->
            LinearProgressIndicator(
                progress = { progressValue },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(4.dp),
            )
        }
    }
}

internal fun hasVramCapacity(usedGb: Double?, totalGb: Double?): Boolean =
    usedGb != null && usedGb.isFinite() && usedGb >= 0.0 &&
        totalGb != null && totalGb.isFinite() && totalGb > 0.0

/**
 * VRAM occupancy derived from the reported capacity pair rather than taken from a
 * separate field. A GPU memory *activity* sensor is not occupancy, so nothing but
 * used/total may drive this. Returns null when capacity is unknown, which leaves
 * the row showing a dash with no bar instead of a misleading empty one.
 */
internal fun vramUsagePercent(usedGb: Double?, totalGb: Double?): Double? =
    if (hasVramCapacity(usedGb, totalGb)) {
        (usedGb!! / totalGb!! * 100.0).coerceIn(0.0, 100.0)
    } else {
        null
    }

internal fun formatVramUsage(
    usedGb: Double?,
    totalGb: Double?,
    locale: Locale = Locale.getDefault(),
): String = if (hasVramCapacity(usedGb, totalGb)) {
    formatMemoryUsage(usedGb, totalGb, locale)
} else {
    METRIC_UNAVAILABLE
}

internal fun normalizedUsageProgress(usagePercent: Double?): Float? =
    usagePercent
        ?.takeIf(Double::isFinite)
        ?.coerceIn(0.0, 100.0)
        ?.div(100.0)
        ?.toFloat()

@Composable
private fun ProcessorMetric(
    label: String,
    name: String?,
    temperatureCelsius: Double?,
    usagePercent: Double?,
    frequencyGhz: Double?,
    modifier: Modifier = Modifier,
) {
    MetricSurface(modifier = modifier) {
        Text(
            text = label,
            style = MaterialTheme.typography.titleMedium,
            color = MaterialTheme.colorScheme.primary,
        )
        name?.takeIf(String::isNotBlank)?.let { hardwareName ->
            Text(
                text = hardwareName,
                style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        Text(
            text = formatTemperatureCelsius(temperatureCelsius),
            style = MaterialTheme.typography.headlineMedium,
        )
        Text(
            text = formatUsagePercent(usagePercent),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = formatFrequencyGhz(frequencyGhz),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun MetricSurface(
    modifier: Modifier,
    content: @Composable () -> Unit,
) {
    Surface(
        modifier = modifier,
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        shape = RoundedCornerShape(22.dp),
    ) {
        Column(
            modifier = Modifier.padding(horizontal = 18.dp, vertical = 16.dp),
            verticalArrangement = Arrangement.spacedBy(3.dp),
        ) {
            content()
        }
    }
}
