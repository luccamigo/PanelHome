package com.panelos.panelhome.session

import com.panelos.panelhome.data.transport.UsbFailureKind
import com.panelos.panelhome.domain.model.ConnectionPhase

/**
 * The whole native USB lifecycle, as a pure reducer.
 *
 * Nothing here touches Android, a descriptor, a coroutine or a clock it did not
 * receive, so every transition this panel performs physically can be replayed in
 * a unit test - including the ones that only happen when a cable is pulled at a
 * bad moment. [UsbLifecycleController] is the only thing allowed to run the
 * actions it returns, and it runs them one event at a time.
 *
 * The rule the machine exists to enforce: **a physical detach is not a session
 * failure**. A failed session may be rebuilt on the descriptor it already has,
 * or on a new one. A detach means there is nothing to open, so the only correct
 * response is to release everything and wait - which is precisely what the old
 * lifecycle could not express, and why it reopened an accessory that was not
 * there until the process was killed.
 */
internal class UsbLifecycleStateMachine(
    private val clock: () -> Long = System::currentTimeMillis,
) {
    var state: UsbRecoveryState = UsbRecoveryState.DISABLED
        private set

    /**
     * Bumped for every physical open attempt. Every asynchronous report carries
     * the generation it belongs to, so a late failure from session A can never
     * tear down session B.
     */
    var generation: Long = 0
        private set

    var metrics: UsbRecoveryMetrics = UsbRecoveryMetrics()
        private set

    var accessory: AccessoryIdentity? = null
        private set

    var phase: ConnectionPhase = ConnectionPhase.DISABLED
        private set

    private var closingGeneration: Long? = null
    private var afterClose: AfterClose = AfterClose.WAIT
    private var openAttempt = 0
    private var sessionRecycles = 0
    private var reopenGraceMillis = 0L
    private var permissionRequestedFor: AccessoryIdentity? = null
    private var attachedAtMillis: Long? = null
    private var recoveryStartedAtMillis: Long? = null
    private var lastAttachAcceptedAtMillis = Long.MIN_VALUE

    fun dispatch(event: UsbLifecycleEvent): UsbLifecycleDecision {
        val before = state
        val log = mutableListOf<String>()
        val actions = mutableListOf<UsbLifecycleAction>()
        log += line("event=" + eventName(event) + " state=" + before)
        reduce(event, actions, log)
        if (state != before) {
            log += line("transition=" + before + "->" + state)
        }
        return UsbLifecycleDecision(
            state = state,
            generation = generation,
            actions = actions.toList(),
            logs = log.toList(),
        )
    }

    private fun reduce(
        event: UsbLifecycleEvent,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (state == UsbRecoveryState.STOPPED && event !is UsbLifecycleEvent.SessionClosed) {
            log += line("ignored=service_stopped")
            return
        }
        when (event) {
            is UsbLifecycleEvent.ServiceStarted -> onServiceStarted(event, actions, log)
            is UsbLifecycleEvent.AccessoryObserved -> onAccessoryObserved(event, actions, log)
            is UsbLifecycleEvent.AccessoryAttached -> onAccessoryAttached(event, actions, log)
            is UsbLifecycleEvent.AccessoryDetached -> onAccessoryDetached(event, actions, log)
            is UsbLifecycleEvent.PermissionResult -> onPermissionResult(event, actions, log)
            is UsbLifecycleEvent.OpenSucceeded -> onOpenSucceeded(event, actions, log)
            is UsbLifecycleEvent.OpenFailed -> onOpenFailed(event, actions, log)
            is UsbLifecycleEvent.TransportHandshakeSucceeded ->
                onTransportHandshakeSucceeded(event, actions, log)
            is UsbLifecycleEvent.TransportHandshakeFailed ->
                onTransportHandshakeFailed(event, actions, log)
            is UsbLifecycleEvent.ApplicationHandshakeSucceeded ->
                onApplicationHandshakeSucceeded(event, actions, log)
            is UsbLifecycleEvent.ApplicationHandshakeFailed ->
                onApplicationHandshakeFailed(event, actions, log)
            is UsbLifecycleEvent.PhysicalConnectionLost ->
                onPhysicalConnectionLost(event, actions, log)
            is UsbLifecycleEvent.HostSessionSuperseded -> onHostSessionSuperseded(event, actions, log)
            is UsbLifecycleEvent.SessionClosed -> onSessionClosed(event, actions, log)
            is UsbLifecycleEvent.RetryTimerElapsed -> onRetryTimerElapsed(event, actions, log)
            is UsbLifecycleEvent.HandshakeDeadlineElapsed ->
                onHandshakeDeadlineElapsed(event, actions, log)
            UsbLifecycleEvent.CarrierDisabled -> onCarrierDisabled(actions, log)
            UsbLifecycleEvent.ServiceStopping -> onServiceStopping(actions, log)
        }
    }

    private fun onServiceStarted(
        event: UsbLifecycleEvent.ServiceStarted,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (hasLiveGeneration() || isClosing()) {
            // START_STICKY redelivery and a second startService both land here.
            log += line("ignored=already_owning_a_session")
            return
        }
        val observed = event.accessory
        if (observed == null) {
            accessory = null
            enterWaiting(actions)
            return
        }
        // An accessory that is already attached never produces a broadcast, so
        // the startup scan is the only thing that can find it. Without this the
        // panel sits on ADB/TCP while the host writes into an endpoint that
        // nobody drains - the exact failure that follows an app update.
        onAccessoryObserved(
            UsbLifecycleEvent.AccessoryObserved(observed, event.permitted),
            actions,
            log,
        )
    }

    private fun onAccessoryObserved(
        event: UsbLifecycleEvent.AccessoryObserved,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (hasLiveGeneration() || isClosing()) {
            // An observation is not evidence of a new AOA transition, so unlike
            // an attach it must never recycle a session that is already working.
            log += line("ignored=session_already_live")
            return
        }
        accessory = event.accessory
        if (!event.permitted) {
            requestPermission(event.accessory, actions, log)
            return
        }
        beginOpen(actions, log)
    }

    private fun onAccessoryAttached(
        event: UsbLifecycleEvent.AccessoryAttached,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        val now = clock()
        if (state == UsbRecoveryState.OPENING_ACCESSORY) {
            // The broadcast and the Activity intent both reach the Service. One
            // physical attach must still produce one open.
            log += line("ignored=open_already_in_flight")
            return
        }
        if (event.accessory == accessory &&
            now - lastAttachAcceptedAtMillis < ATTACH_DEBOUNCE_MILLIS
        ) {
            log += line("ignored=duplicate_attach_within_debounce")
            return
        }
        lastAttachAcceptedAtMillis = now
        attachedAtMillis = now
        metrics = metrics.copy(usbPhysicalAttaches = metrics.usbPhysicalAttaches + 1)
        openAttempt = 0
        sessionRecycles = 0
        if (event.accessory != accessory) permissionRequestedFor = null
        accessory = event.accessory
        if (!event.permitted) {
            requestPermission(event.accessory, actions, log)
            return
        }
        if (isClosing()) {
            // Rapid replug: the previous generation is still being released.
            // Close-before-open is not negotiable, so the attach only changes
            // where the teardown lands.
            afterClose = AfterClose.REOPEN
            log += line("attach=deferred_until_close_completes")
            return
        }
        if (hasLiveGeneration()) {
            // ATTACHED means Android re-enumerated into accessory mode. The
            // descriptor from before that transition is dead by definition, so
            // this is a recycle even though no detach was observed.
            recycle("accessory_reattached_while_live", AfterClose.REOPEN, actions, log)
            return
        }
        beginOpen(actions, log)
    }

    private fun onAccessoryDetached(
        event: UsbLifecycleEvent.AccessoryDetached,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        val current = accessory
        if (event.accessory != null && current != null && event.accessory != current) {
            metrics = metrics.copy(
                usbStaleGenerationEventsIgnored = metrics.usbStaleGenerationEventsIgnored + 1,
            )
            log += line("ignored=detach_for_another_accessory accessory=" + event.accessory)
            return
        }
        // A detach with no accessory extra is still authoritative: the only way
        // to be wrong is to keep a descriptor that no longer has a bus.
        metrics = metrics.copy(usbPhysicalDetaches = metrics.usbPhysicalDetaches + 1)
        accessory = null
        permissionRequestedFor = null
        openAttempt = 0
        sessionRecycles = 0
        attachedAtMillis = null
        recoveryStartedAtMillis = clock()
        actions += UsbLifecycleAction.CancelTimers
        if (isClosing()) {
            // A teardown is already running. Redirecting it is the whole change:
            // whatever it was going to reopen, it now has nothing to reopen.
            afterClose = AfterClose.WAIT
            log += line("detach=redirected_pending_close")
            return
        }
        if (hasLiveGeneration()) {
            closeCurrent(
                reason = "physical_detach",
                then = AfterClose.WAIT,
                via = UsbRecoveryState.CLOSING_ACCESSORY,
                actions = actions,
            )
            return
        }
        enterWaiting(actions)
    }

    private fun onPermissionResult(
        event: UsbLifecycleEvent.PermissionResult,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!event.granted) {
            metrics = metrics.copy(usbPermissionDenials = metrics.usbPermissionDenials + 1)
            log += line("permission=denied accessory=" + event.accessory)
            permissionRequestedFor = null
            if (!hasLiveGeneration() && !isClosing()) enterWaiting(actions)
            return
        }
        log += line("permission=granted accessory=" + event.accessory)
        permissionRequestedFor = null
        if (hasLiveGeneration() || isClosing()) {
            log += line("ignored=session_already_live")
            return
        }
        accessory = event.accessory
        beginOpen(actions, log)
    }

    private fun onOpenSucceeded(
        event: UsbLifecycleEvent.OpenSucceeded,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        if (state != UsbRecoveryState.OPENING_ACCESSORY) {
            log += line("ignored=open_result_outside_opening")
            return
        }
        metrics = metrics.copy(
            usbAccessoryOpenSuccesses = metrics.usbAccessoryOpenSuccesses + 1,
        )
        openAttempt = 0
        log += line("openAccessory=success")
        state = UsbRecoveryState.TRANSPORT_HANDSHAKING
        actions += UsbLifecycleAction.ArmHandshakeDeadline(
            generation = generation,
            timeoutMillis = UsbLifecycleTimings.TRANSPORT_HANDSHAKE_TIMEOUT_MILLIS,
        )
        publish(ConnectionPhase.TRANSPORT_HANDSHAKE, actions)
    }

    private fun onOpenFailed(
        event: UsbLifecycleEvent.OpenFailed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        metrics = metrics.copy(usbAccessoryOpenFailures = metrics.usbAccessoryOpenFailures + 1)
        log += line("openAccessory=failed kind=" + event.kind + " detail=" + event.detail)
        if (accessory == null) {
            // The detach won the race. There is nothing left to open.
            enterWaiting(actions)
            return
        }
        if (openAttempt >= UsbLifecycleTimings.MAX_CONSECUTIVE_OPEN_ATTEMPTS) {
            // The burst is over and the accessory still refuses. It is not
            // necessarily stale: a read blocked on the descriptor we just
            // released can keep `openAccessory` refusing for a minute or more on
            // this hardware. Waiting for an attach that has already happened
            // would strand the panel, so it drops to one attempt every 30 s.
            metrics = metrics.copy(usbRecoveryFailures = metrics.usbRecoveryFailures + 1)
            openAttempt = 0
            state = UsbRecoveryState.BACKOFF
            actions += UsbLifecycleAction.ScheduleRetry(
                generation = generation,
                attempt = 0,
                delayMillis = UsbLifecycleTimings.IDLE_REPROBE_MILLIS,
            )
            log += line(
                "openAccessory=idle_reprobe in=" +
                    UsbLifecycleTimings.IDLE_REPROBE_MILLIS + "ms",
            )
            publish(ConnectionPhase.WAITING_FOR_USB, actions)
            return
        }
        val delay = UsbLifecycleTimings.backoffFor(openAttempt)
        state = UsbRecoveryState.BACKOFF
        actions += UsbLifecycleAction.ScheduleRetry(generation, openAttempt, delay)
        log += line("backoff=" + delay + "ms attempt=" + openAttempt)
        publish(ConnectionPhase.OPENING_USB, actions)
    }

    private fun onTransportHandshakeSucceeded(
        event: UsbLifecycleEvent.TransportHandshakeSucceeded,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        log += line("plmxSession=" + event.sessionPrefix)
        state = UsbRecoveryState.APPLICATION_HANDSHAKING
        actions += UsbLifecycleAction.ArmHandshakeDeadline(
            generation = generation,
            timeoutMillis = UsbLifecycleTimings.APPLICATION_HANDSHAKE_TIMEOUT_MILLIS,
        )
        publish(ConnectionPhase.APPLICATION_HANDSHAKE, actions)
    }

    private fun onTransportHandshakeFailed(
        event: UsbLifecycleEvent.TransportHandshakeFailed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        if (event.kind == UsbFailureKind.HANDSHAKE_TIMEOUT) {
            metrics = metrics.copy(usbHandshakeTimeouts = metrics.usbHandshakeTimeouts + 1)
        }
        log += line("plmxHandshake=failed kind=" + event.kind + " detail=" + event.detail)
        recycle("transport_handshake_failed", AfterClose.REOPEN, actions, log)
    }

    private fun onApplicationHandshakeSucceeded(
        event: UsbLifecycleEvent.ApplicationHandshakeSucceeded,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        val now = clock()
        val attachToConnected = attachedAtMillis?.let { now - it }
        val recoveryDuration = recoveryStartedAtMillis?.let { now - it }
        metrics = metrics.copy(
            usbRecoverySuccesses = metrics.usbRecoverySuccesses + 1,
            attachToConnectedMillis = attachToConnected ?: metrics.attachToConnectedMillis,
            lastRecoveryDurationMillis = recoveryDuration ?: metrics.lastRecoveryDurationMillis,
        )
        recoveryStartedAtMillis = null
        openAttempt = 0
        sessionRecycles = 0
        log += line(
            "panelHandshake=success attachToConnectedMs=" + (attachToConnected ?: "-") +
                " recoveryMs=" + (recoveryDuration ?: "-"),
        )
        state = UsbRecoveryState.CONNECTED
        actions += UsbLifecycleAction.CancelTimers
        publish(ConnectionPhase.CONNECTED, actions)
    }

    private fun onApplicationHandshakeFailed(
        event: UsbLifecycleEvent.ApplicationHandshakeFailed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        if (state != UsbRecoveryState.CONNECTED &&
            state != UsbRecoveryState.APPLICATION_HANDSHAKING
        ) {
            log += line("ignored=application_failure_outside_session")
            return
        }
        // The carrier is still up: PLMX did not fail, only the JSONL session
        // did. That is what a host restart looks like from here, and the
        // control connection's own retry loop is what fixes it. Tearing the
        // descriptor down would turn a two-second reconnect into a physical
        // recovery that needs a cable event nobody is going to produce.
        log += line("panelHandshake=lost detail=" + event.detail + " carrier=preserved")
        state = UsbRecoveryState.APPLICATION_HANDSHAKING
        actions += UsbLifecycleAction.ArmHandshakeDeadline(
            generation = generation,
            timeoutMillis = UsbLifecycleTimings.APPLICATION_HANDSHAKE_TIMEOUT_MILLIS,
        )
        publish(ConnectionPhase.APPLICATION_HANDSHAKE, actions)
    }

    private fun onPhysicalConnectionLost(
        event: UsbLifecycleEvent.PhysicalConnectionLost,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        log += line("physicalSession=lost kind=" + event.kind + " detail=" + event.detail)
        // A mid-frame loss is what a cable pull looks like from inside a session,
        // and Android takes about a second to confirm it with a DETACHED
        // broadcast. Reopening inside that window means opening an accessory the
        // kernel is tearing down; the grace is there to lose that race on
        // purpose.
        recycle(
            reason = "physical_session_lost",
            then = AfterClose.REOPEN,
            actions = actions,
            log = log,
            graceMillis = UsbLifecycleTimings.DETACH_GRACE_MILLIS,
        )
    }

    private fun onHostSessionSuperseded(
        event: UsbLifecycleEvent.HostSessionSuperseded,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        metrics = metrics.copy(usbSessionSupersedes = metrics.usbSessionSupersedes + 1)
        // The host restarted with the cable in place. PLMX adopts the newest
        // HELLO on the descriptor it already has; closing it here would be the
        // one thing that breaks a reconnect which otherwise costs nothing.
        log += line("plmxSession=" + event.sessionPrefix + " superseded=host_restart carrier=preserved")
        state = UsbRecoveryState.APPLICATION_HANDSHAKING
        actions += UsbLifecycleAction.ArmHandshakeDeadline(
            generation = generation,
            timeoutMillis = UsbLifecycleTimings.APPLICATION_HANDSHAKE_TIMEOUT_MILLIS,
        )
        publish(ConnectionPhase.APPLICATION_HANDSHAKE, actions)
    }

    private fun onSessionClosed(
        event: UsbLifecycleEvent.SessionClosed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (event.generation != closingGeneration) {
            metrics = metrics.copy(
                usbStaleGenerationEventsIgnored = metrics.usbStaleGenerationEventsIgnored + 1,
            )
            log += line("ignored=stale_close generation=" + event.generation)
            return
        }
        closingGeneration = null
        metrics = metrics.copy(usbDescriptorCloses = metrics.usbDescriptorCloses + 1)
        log += line("descriptorClose=success")
        when (afterClose) {
            AfterClose.WAIT -> enterWaiting(actions)
            AfterClose.REOPEN -> reopenAfterRecycle(actions, log)
            AfterClose.STOP -> {
                state = UsbRecoveryState.STOPPED
                publish(ConnectionPhase.DISCONNECTED, actions)
            }
            AfterClose.DISABLE -> {
                state = UsbRecoveryState.DISABLED
                publish(ConnectionPhase.DISABLED, actions)
            }
        }
    }

    /**
     * A replug has to reconnect immediately, so the first few recycles reopen at
     * once. After that the panel is almost certainly sitting next to a host that
     * is not running, and every further reopen is one more cycle on the
     * descriptor for nothing - so the reopen is paced instead.
     */
    private fun reopenAfterRecycle(
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (accessory == null) {
            // A detach landed during the teardown. Reopening now would call
            // openAccessory against nothing.
            log += line("reopen=cancelled reason=accessory_gone")
            enterWaiting(actions)
            return
        }
        val paced = if (sessionRecycles <= UsbLifecycleTimings.IMMEDIATE_SESSION_RECYCLES) {
            0L
        } else {
            UsbLifecycleTimings.backoffFor(
                sessionRecycles - UsbLifecycleTimings.IMMEDIATE_SESSION_RECYCLES,
            )
        }
        val delay = maxOf(paced, reopenGraceMillis)
        if (delay == 0L) {
            beginOpen(actions, log)
            return
        }
        state = UsbRecoveryState.BACKOFF
        actions += UsbLifecycleAction.ScheduleRetry(generation, sessionRecycles, delay)
        log += line(
            "reopen=deferred in=" + delay + "ms recycles=" + sessionRecycles +
                " grace=" + reopenGraceMillis + "ms",
        )
        publish(ConnectionPhase.RECOVERING, actions)
    }

    private fun onRetryTimerElapsed(
        event: UsbLifecycleEvent.RetryTimerElapsed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (state != UsbRecoveryState.BACKOFF || event.generation != generation) {
            metrics = metrics.copy(
                usbStaleGenerationEventsIgnored = metrics.usbStaleGenerationEventsIgnored + 1,
            )
            log += line("ignored=stale_retry generation=" + event.generation)
            return
        }
        if (accessory == null) {
            enterWaiting(actions)
            return
        }
        reopenGraceMillis = 0L
        beginOpen(actions, log)
    }

    private fun onHandshakeDeadlineElapsed(
        event: UsbLifecycleEvent.HandshakeDeadlineElapsed,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        if (!isCurrent(event.generation, log)) return
        if (state != UsbRecoveryState.TRANSPORT_HANDSHAKING &&
            state != UsbRecoveryState.APPLICATION_HANDSHAKING
        ) {
            log += line("ignored=deadline_outside_handshake")
            return
        }
        metrics = metrics.copy(usbHandshakeTimeouts = metrics.usbHandshakeTimeouts + 1)
        log += line("handshake=deadline_elapsed state=" + state)
        recycle("handshake_deadline", AfterClose.REOPEN, actions, log)
    }

    private fun onCarrierDisabled(
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        accessory = null
        openAttempt = 0
        actions += UsbLifecycleAction.CancelTimers
        if (isClosing()) {
            afterClose = AfterClose.DISABLE
            log += line("carrier=disabled_pending_close")
            return
        }
        if (hasLiveGeneration()) {
            closeCurrent(
                reason = "carrier_disabled",
                then = AfterClose.DISABLE,
                via = UsbRecoveryState.CLOSING_ACCESSORY,
                actions = actions,
            )
            return
        }
        state = UsbRecoveryState.DISABLED
        publish(ConnectionPhase.DISABLED, actions)
    }

    private fun onServiceStopping(
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        actions += UsbLifecycleAction.CancelTimers
        if (isClosing()) {
            afterClose = AfterClose.STOP
            log += line("stop=pending_close")
            return
        }
        if (hasLiveGeneration()) {
            closeCurrent(
                reason = "service_stopping",
                then = AfterClose.STOP,
                via = UsbRecoveryState.CLOSING_ACCESSORY,
                actions = actions,
            )
            return
        }
        state = UsbRecoveryState.STOPPED
        publish(ConnectionPhase.DISCONNECTED, actions)
    }

    private fun requestPermission(
        target: AccessoryIdentity,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        state = UsbRecoveryState.WAITING_FOR_PERMISSION
        publish(ConnectionPhase.WAITING_FOR_USB, actions)
        if (permissionRequestedFor == target) {
            // One dialog per accessory. A second request would stack another
            // system dialog on top of the panel's own home screen.
            log += line("permission=already_requested accessory=" + target)
            return
        }
        permissionRequestedFor = target
        metrics = metrics.copy(usbPermissionRequests = metrics.usbPermissionRequests + 1)
        log += line("permission=requesting accessory=" + target)
        actions += UsbLifecycleAction.RequestPermission(target)
    }

    private fun beginOpen(
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
    ) {
        val target = accessory
        if (target == null) {
            enterWaiting(actions)
            return
        }
        generation += 1
        openAttempt += 1
        metrics = metrics.copy(
            usbAccessoryOpenAttempts = metrics.usbAccessoryOpenAttempts + 1,
        )
        state = UsbRecoveryState.OPENING_ACCESSORY
        log += line("openAccessory=begin attempt=" + openAttempt + " accessory=" + target)
        actions += UsbLifecycleAction.CancelTimers
        actions += UsbLifecycleAction.OpenAccessory(generation, target)
        publish(ConnectionPhase.OPENING_USB, actions)
    }

    private fun recycle(
        reason: String,
        then: AfterClose,
        actions: MutableList<UsbLifecycleAction>,
        log: MutableList<String>,
        graceMillis: Long = 0L,
    ) {
        metrics = metrics.copy(usbRecoveryAttempts = metrics.usbRecoveryAttempts + 1)
        sessionRecycles += 1
        reopenGraceMillis = graceMillis
        if (recoveryStartedAtMillis == null) recoveryStartedAtMillis = clock()
        actions += UsbLifecycleAction.CancelTimers
        log += line("recycle=" + reason)
        closeCurrent(reason, then, UsbRecoveryState.RECOVERING_SESSION, actions)
    }

    private fun closeCurrent(
        reason: String,
        then: AfterClose,
        via: UsbRecoveryState,
        actions: MutableList<UsbLifecycleAction>,
    ) {
        afterClose = then
        closingGeneration = generation
        state = via
        actions += UsbLifecycleAction.CloseSession(generation, reason)
        publish(
            if (then == AfterClose.WAIT) ConnectionPhase.WAITING_FOR_USB else ConnectionPhase.RECOVERING,
            actions,
        )
    }

    private fun enterWaiting(actions: MutableList<UsbLifecycleAction>) {
        state = UsbRecoveryState.WAITING_FOR_ACCESSORY
        actions += UsbLifecycleAction.CancelTimers
        publish(ConnectionPhase.WAITING_FOR_USB, actions)
    }

    private fun publish(next: ConnectionPhase, actions: MutableList<UsbLifecycleAction>) {
        if (phase == next) return
        phase = next
        actions += UsbLifecycleAction.PublishPhase(next)
    }

    private fun isCurrent(eventGeneration: Long, log: MutableList<String>): Boolean {
        if (eventGeneration == generation && hasLiveGeneration()) return true
        metrics = metrics.copy(
            usbStaleGenerationEventsIgnored = metrics.usbStaleGenerationEventsIgnored + 1,
        )
        // Two different things are being refused here and the log has to say
        // which: a report from a session that has been replaced, and a report
        // that is current but arrived after its session already started dying.
        val reason = if (eventGeneration != generation) "superseded_generation" else "state=" + state
        log += line(
            "ignored=" + reason + " generation=" + eventGeneration + " current=" + generation,
        )
        return false
    }

    private fun hasLiveGeneration(): Boolean = state in LIVE_STATES

    /**
     * True while a teardown this machine asked for has not reported back.
     *
     * Nothing may open in that window. Every event that would normally start a
     * session instead rewrites where the teardown lands, which is what keeps
     * close-before-open true even when the cable is pulled and replaced faster
     * than a descriptor can be released.
     */
    private fun isClosing(): Boolean = closingGeneration != null

    private fun line(body: String): String =
        "[UsbRecovery] generation=" + generation + " " + body

    private enum class AfterClose { WAIT, REOPEN, STOP, DISABLE }

    private companion object {
        /**
         * Two independent sources report one physical attach: the broadcast
         * receiver and the Activity intent the same attach starts. Anything
         * inside this window is the second copy of the first attach.
         */
        const val ATTACH_DEBOUNCE_MILLIS = 750L

        val LIVE_STATES = setOf(
            UsbRecoveryState.OPENING_ACCESSORY,
            UsbRecoveryState.TRANSPORT_HANDSHAKING,
            UsbRecoveryState.APPLICATION_HANDSHAKING,
            UsbRecoveryState.CONNECTED,
        )

        fun eventName(event: UsbLifecycleEvent): String = event::class.java.simpleName
    }
}

internal data class UsbLifecycleDecision(
    val state: UsbRecoveryState,
    val generation: Long,
    val actions: List<UsbLifecycleAction>,
    val logs: List<String>,
)
