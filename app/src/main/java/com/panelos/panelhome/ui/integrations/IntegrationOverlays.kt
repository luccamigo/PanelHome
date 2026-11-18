package com.panelos.panelhome.ui.integrations

import androidx.activity.compose.BackHandler
import androidx.compose.animation.*
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.foundation.focusable
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.presentation.*
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelWarning
import com.panelos.panelhome.ui.components.PanelIcon
import com.panelos.panelhome.R

@Composable
internal fun NoticeBanner(state: LocalIntegrationsState, compact: Boolean, pages: Map<String, String>,
    onDismiss: () -> Unit, onOpenNotices: () -> Unit, onNavigate: (String) -> Unit) {
    val banner = state.banner ?: return
    val notice = banner.notice
    var drag by remember(notice.id) { mutableFloatStateOf(0f) }
    val touchModifier = if (compact) Modifier else Modifier.pointerInput(notice.id) {
        detectVerticalDragGestures(onDragStart = { drag = 0f }, onDragEnd = { if (drag < -24.dp.toPx()) onDismiss() }) { change, delta ->
            if (delta < 0) { change.consume(); drag += delta }
        }
    }
    Box(Modifier.fillMaxWidth().padding(top = 16.dp), contentAlignment = Alignment.TopCenter) {
        Surface(modifier = Modifier.widthIn(max = 560.dp).then(touchModifier),
            shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh,
            border = androidx.compose.foundation.BorderStroke(1.dp, levelColor(notice.level))) {
            if (compact) Row(Modifier.padding(horizontal = 16.dp).heightIn(min = 40.dp), verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PanelIcon(levelIconRes(notice.level), null, tint = levelColor(notice.level))
                Text(notice.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelLarge)
            }
            else Column(Modifier.padding(start = 18.dp, end = 8.dp, bottom = 10.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    TextButton(onClick = onOpenNotices, modifier = Modifier.weight(1f).heightIn(min = 48.dp)) {
                        PanelIcon(levelIconRes(notice.level), null, tint = levelColor(notice.level))
                        Spacer(Modifier.width(8.dp))
                        Text(notice.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    Text(relativeAge(state.now - banner.receivedAt), style = MaterialTheme.typography.labelMedium)
                    IconButton(onClick = onDismiss, modifier = Modifier.semantics { contentDescription = "Fechar aviso" }) { PanelIcon(R.drawable.ic_close, null) }
                }
                Text(notice.text, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(end = 10.dp).semantics {
                    contentDescription = "${levelLabel(notice.level)}. ${notice.title}. ${notice.text}"
                    liveRegion = LiveRegionMode.Polite
                })
                notice.page?.takeIf { it in pages }?.let { page ->
                    TextButton(onClick = { onNavigate(page) }, modifier = Modifier.heightIn(min = 48.dp)) { Text("Ir para ${pages[page]}") }
                }
                if (state.overflow > 0) TextButton(onClick = onOpenNotices) { Text("+${state.overflow} avisos") }
            }
        }
    }
}

@Composable
internal fun PermissionOverlay(state: LocalIntegrationsState, onShown: (String) -> Unit, onRespond: (String, String) -> Unit) {
    val permission = state.permission ?: return
    val request = permission.request
    val active = permission.stage == PermissionStage.ACTIVE
    val released = permission.shownAt?.let { state.now - it >= 700 } == true && active && state.connected && state.now < permission.expiresAt
    val focus = remember(request.id) { FocusRequester() }
    LaunchedEffect(request.id) { onShown(request.id); focus.requestFocus() }
    BackHandler { if (active) onRespond(request.id, "defer") }
    val remaining = ((permission.expiresAt - state.now).coerceAtLeast(0) + 999) / 1_000
    val initialRemaining = remember(request.id) { (permission.expiresAt - state.now).coerceAtLeast(1) }
    Box(Modifier.fillMaxSize().background(androidx.compose.ui.graphics.Color.Black.copy(alpha = 0.6f))
        .pointerInput(Unit) { awaitPointerEventScope { while (true) awaitPointerEvent(PointerEventPass.Final).changes.forEach { it.consume() } } }
        .semantics { paneTitle = "Claude Code pede permissão" },
        contentAlignment = Alignment.Center) {
        Surface(Modifier.widthIn(max = 640.dp).padding(12.dp), shape = RoundedCornerShape(22.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
            Column(Modifier.heightIn(max = 560.dp).padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                LinearProgressIndicator(progress = { ((permission.expiresAt - state.now).toFloat() / initialRemaining).coerceIn(0f, 1f) },
                    modifier = Modifier.fillMaxWidth().height(4.dp), color = if (remaining <= 10) PanelWarning else MaterialTheme.colorScheme.primary)
                Text("Claude Code pede permissão", style = MaterialTheme.typography.titleLarge,
                    modifier = Modifier.focusRequester(focus).focusable().semantics {
                        heading(); liveRegion = LiveRegionMode.Assertive
                        contentDescription = "Claude Code pede permissão. Projeto ${request.project}. Ferramenta ${request.tool}. ${request.head}. ${if (request.omitted > 0) "Parte do meio omitida. ${request.tail}" else ""} ${incompleteSummaryWarnings(request).joinToString(" ")}"
                    })
                if (permission.stage == PermissionStage.FINISHED || permission.stage == PermissionStage.SENDING) {
                    Text(permission.status ?: "Confira no terminal.", modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
                } else {
                    Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState()),
                        verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Text("${if (state.permissionCount > 1) "1 de ${state.permissionCount} · " else ""}Projeto ${request.project}", maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text("Ferramenta ${request.tool}", style = MaterialTheme.typography.titleMedium)
                        Surface(color = MaterialTheme.colorScheme.surface, shape = RoundedCornerShape(14.dp)) {
                            Column(Modifier.fillMaxWidth().padding(14.dp)) {
                                Text(request.head, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodyLarge)
                                if (request.omitted > 0) {
                                    Text("⋯ ${request.omitted} caracteres omitidos ⋯", style = MaterialTheme.typography.labelMedium)
                                    Text(request.tail, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodyLarge)
                                }
                            }
                        }
                    }
                    incompleteSummaryWarnings(request).forEach { warning ->
                        Text("⚠ $warning", color = PanelWarning, style = MaterialTheme.typography.bodyLarge)
                    }
                    val spokenTime = when { remaining <= 5 -> 5; remaining <= 10 -> 10; remaining <= 30 -> 30; else -> null }
                    Text("Restam ${remaining / 60}:${(remaining % 60).toString().padStart(2, '0')}",
                        style = MaterialTheme.typography.labelLarge.copy(fontFeatureSettings = "tnum"),
                        modifier = Modifier.clearAndSetSemantics {
                            if (spokenTime != null) { contentDescription = "Restam $spokenTime segundos"; liveRegion = LiveRegionMode.Polite }
                        })
                    Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
                        Button(onClick = { onRespond(request.id, "deny") }, enabled = released,
                            modifier = Modifier.weight(1f).heightIn(min = 72.dp).semantics { contentDescription = "Negar este pedido" },
                            colors = ButtonDefaults.buttonColors(containerColor = MaterialTheme.colorScheme.surface, contentColor = MaterialTheme.colorScheme.onSurface)) { Text("Negar") }
                        Button(onClick = { onRespond(request.id, "allow") }, enabled = released,
                            modifier = Modifier.weight(1f).heightIn(min = 72.dp).semantics { contentDescription = "Permitir este pedido" }) { Text("Permitir") }
                    }
                    if (!released) LinearProgressIndicator(progress = { permission.shownAt?.let { ((state.now - it) / 700f).coerceIn(0f, 1f) } ?: 0f },
                        modifier = Modifier.fillMaxWidth().height(2.dp))
                    TextButton(onClick = { onRespond(request.id, "defer") }, enabled = active,
                        modifier = Modifier.align(Alignment.CenterHorizontally).heightIn(min = 48.dp)) { Text("Deixar no PC") }
                }
            }
        }
    }
}

/**
 * Permitir stays available when the summary is incomplete (decision D7); the tablet must say so
 * next to the buttons, in plain text, never hide Negar or "Deixar no PC".
 */
internal fun incompleteSummaryWarnings(request: com.panelos.panelhome.domain.protocol.ClaudePermissionRequest): List<String> = buildList {
    if (request.omitted > 0) add("Texto cortado · ${request.omitted} caracteres omitidos")
    if (request.contentHidden) add("O conteúdo da edição não está visível neste resumo")
}
