package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.video.VideoLogger
import java.util.Locale
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Owns touch gating, session reset, MOVE coalescing, and boundary ordering. */
internal class TouchController(
    scope: CoroutineScope,
    private val sessionState: () -> PanelUiState,
    private val enqueueCommand: (TouchCommand) -> Boolean,
    private val logger: VideoLogger,
) {
    private var touchDebugCount = 0
    private var gateDebugCount = 0
    private var lastMetricsLogNanos = System.nanoTime()
    private val commands = DesktopTouchCommandThrottler(
        scope = scope,
        onSend = { command ->
            enqueueCommand(command)
            logCommand(command)
        },
    )

    fun submit(event: DesktopTouchEvent) {
        val state = sessionState()
        val rejection = when {
            !state.desktopTouchAvailable -> "feature_missing"
            state.connectionStatus != ConnectionStatus.CONNECTED -> "control_disconnected"
            state.mode != PanelMode.DESKTOP || state.desktopStatus != DesktopStatus.RUNNING ->
                "desktop_not_running"
            state.desktopTouchGeneration == null -> "generation_missing"
            event.generation != state.desktopTouchGeneration -> "invalid_generation"
            else -> null
        }
        if (event.action == TouchCommandAction.DOWN && gateDebugCount++ < TOUCH_GATE_DEBUG_LIMIT) {
            logger.debug(
                "[TouchDebug] VIEWMODEL DOWN feature=${state.desktopTouchAvailable} " +
                    "desktopRunning=${state.mode == PanelMode.DESKTOP && state.desktopStatus == DesktopStatus.RUNNING} " +
                    "desktopGeneration=${state.desktopTouchGeneration ?: "missing"} " +
                    "eventGeneration=${event.generation} rejection=${rejection ?: "none"}",
            )
        }
        if (rejection == null) commands.submit(event)
    }

    fun cancelActive() = commands.cancelActive()
    fun reset() = commands.reset()
    fun metrics(): DesktopTouchMetrics = commands.metrics()

    @Synchronized
    private fun logCommand(command: TouchCommand) {
        if (command.action != TouchCommandAction.MOVE && touchDebugCount++ < TOUCH_DEBUG_LIMIT) {
            val coordinates = if (command.x != null && command.y != null) {
                " norm=(%.3f,%.3f)".format(Locale.US, command.x, command.y)
            } else {
                ""
            }
            logger.debug("[TouchDebug] QUEUE ${command.action.name}$coordinates")
        }
        val now = System.nanoTime()
        if (now - lastMetricsLogNanos >= TOUCH_METRICS_LOG_INTERVAL_NANOS) {
            val metrics = commands.metrics()
            logger.debug(
                "[TouchDebug] touchQueued=${metrics.touchQueued} " +
                    "moveCoalesced=${metrics.moveCoalesced}",
            )
            lastMetricsLogNanos = now
        }
    }

    private companion object {
        const val TOUCH_DEBUG_LIMIT = 8
        const val TOUCH_GATE_DEBUG_LIMIT = 8
        const val TOUCH_METRICS_LOG_INTERVAL_NANOS = 5_000_000_000L
    }
}

internal const val DESKTOP_TOUCH_MOVE_INTERVAL_MILLIS = 16L

internal data class DesktopTouchMetrics(
    val touchQueued: Long,
    val moveCoalesced: Long,
)

internal class DesktopTouchCommandThrottler(
    private val scope: CoroutineScope,
    private val intervalMillis: Long = DESKTOP_TOUCH_MOVE_INTERVAL_MILLIS,
    private val wait: suspend (Long) -> Unit = { delay(it) },
    private val onSend: (TouchCommand) -> Unit,
) {
    private val lock = Any()
    private var workerGeneration = 0L
    private var activeGeneration: Long? = null
    private var pendingMove: TouchCommand? = null
    private var worker: Job? = null
    private var touchQueued = 0L
    private var moveCoalesced = 0L

    init {
        require(intervalMillis > 0)
    }

    fun submit(event: DesktopTouchEvent) {
        when (event.action) {
            TouchCommandAction.DOWN -> submitDown(event)
            TouchCommandAction.MOVE -> submitMove(event)
            TouchCommandAction.UP -> submitTerminal(event, TouchCommandAction.UP)
            TouchCommandAction.CANCEL -> submitTerminal(event, TouchCommandAction.CANCEL)
        }
    }

    fun cancelActive() {
        val jobToCancel = synchronized(lock) {
            val generation = activeGeneration ?: return
            workerGeneration++
            activeGeneration = null
            pendingMove = null
            sendLocked(TouchCommand(TouchCommandAction.CANCEL, 0, generation = generation))
            worker.also { worker = null }
        }
        jobToCancel?.cancel()
    }

    fun reset() {
        val jobToCancel = synchronized(lock) {
            workerGeneration++
            activeGeneration = null
            pendingMove = null
            worker.also { worker = null }
        }
        jobToCancel?.cancel()
    }

    fun metrics(): DesktopTouchMetrics = synchronized(lock) {
        DesktopTouchMetrics(touchQueued, moveCoalesced)
    }

    private fun submitDown(event: DesktopTouchEvent) {
        val command = event.toTouchCommand() ?: return
        synchronized(lock) {
            if (activeGeneration != null) return
            activeGeneration = event.generation
            sendLocked(command)
        }
    }

    private fun submitMove(event: DesktopTouchEvent) {
        val command = event.toTouchCommand() ?: return
        synchronized(lock) {
            if (activeGeneration != event.generation) return
            if (pendingMove != null) moveCoalesced++
            pendingMove = command
            if (worker?.isActive != true) {
                val generation = workerGeneration
                worker = scope.launch { drainMoves(generation) }
            }
        }
    }

    private fun submitTerminal(event: DesktopTouchEvent, action: TouchCommandAction) {
        val command = if (action == TouchCommandAction.CANCEL) {
            TouchCommand(action, 0, generation = event.generation)
        } else {
            event.toTouchCommand() ?: return
        }
        val jobToCancel = synchronized(lock) {
            if (activeGeneration != event.generation) return
            workerGeneration++
            activeGeneration = null
            pendingMove = null
            sendLocked(command)
            worker.also { worker = null }
        }
        jobToCancel?.cancel()
    }

    private suspend fun drainMoves(generation: Long) {
        while (true) {
            wait(intervalMillis)
            synchronized(lock) {
                if (generation != workerGeneration || activeGeneration == null) return
                val command = pendingMove ?: run {
                    worker = null
                    return
                }
                pendingMove = null
                sendLocked(command)
            }
        }
    }

    private fun sendLocked(command: TouchCommand) {
        onSend(command)
        touchQueued++
    }

    private fun DesktopTouchEvent.toTouchCommand(): TouchCommand? {
        val normalizedX = x?.takeIf { it.isFinite() }?.coerceIn(0.0, 1.0) ?: return null
        val normalizedY = y?.takeIf { it.isFinite() }?.coerceIn(0.0, 1.0) ?: return null
        return TouchCommand(action, pointerId, normalizedX, normalizedY, generation)
    }
}
