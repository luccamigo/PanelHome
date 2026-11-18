package com.panelos.panelhome.ui.monitoring

import com.panelos.panelhome.ui.theme.PanelText
import androidx.compose.ui.platform.LocalConfiguration

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.*
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.FpsState
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.ui.components.ConnectionBanner
import com.panelos.panelhome.ui.components.FpsCard
import com.panelos.panelhome.ui.components.FpsSummaryStyle
import com.panelos.panelhome.ui.components.MetricCardFrame
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelSpacing
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import androidx.compose.ui.graphics.Color
import java.util.Locale
import com.panelos.panelhome.presentation.MonitoringPreferences
import com.panelos.panelhome.presentation.MonitoringState
import com.panelos.panelhome.ui.components.*
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion

@Composable
internal fun ComputerScreen(
    state: MonitoringState,
    data: PanelData,
    fps: FpsState,
    connectionStatus: ConnectionStatus,
    onRetryConnection: () -> Unit,
    onOpenSettings: () -> Unit,
    onOpenResource: (ComputerResource) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier.fillMaxSize().padding(horizontal = PanelSpacing.page).padding(top = PanelSpacing.sm, bottom = PanelSpacing.lg),
        verticalArrangement = Arrangement.spacedBy(PanelSpacing.md),
    ) {
        ConnectionBanner(connectionStatus, onRetryConnection, onOpenSettings)
        Column(Modifier.weight(1f).alpha(if (state.connected) 1f else 0.55f), verticalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                listOf(ComputerResource.CPU, ComputerResource.MEMORY, ComputerResource.GPU).forEach { resource ->
                    ResourceTile(resource, state, data, { onOpenResource(resource) }, Modifier.weight(1f).fillMaxHeight())
                }
            }
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(PanelSpacing.cardGap)) {
                ResourceTile(ComputerResource.NETWORK, state, data, { onOpenResource(ComputerResource.NETWORK) }, Modifier.weight(1f).fillMaxHeight())
                // No FPS card when the PC offers none: Rede takes the whole row.
                FpsCard(
                    fps, state.preferences.windowMinutes, Modifier.weight(1f).fillMaxHeight(),
                    numeral = PanelType.numeralM, summaryStyle = FpsSummaryStyle.MinAverageMax, chartHeight = 92.dp,
                    stale = !state.connected,
                )
            }
        }
    }
}

/** "3,2 MB/s" into ("3,2", "MB/s"); an unavailable reading gives null, so no placeholder is drawn. */
internal fun splitRate(text: String): Pair<String, String>? {
    if (text == METRIC_UNAVAILABLE) return null
    val cut = text.lastIndexOf(' ')
    return if (cut <= 0) null else text.substring(0, cut) to text.substring(cut + 1)
}

@Composable
private fun ResourceTile(resource: ComputerResource, state: MonitoringState, data: PanelData, onOpen: () -> Unit, modifier: Modifier) {
    val points = remember(state.telemetry, state.network, state.chartNow, state.preferences.windowMinutes, resource) {
        chartPoints(state, resource, null)
    }
    val current = currentPoint(state, resource, points)
    val network = resource == ComputerResource.NETWORK
    val locale = LocalConfiguration.current.locales[0]
    val percent = if (network) null else current?.value?.takeIf { it.isFinite() }?.let { String.format(locale, "%.1f", it) }
    val down = if (network) splitRate(rateText(current?.value)) else null
    val up = if (network) splitRate(rateText(current?.secondary)) else null
    val summary = remember(state.now / 10_000, resource, state.connected, state.preferences.windowMinutes) {
        val stats = summarize(points)
        val format: (Double?) -> String = if (network) { v -> rateText(v) } else { v -> formatUsagePercent(v) }
        "${resource.label}, últimos ${state.preferences.windowMinutes} minutos. Agora ${if (network) "${rateText(current?.value)} de download" else formatUsagePercent(current?.value)}. " +
            "Média ${format(stats.average)}, máximo ${format(stats.maximum)}. Toque para ver o detalhe."
    }
    val subtitle = resourceSubtitle(resource, state, data, current)
    val reading = state.connected && current != null && (current.value != null || current.secondary != null)
    MetricCardFrame(modifier, description = summary, stale = false, onClick = onOpen) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(resource.label, style = PanelType.label, color = PanelAccent, maxLines = 1)
            Box(Modifier.weight(1f).padding(start = 8.dp), contentAlignment = Alignment.CenterEnd) {
                val corner = if (network) networkLabel(state, null) else cornerText(resource, data)
                if (corner != null) {
                    Text(corner, style = PanelType.caption, color = PanelTextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        if (reading && network) {
            Row(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalAlignment = Alignment.Bottom) {
                RateNumber("↓", down, PanelText, PanelAccent)
                RateNumber("↑", up, PanelTextMuted, PanelTextMuted)
            }
        } else if (reading && percent != null) {
            Row(verticalAlignment = Alignment.Bottom) {
                Text(percent, style = PanelType.numeralM, color = PanelText, maxLines = 1, softWrap = false)
                Text("%", style = PanelType.value, color = PanelTextMuted, modifier = Modifier.padding(start = 4.dp, bottom = 6.dp))
            }
        } else {
            Text(subtitle, style = PanelType.body, color = PanelTextMuted)
        }
        if (reading && !network) {
            readingCaption(resource, data)?.let {
                Text(it, style = PanelType.caption, color = PanelTextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        MonitoringChart(
            points, state.chartNow, state.preferences.windowMinutes, network,
            Modifier.fillMaxWidth().weight(1f).heightIn(min = 66.dp), gaps = state.gaps,
        )
        ChartTimeLabels(points, state)
    }
}

@Composable
private fun RateNumber(arrow: String, rate: Pair<String, String>?, color: Color, arrowColor: Color) {
    if (rate == null) return
    Row(verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
        Text(arrow, style = PanelType.numeralS, color = arrowColor)
        Text(rate.first, style = PanelType.numeralM, color = color, maxLines = 1, softWrap = false)
        Text(rate.second, style = PanelType.value, color = PanelTextMuted, modifier = Modifier.padding(bottom = 6.dp))
    }
}

/** The grey line at the card's right: temperature and clock of CPU and GPU, nothing when none exists. */
private fun cornerText(resource: ComputerResource, data: PanelData): String? {
    val locale = Locale.getDefault()
    fun t(v: Double?) = v?.takeIf { it.isFinite() }?.let { String.format(locale, "%.0f °C", it) }
    fun g(v: Double?) = v?.takeIf { it.isFinite() }?.let { String.format(locale, "%.2f GHz", it) }
    return when (resource) {
        ComputerResource.CPU -> listOfNotNull(t(data.cpu.temperatureCelsius), g(data.cpu.frequencyGhz))
        ComputerResource.GPU -> listOfNotNull(t(data.gpu.temperatureCelsius), g(data.gpu.frequencyGhz))
        else -> emptyList()
    }.joinToString(" · ").ifEmpty { null }
}

/** The line under the number when a reading exists: the model, or used of total memory. */
private fun readingCaption(resource: ComputerResource, data: PanelData): String? {
    val locale = Locale.getDefault()
    return when (resource) {
        ComputerResource.CPU -> data.cpu.name
        ComputerResource.MEMORY -> if (data.ram.usedGb != null && data.ram.totalGb != null)
            String.format(locale, "%.1f de %.1f GB", data.ram.usedGb, data.ram.totalGb) else null
        ComputerResource.GPU -> listOfNotNull(
            data.gpu.name,
            if (hasVramCapacity(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb))
                "VRAM " + formatVramUsage(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb, locale) else null,
        ).joinToString(" \u00B7 ").ifEmpty { null }
        ComputerResource.NETWORK -> null
    }
}

@Composable
internal fun ComputerDetailScreen(
    resource: ComputerResource,
    state: MonitoringState,
    data: PanelData,
    onPreferences: (MonitoringPreferences) -> Unit,
    onBack: () -> Unit,
) {
    BackHandler(onBack = onBack)
    var adapterId by remember { mutableStateOf<String?>(null) }
    val points = remember(state.telemetry, state.network, state.chartNow, state.preferences.windowMinutes, resource, adapterId) {
        chartPoints(state, resource, adapterId)
    }
    val current = currentPoint(state, resource, points)
    val stats = remember(points) { summarize(points) }
    val upload = remember(points) { summarize(points.map { it.copy(value = it.secondary) }) }
    val format: (Double?) -> String = if (resource == ComputerResource.NETWORK) { v -> rateText(v) }
        else { v -> formatUsagePercent(v) }
    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background).pointerInput(Unit) {
        awaitPointerEventScope {
            while (true) awaitPointerEvent(PointerEventPass.Final).changes.forEach { it.consume() }
        }
    }.padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            PanelButton("Voltar", onBack, style = PanelButtonStyle.Secondary, iconRes = com.panelos.panelhome.R.drawable.ic_back, height = 48.dp)
            Text(resource.label, style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
            WindowPicker(state.preferences.windowMinutes) { onPreferences(state.preferences.copy(windowMinutes = it)) }
        }
        if (!state.connected) StatusNote("Sem conexão com o PC")
        if (resource == ComputerResource.NETWORK) AdapterPicker(state, adapterId) { adapterId = it }
        Row(Modifier.weight(1f).alpha(if (state.connected) 1f else 0.55f), horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Column(Modifier.weight(0.62f).fillMaxHeight()) {
                Text(if (resource == ComputerResource.NETWORK) networkLabel(state, adapterId) else resourceSubtitle(resource, state, data, current),
                    color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Box(Modifier.weight(1f)) {
                    MonitoringChart(points, state.chartNow, state.preferences.windowMinutes, resource == ComputerResource.NETWORK,
                        Modifier.fillMaxSize().semantics { role = Role.Image; contentDescription = "${resource.label}. Arraste para ler hora e valor." },
                        interactive = true, gaps = state.gaps)
                    if (points.none { it.value != null || it.secondary != null }) Text("Sem dados nesta janela", modifier = Modifier.align(Alignment.Center))
                }
                ChartTimeLabels(points, state)
            }
            Surface(Modifier.weight(0.38f).fillMaxHeight(), shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
                Column(Modifier.verticalScroll(rememberScrollState()).padding(18.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                    DetailValue(if (resource == ComputerResource.NETWORK) "↓ Agora" else "Agora", format(current?.value))
                    DetailValue("Média", format(stats.average))
                    DetailValue(if (resource == ComputerResource.NETWORK) "↓ Pico" else "Máximo", format(stats.maximum))
                    stats.maximumAt?.let { Text("há ${ageText(state.chartNow - it)}", style = MaterialTheme.typography.labelMedium) }
                    DetailValue("Mínimo", format(stats.minimum))
                    HorizontalDivider()
                    when (resource) {
                        ComputerResource.CPU -> {
                            DetailValue("Temperatura", formatTemperatureCelsius(if (current != null) data.cpu.temperatureCelsius else null))
                            DetailValue("Clock", formatFrequencyGhz(if (current != null) data.cpu.frequencyGhz else null))
                        }
                        ComputerResource.GPU -> {
                            DetailValue("Temperatura", formatTemperatureCelsius(if (current != null) data.gpu.temperatureCelsius else null))
                            DetailValue("Clock", formatFrequencyGhz(if (current != null) data.gpu.frequencyGhz else null))
                            DetailValue("VRAM", if (current != null) formatVramUsage(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb) else METRIC_UNAVAILABLE)
                        }
                        ComputerResource.MEMORY -> DetailValue("Usada / total", if (current != null) formatMemoryUsage(data.ram.usedGb, data.ram.totalGb) else METRIC_UNAVAILABLE)
                        ComputerResource.NETWORK -> {
                            DetailValue("↑ Agora", rateText(current?.secondary))
                            DetailValue("↑ Média", rateText(upload.average))
                            DetailValue("↑ Pico", rateText(upload.maximum))
                            DetailValue("↑ Mínimo", rateText(upload.minimum))
                        }
                    }
                }
            }
        }
    }
}

@Composable
internal fun WindowPicker(selected: Int, onSelected: (Int) -> Unit) {
    val windows = listOf(1, 5, 15)
    SegmentedControl(
        options = windows.map { "$it min" },
        selectedIndex = windows.indexOf(selected).coerceAtLeast(0),
        onSelect = { onSelected(windows[it]) },
    )
}

@Composable
private fun AdapterPicker(state: MonitoringState, selected: String?, onSelected: (String?) -> Unit) {
    val adapters = state.network.lastOrNull { it.rates != null }?.rates?.adapters.orEmpty()
    var others by remember { mutableStateOf(false) }
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        FilterChip(selected == null, { onSelected(null) }, label = { Text("Somado") }, modifier = Modifier.heightIn(min = 48.dp))
        adapters.take(if (adapters.size <= 3) 3 else 2).forEach { adapter ->
            FilterChip(selected == adapter.id, { onSelected(adapter.id) },
                label = { Text(adapter.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                modifier = Modifier.heightIn(min = 48.dp).widthIn(max = 200.dp))
        }
        if (adapters.size > 3) Box {
            FilterChip(selected != null && adapters.drop(2).any { it.id == selected }, { others = true },
                label = { Text("Outros") }, modifier = Modifier.heightIn(min = 48.dp))
            DropdownMenu(others, { others = false }) {
                adapters.drop(2).forEach { adapter -> DropdownMenuItem(text = { Text(adapter.name) }, onClick = { others = false; onSelected(adapter.id) }) }
            }
        }
    }
}

@Composable
internal fun MetricNumber(value: String, large: Boolean = true) {
    Text(value, style = (if (large && LocalDensity.current.fontScale <= 1.1f) MaterialTheme.typography.headlineMedium
        else MaterialTheme.typography.titleLarge).copy(fontFeatureSettings = "tnum"), maxLines = 1, softWrap = false,
        overflow = TextOverflow.Ellipsis)
}

@Composable
internal fun StatusNote(text: String) {
    Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
}

@Composable
private fun DetailValue(label: String, value: String) {
    Column { Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant); MetricNumber(value, false) }
}

@Composable
private fun ChartTimeLabels(points: List<ChartPoint>, state: MonitoringState) {
    val first = points.firstOrNull()
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(if (first != null && state.chartNow - first.at < state.preferences.windowMinutes * 60_000L - 2_000)
            "desde ${clockText(first.unixMs)}" else "há ${state.preferences.windowMinutes} min", style = MaterialTheme.typography.labelMedium, color = PanelTextMuted)
        Text("agora", style = MaterialTheme.typography.labelMedium, color = PanelTextMuted)
    }
}

private fun currentPoint(state: MonitoringState, resource: ComputerResource, points: List<ChartPoint>): ChartPoint? {
    if (!state.connected) return null
    if (if (resource == ComputerResource.NETWORK) state.networkError else state.telemetryError) return null
    val latest = if (resource == ComputerResource.NETWORK) state.lastNetworkAt else state.lastTelemetryAt
    return points.lastOrNull()?.takeIf { latest != null && state.now - latest <= 2_500 }
}

private fun networkLabel(state: MonitoringState, adapterId: String?): String {
    val adapters = state.network.lastOrNull { it.rates != null }?.rates?.adapters.orEmpty()
    return if (adapterId != null) adapters.find { it.id == adapterId }?.name ?: "Adaptador indisponível"
    else when (adapters.size) { 0 -> "Sem adaptador ativo"; 1 -> adapters.first().name; else -> "Soma de ${adapters.size} adaptadores" }
}

private fun resourceSubtitle(resource: ComputerResource, state: MonitoringState, data: PanelData, current: ChartPoint?): String {
    if (!state.connected) return "Sem conexão com o PC"
    if (if (resource == ComputerResource.NETWORK) state.networkError else state.telemetryError) return "Dados inválidos. Tentando de novo."
    val noSamples = if (resource == ComputerResource.NETWORK) state.lastNetworkAt == null else state.lastTelemetryAt == null
    if (noSamples) return "Aguardando os primeiros dados…"
    if (current?.value == null && current?.secondary == null) return "Este sensor não está disponível no PC."
    return when (resource) {
        ComputerResource.CPU -> "${data.cpu.name ?: "CPU"} · ${formatTemperatureCelsius(data.cpu.temperatureCelsius)} · ${formatFrequencyGhz(data.cpu.frequencyGhz)}"
        ComputerResource.MEMORY -> formatUsagePercent(current.value)
        ComputerResource.GPU -> "${data.gpu.name ?: "GPU"} · ${formatTemperatureCelsius(data.gpu.temperatureCelsius)} · VRAM ${formatVramUsage(data.gpu.memoryUsedGb, data.gpu.memoryTotalGb)}"
        ComputerResource.NETWORK -> "↓ Baixar · ↑ Enviar"
    }
}
