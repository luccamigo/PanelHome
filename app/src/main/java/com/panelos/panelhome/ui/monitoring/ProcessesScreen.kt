package com.panelos.panelhome.ui.monitoring

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.domain.protocol.ProcessSort
import com.panelos.panelhome.presentation.MonitoringPreferences
import com.panelos.panelhome.presentation.MonitoringState
import com.panelos.panelhome.presentation.ProcessRow
import com.panelos.panelhome.presentation.TerminationStage
import com.panelos.panelhome.ui.components.formatMemoryUsage
import com.panelos.panelhome.ui.components.formatUsagePercent
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.ui.components.ConnectionBanner
import com.panelos.panelhome.ui.components.SegmentedControl
import com.panelos.panelhome.ui.components.formatMemoryUsage
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelHairline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSpacing
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import com.panelos.panelhome.ui.theme.PanelWarning
import androidx.compose.foundation.BorderStroke
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.TextUnit

@Composable
internal fun ProcessesScreen(
    state: MonitoringState,
    data: PanelData,
    onPreferences: (MonitoringPreferences) -> Unit,
    onInteracting: (Boolean) -> Unit,
    onTerminate: (String) -> Unit = {},
    onDismissTermination: () -> Unit = {},
    connectionStatus: ConnectionStatus = ConnectionStatus.CONNECTED,
    onRetryConnection: () -> Unit = {},
    onOpenSettings: () -> Unit = {},
) {
    val list = rememberLazyListState()
    var fingerDown by remember { mutableStateOf(false) }
    var expanded by remember { mutableStateOf<String?>(null) }
    val reducedMotion = LocalPanelReducedMotion.current
    val latestOnInteracting by rememberUpdatedState(onInteracting)
    LaunchedEffect(fingerDown, list.isScrollInProgress) { latestOnInteracting(fingerDown || list.isScrollInProgress) }
    DisposableEffect(Unit) { onDispose { latestOnInteracting(false) } }
    LaunchedEffect(state.preferences.grouped, state.preferences.sort) { expanded = null; list.scrollToItem(0) }
    Column(
        Modifier.fillMaxSize().padding(horizontal = PanelSpacing.page).padding(top = PanelSpacing.sm, bottom = PanelSpacing.md),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        ConnectionBanner(connectionStatus, onRetryConnection, onOpenSettings)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Text(
                buildAnnotatedString {
                    append("PC: CPU ")
                    withStyle(SpanStyle(color = PanelText, fontWeight = FontWeight.SemiBold)) { append(formatUsagePercent(data.cpu.usagePercent)) }
                    append(" \u00B7 RAM ")
                    withStyle(SpanStyle(color = PanelText, fontWeight = FontWeight.SemiBold)) { append(formatMemoryUsage(data.ram.usedGb, data.ram.totalGb)) }
                },
                style = PanelType.body, color = PanelTextMuted, modifier = Modifier.weight(1f), maxLines = 1,
            )
            if (state.orderPaused) Text("Ordem pausada", style = PanelType.label, color = PanelWarning)
            Row(Modifier.heightIn(min = 48.dp).clickable(role = Role.Switch) {
                onPreferences(state.preferences.copy(grouped = !state.preferences.grouped))
            }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Agrupar por nome", style = PanelType.body, color = PanelTextMuted)
                Switch(state.preferences.grouped, onCheckedChange = null)
            }
            val sorts = ProcessSort.entries
            SegmentedControl(
                options = sorts.map { if (it == ProcessSort.CPU) "CPU" else "RAM" },
                selectedIndex = sorts.indexOf(state.preferences.sort),
                onSelect = { onPreferences(state.preferences.copy(sort = sorts[it])) },
            )
        }
        state.termination?.takeIf { it.stage == TerminationStage.FINISHED }?.let { termination ->
            Row(Modifier.fillMaxWidth().semantics { liveRegion = LiveRegionMode.Polite }, verticalAlignment = Alignment.CenterVertically) {
                Text(termination.message.orEmpty(), Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium,
                    color = if (termination.failed) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface)
                TextButton(onClick = onDismissTermination, modifier = Modifier.heightIn(min = 48.dp)) { Text("Fechar") }
            }
        }
        // Disconnected is the banner's job; a read error on a live link is told here.
        if (state.connected && state.processError) StatusNote("Não consegui atualizar a lista. Tentando de novo.")
        Surface(
            Modifier.weight(1f).fillMaxWidth(), shape = RoundedCornerShape(PanelRadius.card), color = PanelSurface,
            border = BorderStroke(1.dp, PanelHairline),
        ) {
            when {
                !state.receivedProcesses -> Column(Modifier.padding(16.dp)) {
                    repeat(6) { Row(Modifier.heightIn(min = 56.dp).fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text("—"); Text("—        —")
                    } }
                    StatusNote(if (state.processError) "Não consegui atualizar a lista. Tentando de novo." else "Lendo os processos…")
                }
                state.processRows.isEmpty() -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    Text("O PC não devolveu nenhum processo. Tente de novo em instantes.", modifier = Modifier.padding(24.dp))
                }
                else -> LazyColumn(state = list, modifier = Modifier.fillMaxSize()
                    .alpha(if (state.connected && !state.processError) 1f else 0.55f)
                    .pointerInput(Unit) {
                        awaitPointerEventScope {
                            while (true) {
                                val event = awaitPointerEvent(PointerEventPass.Initial)
                                val pressed = event.changes.any { it.pressed }
                                if (pressed != fingerDown) {
                                    fingerDown = pressed
                                    latestOnInteracting(pressed || list.isScrollInProgress)
                                }
                            }
                        }
                    }, contentPadding = PaddingValues(vertical = 8.dp)) {
                    itemsIndexed(state.processRows, key = { _, row -> row.key }) { index, row ->
                        Column(if (reducedMotion || state.orderPaused) Modifier else Modifier.animateItem(
                            fadeInSpec = null, fadeOutSpec = null, placementSpec = tween(180))) {
                            if (!row.item.readable && (index == 0 || state.processRows[index - 1].item.readable)) {
                                HorizontalDivider(Modifier.padding(horizontal = 18.dp))
                                Text("Sem permissão de leitura", style = MaterialTheme.typography.labelMedium,
                                    modifier = Modifier.padding(horizontal = 18.dp, vertical = 8.dp).semantics { heading() })
                            }
                            ProcessLine(row, expanded == row.key, state, onExpand = { expanded = if (expanded == row.key) null else row.key },
                                onTerminate = onTerminate)
                        }
                    }
                }
            }
        }
        Text("% de CPU é a parte do total do PC (todos os núcleos). A soma de todos os processos é próxima do uso total.",
            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
private fun ProcessLine(row: ProcessRow, expanded: Boolean, state: MonitoringState, onExpand: () -> Unit,
    onTerminate: (String) -> Unit) {
    val item = row.item
    val reducedMotion = LocalPanelReducedMotion.current
    Column {
        Row(Modifier.fillMaxWidth().heightIn(min = 56.dp).clickable(role = Role.Button, onClick = onExpand)
            .padding(horizontal = 18.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Box(Modifier.size(40.dp).background(PanelSurfaceElevated, RoundedCornerShape(PanelRadius.chip)), contentAlignment = Alignment.Center) {
                Text(item.name.take(1).uppercase(), style = PanelType.label.copy(letterSpacing = TextUnit.Unspecified), color = PanelTextMuted)
            }
            Text(item.name, Modifier.weight(1f), style = PanelType.value, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (state.preferences.grouped) Text("\u00D7${item.count}", style = PanelType.body, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Column(Modifier.width(100.dp), horizontalAlignment = Alignment.End) {
                Text(if (row.ended) "—" else formatUsagePercent(item.cpuPercent),
                    style = PanelType.value, maxLines = 1)
                Box(Modifier.width(72.dp).height(3.dp).background(MaterialTheme.colorScheme.outline)) {
                    if (item.cpuPercent != null && !row.ended) Box(Modifier.fillMaxHeight().fillMaxWidth((item.cpuPercent / 100).toFloat().coerceIn(0f, 1f))
                        .background(MaterialTheme.colorScheme.primary))
                }
            }
            Text(if (row.ended) "—" else memoryText(item.memoryBytes), Modifier.width(112.dp), maxLines = 1,
                style = PanelType.value)
        }
        AnimatedVisibility(expanded,
            enter = expandVertically(tween(if (reducedMotion) 0 else 200)) + fadeIn(tween(if (reducedMotion) 100 else 200)),
            exit = shrinkVertically(tween(if (reducedMotion) 0 else 200)) + fadeOut(tween(if (reducedMotion) 100 else 200))) {
            Column(Modifier.fillMaxWidth().padding(start = 62.dp, end = 18.dp, top = 8.dp, bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                val startedUnixMs = item.startTimeUtcTicks?.let { (it - 621_355_968_000_000_000L) / 10_000 }
                    ?.takeIf { it > 0 && it <= state.processSampleUnixMs }
                val uptime = " · aberto há ${startedUnixMs?.let { ageText(state.processSampleUnixMs - it) } ?: "—"}"
                val owner = when (item.owner) {
                    "current_user" -> "sessão do seu usuário"
                    "other_user" -> "outro usuário"
                    "system" -> "sistema"
                    else -> "dono indisponível"
                }
                Text("PID ${item.pid ?: "—"}$uptime · $owner", style = MaterialTheme.typography.bodyMedium)
                if (state.canTerminate(row)) HoldToTerminateButton(item.name) { onTerminate(row.key) }
                else if (state.termination?.key == row.key && state.termination.stage == TerminationStage.SENDING) HoldToTerminateButton(
                    item.name, sending = true) {}
                else Text(when {
                    row.ended -> "Este processo já tinha fechado."
                    !state.connected -> "Sem conexão com o PC."
                    !item.readable -> "Sem permissão de leitura. Não dá para encerrar."
                    state.preferences.grouped -> "Desfaça o agrupamento para encerrar um processo."
                    item.owner == "other_user" -> "Este processo pertence a outro usuário."
                    item.owner == "system" || item.isProtected -> "Processo protegido. O PC não permite encerrar daqui."
                    !state.terminateEnabled -> "Encerrar pelo tablet está desligado no Control Center."
                    else -> "O PC não oferece encerrar este processo."
                }, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}
