package com.panelos.panelhome.power

enum class ExternalPowerState {
    Powered,
    OnBattery,
}

enum class PowerOffDelay(
    val storageValue: String,
    val durationMillis: Long?,
    internal val logValue: String,
) {
    TEN_SECONDS("10_seconds", 10_000L, "10s"),
    THIRTY_SECONDS("30_seconds", 30_000L, "30s"),
    SIXTY_SECONDS("60_seconds", 60_000L, "60s"),
    TWO_MINUTES("2_minutes", 120_000L, "2m"),
    FIVE_MINUTES("5_minutes", 300_000L, "5m"),
    NEVER("never", null, "never");

    companion object {
        fun fromStorageValue(value: String?): PowerOffDelay =
            entries.firstOrNull { it.storageValue == value } ?: THIRTY_SECONDS
    }
}

data class PowerSettings(
    val wakeOnPower: Boolean = true,
    val keepScreenOnWhilePowered: Boolean = true,
    val sleepAfterPowerLoss: PowerOffDelay = PowerOffDelay.THIRTY_SECONDS,
)

fun interface ScheduledPowerTask {
    fun cancel()
}

fun interface PowerScheduler {
    fun schedule(delayMillis: Long, callback: () -> Unit): ScheduledPowerTask
}

enum class SleepRequestResult {
    REQUESTED,
    DEVICE_ADMIN_INACTIVE,
    FAILED,
}

interface PowerBehaviorActions {
    fun setKeepScreenOn(enabled: Boolean)

    fun setTurnScreenOn(enabled: Boolean)

    fun requestWake()

    fun requestSleep(): SleepRequestResult

    fun log(message: String)
}

/**
 * Pure state machine for appliance-like power behavior.
 *
 * The controller is retained across Activity recreation by [PowerBehaviorViewModel]. Every
 * cancellation advances [scheduleGeneration], so a callback already queued by the platform can
 * never lock the device after external power has returned or the delay has changed.
 */
class PowerBehaviorController(
    initialSettings: PowerSettings,
    private val scheduler: PowerScheduler,
    private val actions: PowerBehaviorActions,
) {
    private var settings = initialSettings
    private var externalPowerState: ExternalPowerState? = null
    private var scheduledSleep: ScheduledPowerTask? = null
    private var scheduleGeneration = 0L
    private var appliedKeepScreenOn: Boolean? = null
    private var appliedTurnScreenOn: Boolean? = null

    @Synchronized
    fun initialize(initialPowerState: ExternalPowerState) {
        if (externalPowerState != null) return
        applyExternalPowerState(initialPowerState)
    }

    @Synchronized
    fun onExternalPowerStateChanged(newState: ExternalPowerState) {
        if (externalPowerState == newState) return
        applyExternalPowerState(newState)
    }

    @Synchronized
    fun updateSettings(newSettings: PowerSettings) {
        if (settings == newSettings) return
        val previousDelay = settings.sleepAfterPowerLoss
        settings = newSettings

        when (externalPowerState) {
            ExternalPowerState.Powered -> applyPoweredScreenPolicy()
            ExternalPowerState.OnBattery -> {
                applyScreenPolicy(keepScreenOn = false, turnScreenOn = false)
                if (previousDelay != newSettings.sleepAfterPowerLoss) {
                    cancelScheduledSleep()
                    scheduleSleepIfNeeded()
                }
            }
            null -> Unit
        }
    }

    /** Reapplies window-owned flags without changing or restarting the active countdown. */
    @Synchronized
    fun onActivityAttached() {
        val powered = externalPowerState == ExternalPowerState.Powered
        actions.setKeepScreenOn(powered && settings.keepScreenOnWhilePowered)
        actions.setTurnScreenOn(powered && settings.wakeOnPower)
    }

    @Synchronized
    fun close() {
        cancelScheduledSleep()
        applyScreenPolicy(keepScreenOn = false, turnScreenOn = false)
        externalPowerState = null
    }

    private fun applyExternalPowerState(newState: ExternalPowerState) {
        val previousState = externalPowerState
        externalPowerState = newState

        when (newState) {
            ExternalPowerState.Powered -> {
                actions.log("[Power] External power connected")
                val sleepWasPending = cancelScheduledSleep()
                if (previousState == ExternalPowerState.OnBattery && sleepWasPending) {
                    actions.log("[Power] External power restored; sleep cancelled")
                }
                applyPoweredScreenPolicy()
                if (settings.wakeOnPower) {
                    actions.requestWake()
                    actions.log("[Power] Wake requested")
                }
            }

            ExternalPowerState.OnBattery -> {
                actions.log("[Power] External power disconnected")
                cancelScheduledSleep()
                applyScreenPolicy(keepScreenOn = false, turnScreenOn = false)
                scheduleSleepIfNeeded()
            }
        }
    }

    private fun applyPoweredScreenPolicy() {
        applyScreenPolicy(
            keepScreenOn = settings.keepScreenOnWhilePowered,
            turnScreenOn = settings.wakeOnPower,
        )
    }

    private fun applyScreenPolicy(keepScreenOn: Boolean, turnScreenOn: Boolean) {
        if (appliedKeepScreenOn != keepScreenOn) {
            actions.setKeepScreenOn(keepScreenOn)
            appliedKeepScreenOn = keepScreenOn
            if (keepScreenOn) {
                actions.log("[Power] Screen keep-awake enabled")
            }
        }
        if (appliedTurnScreenOn != turnScreenOn) {
            actions.setTurnScreenOn(turnScreenOn)
            appliedTurnScreenOn = turnScreenOn
        }
    }

    private fun scheduleSleepIfNeeded() {
        val delay = settings.sleepAfterPowerLoss
        val durationMillis = delay.durationMillis ?: return
        val generation = ++scheduleGeneration
        scheduledSleep = scheduler.schedule(durationMillis) {
            onSleepCountdownExpired(generation)
        }
        actions.log("[Power] Sleep scheduled in ${delay.logValue}")
    }

    @Synchronized
    private fun onSleepCountdownExpired(generation: Long) {
        if (generation != scheduleGeneration ||
            externalPowerState != ExternalPowerState.OnBattery
        ) {
            return
        }

        scheduledSleep = null
        scheduleGeneration++
        when (actions.requestSleep()) {
            SleepRequestResult.REQUESTED -> actions.log("[Power] Sleep requested")
            SleepRequestResult.DEVICE_ADMIN_INACTIVE ->
                actions.log("[Power] Force sleep unavailable; Device Admin is not active")
            SleepRequestResult.FAILED ->
                actions.log("[Power] Sleep request failed")
        }
    }

    private fun cancelScheduledSleep(): Boolean {
        scheduleGeneration++
        val task = scheduledSleep ?: return false
        scheduledSleep = null
        task.cancel()
        return true
    }
}
