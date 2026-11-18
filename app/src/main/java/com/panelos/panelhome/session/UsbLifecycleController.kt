package com.panelos.panelhome.session

import com.panelos.panelhome.data.transport.UsbFailureKind
import com.panelos.panelhome.domain.model.ConnectionPhase
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** Outcome of the one call that is allowed to open `/dev/usb_accessory`. */
internal sealed interface UsbOpenResult {
    data object Opened : UsbOpenResult
    data class Failed(val kind: UsbFailureKind, val detail: String) : UsbOpenResult
}

/**
 * Everything the lifecycle needs from Android and from the session runtime.
 *
 * Keeping it an interface is what lets the state machine be exercised against
 * cable pulls that never happen on this bench, and it is also the seam that
 * guarantees only one component ever calls `openAccessory`.
 */
internal interface UsbLifecyclePort {
    /**
     * Opens the descriptor and builds the session on it. Returns as soon as the
     * carrier exists: the PLMX handshake reports itself through the transport
     * listener, because that is the part that can block.
     */
    suspend fun openAccessory(generation: Long, accessory: AccessoryIdentity): UsbOpenResult

    /**
     * Releases the whole generation: coordinator, logical channels, transport,
     * descriptor. Must never block on the wire - after a cable pull a write to
     * the gadget does not return.
     */
    suspend fun closeSession(generation: Long, reason: String)

    fun requestPermission(accessory: AccessoryIdentity)

    fun publishPhase(phase: ConnectionPhase)

    fun log(message: String)
}

/**
 * The single serialized owner of the native USB lifecycle.
 *
 * Before this existed, four things changed USB state independently and none of
 * them knew about the others: the broadcast receiver, `onStartCommand`, the
 * runtime's recovery timer and the transport's own reconnect. A cable pull could
 * therefore be handled twice, or handled while a teardown was still running, and
 * the descriptor that survived that race was the reason a reconnect needed a new
 * process.
 *
 * Every event goes through one channel, one reducer and one action executor, in
 * order. Nothing else may open or close an accessory.
 */
internal class UsbLifecycleController(
    private val scope: CoroutineScope,
    private val port: UsbLifecyclePort,
    private val machine: UsbLifecycleStateMachine = UsbLifecycleStateMachine(),
) {
    private val events = Channel<UsbLifecycleEvent>(Channel.UNLIMITED)
    private var timer: Job? = null
    private var loop: Job? = null

    val state: UsbRecoveryState get() = machine.state
    val generation: Long get() = machine.generation
    val metrics: UsbRecoveryMetrics get() = machine.metrics

    fun start() {
        if (loop != null) return
        loop = scope.launch {
            for (event in events) {
                handle(event)
            }
        }
    }

    /**
     * Callable from any thread, including a `BroadcastReceiver` on the main
     * thread. It only queues: nothing that can block runs on the caller.
     */
    fun submit(event: UsbLifecycleEvent) {
        events.trySend(event)
    }

    fun shutdown() {
        submit(UsbLifecycleEvent.ServiceStopping)
        events.close()
    }

    private suspend fun handle(event: UsbLifecycleEvent) {
        val decision = machine.dispatch(event)
        decision.logs.forEach(port::log)
        decision.actions.forEach { execute(it) }
    }

    private suspend fun execute(action: UsbLifecycleAction) {
        when (action) {
            is UsbLifecycleAction.CancelTimers -> cancelTimer()

            is UsbLifecycleAction.ScheduleRetry -> {
                cancelTimer()
                timer = scope.launch {
                    delay(action.delayMillis)
                    submit(
                        UsbLifecycleEvent.RetryTimerElapsed(action.generation, action.attempt),
                    )
                }
            }

            is UsbLifecycleAction.ArmHandshakeDeadline -> {
                cancelTimer()
                timer = scope.launch {
                    delay(action.timeoutMillis)
                    submit(UsbLifecycleEvent.HandshakeDeadlineElapsed(action.generation))
                }
            }

            is UsbLifecycleAction.RequestPermission -> port.requestPermission(action.accessory)

            is UsbLifecycleAction.PublishPhase -> port.publishPhase(action.phase)

            is UsbLifecycleAction.OpenAccessory -> {
                // Suspending inside the loop is the point: no second open can
                // start while this one is outstanding.
                val result = port.openAccessory(action.generation, action.accessory)
                submit(
                    when (result) {
                        is UsbOpenResult.Opened ->
                            UsbLifecycleEvent.OpenSucceeded(action.generation)
                        is UsbOpenResult.Failed ->
                            UsbLifecycleEvent.OpenFailed(
                                generation = action.generation,
                                kind = result.kind,
                                detail = result.detail,
                            )
                    },
                )
            }

            is UsbLifecycleAction.CloseSession -> {
                port.log(
                    "[UsbRecovery] generation=" + action.generation +
                        " descriptorClose=begin reason=" + action.reason,
                )
                port.closeSession(action.generation, action.reason)
                submit(UsbLifecycleEvent.SessionClosed(action.generation))
            }
        }
    }

    private fun cancelTimer() {
        timer?.cancel()
        timer = null
    }
}
