package com.panelos.panelhome.ui.integrations

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.presentation.*
import com.panelos.panelhome.ui.theme.PanelGood
import com.panelos.panelhome.ui.theme.PanelWarning
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.ui.components.ConnectionBanner
import com.panelos.panelhome.ui.components.PanelIcon
import com.panelos.panelhome.R
import com.panelos.panelhome.ui.theme.PanelType

@Composable
internal fun NoticesScreen(state: LocalIntegrationsState,
    connectionStatus: ConnectionStatus,
    pages: Map<String, String>, onSeen: () -> Unit, onNavigate: (String) -> Unit,
    onRetryConnection: () -> Unit, onOpenSettings: () -> Unit) {
    var expanded by remember { mutableStateOf<String?>(null) }
    var valueDetail by remember { mutableStateOf<PublishedValueUi?>(null) }
    LaunchedEffect(state.history.firstOrNull()?.notice?.id) { onSeen() }
    Column(Modifier.fillMaxSize().padding(horizontal = 24.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        ConnectionBanner(connectionStatus, onRetryConnection, onOpenSettings)
        if (state.connected && !state.enabled) Text("Os avisos estão desligados no Control Center.", style = MaterialTheme.typography.bodyMedium)
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(18.dp)) {
            Column(Modifier.weight(0.58f).alpha(if (state.connected) 1f else 0.55f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("VALORES", style = MaterialTheme.typography.labelLarge, modifier = Modifier.semantics { heading() })
                if (state.values.isEmpty()) Surface(shape = RoundedCornerShape(22.dp)) {
                    Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text("Nenhum valor ainda.", style = MaterialTheme.typography.titleMedium)
                        Text("Programas no PC publicam valores aqui. O passo a passo está no Control Center, em Integrações › Avisos.",
                            color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                } else LazyVerticalGrid(GridCells.Fixed(3), horizontalArrangement = Arrangement.spacedBy(10.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    items(state.values.filter { it.value.value.isNotBlank() && (it.expiresAt == null || it.expiresAt > state.now) }.sortedByDescending { it.updatedAt }, key = { it.value.name }) { value ->
                        Surface(onClick = { valueDetail = value }, shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
                            Column(Modifier.heightIn(min = 137.dp).padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                PanelIcon(levelIconRes(value.value.level), null, size = 18.dp, tint = levelColor(value.value.level))
                                Text(value.value.name, color = levelColor(value.value.level),
                                    style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                }
                                Text(value.value.value, style = PanelType.numeralS,
                                    maxLines = 1, softWrap = false, overflow = TextOverflow.Ellipsis)
                                value.value.unit?.let { Text(it, style = MaterialTheme.typography.labelMedium, maxLines = 1) }
                                val old = state.now - value.updatedAt > 300_000
                                Text("${relativeAge(state.now - value.updatedAt)}${if (old) " · desatualizado" else ""}",
                                    color = if (old) PanelWarning else MaterialTheme.colorScheme.onSurfaceVariant,
                                    style = MaterialTheme.typography.labelMedium)
                            }
                        }
                    }
                }
            }
            Column(Modifier.weight(0.42f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("RECENTES", Modifier.weight(1f), style = MaterialTheme.typography.labelLarge)
                    TextButton(onClick = onSeen, modifier = Modifier.heightIn(min = 48.dp)) { Text("Marcar como lidos") }
                }
                if (state.history.isEmpty()) Text("Nenhum aviso recente.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(state.history, key = { it.notice.id }) { notice ->
                        Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
                            Column(Modifier.fillMaxWidth().clickable { expanded = if (expanded == notice.notice.id) null else notice.notice.id }.padding(14.dp)) {
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    PanelIcon(levelIconRes(notice.notice.level), null, tint = levelColor(notice.notice.level))
                                    Text(notice.notice.title.ifEmpty { "Aviso" }, Modifier.weight(1f), style = MaterialTheme.typography.titleMedium,
                                        maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    Text(relativeAge(state.now - notice.receivedAt), style = MaterialTheme.typography.labelMedium)
                                }
                                Text(notice.notice.text, maxLines = if (expanded == notice.notice.id) Int.MAX_VALUE else 1,
                                    overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium)
                                if (expanded == notice.notice.id) notice.notice.page?.takeIf { it in pages }?.let { page ->
                                    TextButton(onClick = { onNavigate(page) }, modifier = Modifier.heightIn(min = 48.dp)) { Text("Ir para ${pages[page]}") }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    valueDetail?.let { detail ->
        val current = state.values.find { it.value.name == detail.value.name }
        AlertDialog(onDismissRequest = { valueDetail = null }, title = { Text(detail.value.name) },
            text = { Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(current?.value?.value ?: "Este valor já venceu.")
                current?.value?.unit?.let { Text(it) }
                if (current != null) Text(relativeAge(state.now - current.updatedAt))
                current?.expiresAt?.let { Text("Some em ${((it - state.now).coerceAtLeast(0) + 999) / 1_000} s") }
            } }, confirmButton = { TextButton(onClick = { valueDetail = null }) { Text("Fechar") } })
    }
}

internal fun relativeAge(ms: Long): String = when {
    ms < 1_000 -> "agora"
    ms < 60_000 -> "há ${ms.coerceAtLeast(0) / 1_000} s"
    ms < 3_600_000 -> "há ${ms / 60_000} min"
    else -> "há ${ms / 3_600_000} h"
}
internal fun levelIconRes(level: String) = when (level) {
    "success" -> R.drawable.ic_check
    "warning", "error" -> R.drawable.ic_warning
    else -> R.drawable.ic_info
}
internal fun levelLabel(level: String) = when (level) { "success" -> "Sucesso"; "warning" -> "Aviso"; "error" -> "Erro"; else -> "Informação" }
@Composable internal fun levelColor(level: String): Color = when (level) {
    "success" -> PanelGood
    "warning" -> PanelWarning
    "error" -> MaterialTheme.colorScheme.error
    else -> MaterialTheme.colorScheme.primary
}
