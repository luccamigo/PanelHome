package com.panelos.panelhome.ui.monitoring

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp

internal const val HOLD_TO_TERMINATE_MILLIS = 1_200L
internal val HOLD_CANCEL_DISTANCE = 8.dp

/**
 * The hold itself is the lock: 1.2 s of continuous contact, cancelled when the finger or the
 * pointer moves more than 8 dp (the gesture then belongs to the list or the pager) or when a
 * parent consumes it. Nothing happens on a plain tap.
 */
@Composable
internal fun HoldToTerminateButton(name: String, sending: Boolean = false, onConfirmed: () -> Unit) {
    val haptics = LocalHapticFeedback.current
    var holding by remember { mutableStateOf(false) }
    val fill = remember { Animatable(0f) }
    val latestConfirmed by rememberUpdatedState(onConfirmed)
    LaunchedEffect(holding, sending) {
        when {
            sending -> fill.snapTo(1f)
            holding -> fill.animateTo(1f, tween(HOLD_TO_TERMINATE_MILLIS.toInt(), easing = LinearEasing))
            else -> fill.animateTo(0f, tween(200, easing = LinearEasing))
        }
    }
    val bad = MaterialTheme.colorScheme.error
    Box(
        Modifier.fillMaxWidth().widthIn(min = 300.dp).heightIn(min = 64.dp).clip(RoundedCornerShape(18.dp))
            .background(bad.copy(alpha = 0.15f))
            .semantics {
                role = Role.Button
                contentDescription = if (sending) "Encerrando $name" else "Segure para encerrar $name"
            }
            .pointerInput(sending) {
                if (sending) return@pointerInput
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    holding = true
                    val limit = HOLD_CANCEL_DISTANCE.toPx()
                    // Null means the full second and a fifth went by with the finger still down.
                    val interrupted = withTimeoutOrNull(HOLD_TO_TERMINATE_MILLIS) {
                        var interrupted = false
                        while (!interrupted) {
                            val change = awaitPointerEvent().changes.firstOrNull { it.id == down.id }
                            interrupted = change == null || !change.pressed || change.isConsumed ||
                                (change.position - down.position).getDistance() > limit
                        }
                        true
                    }
                    if (interrupted == null) {
                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                        latestConfirmed()
                    }
                    holding = false
                    if (interrupted == null) {
                        // Swallow the rest of this contact so it cannot also trigger a tap.
                        do {
                            val event = awaitPointerEvent()
                            event.changes.forEach { it.consume() }
                        } while (event.changes.any { it.pressed })
                    }
                }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(Modifier.fillMaxHeight().fillMaxWidth(fill.value).background(bad.copy(alpha = if (sending) 0.35f else 0.85f)))
        Row(Modifier.fillMaxWidth().padding(horizontal = 20.dp), horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically) {
            if (sending) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Color.White)
            if (sending) Spacer(Modifier.width(12.dp))
            Text(
                when {
                    sending -> "Encerrando…"
                    holding -> "Continue segurando…"
                    else -> "Segure para encerrar"
                },
                style = MaterialTheme.typography.titleMedium,
                color = if (holding || sending) Color.White else bad,
            )
        }
    }
}
