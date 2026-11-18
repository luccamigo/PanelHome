package com.panelos.panelhome.power

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class PowerBehaviorControllerTest {
    @Test
    fun defaultsMatchAppliancePolicy() {
        assertEquals(
            PowerSettings(
                wakeOnPower = true,
                keepScreenOnWhilePowered = true,
                sleepAfterPowerLoss = PowerOffDelay.THIRTY_SECONDS,
            ),
            PowerSettings(),
        )
        assertEquals(
            PowerOffDelay.THIRTY_SECONDS,
            PowerOffDelay.fromStorageValue("unknown"),
        )
    }

    @Test
    fun startupAlreadyPoweredWakesAndKeepsScreenOn() {
        val fixture = Fixture()

        fixture.controller.initialize(ExternalPowerState.Powered)

        assertTrue(fixture.actions.keepScreenOnState)
        assertTrue(fixture.actions.turnScreenOnState)
        assertEquals(1, fixture.actions.wakeRequests)
        assertEquals(0, fixture.scheduler.activeTasks.size)
    }

    @Test
    fun startupOnBatteryClearsKeepScreenOnAndSchedulesDefaultDelay() {
        val fixture = Fixture()

        fixture.controller.initialize(ExternalPowerState.OnBattery)

        assertFalse(fixture.actions.keepScreenOnState)
        assertFalse(fixture.actions.turnScreenOnState)
        assertEquals(listOf(30_000L), fixture.scheduler.activeTasks.map { it.delayMillis })
    }

    @Test
    fun connectCancelsBatteryCountdownAndAppliesPoweredPolicy() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val batteryTask = fixture.scheduler.tasks.single()

        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)

        assertTrue(batteryTask.cancelled)
        assertTrue(fixture.actions.keepScreenOnState)
        assertTrue(fixture.actions.turnScreenOnState)
        assertEquals(1, fixture.actions.wakeRequests)
        assertTrue(
            fixture.actions.logs.contains(
                "[Power] External power restored; sleep cancelled",
            ),
        )
    }

    @Test
    fun disconnectClearsKeepScreenOnImmediatelyAndStartsCountdown() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.Powered)

        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)

        assertFalse(fixture.actions.keepScreenOnState)
        assertFalse(fixture.actions.turnScreenOnState)
        assertEquals(30_000L, fixture.scheduler.activeTasks.single().delayMillis)
    }

    @Test
    fun reconnectCancelsCountdownAndCancelledCallbackCannotSleep() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val oldTask = fixture.scheduler.tasks.single()

        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)
        oldTask.fireEvenIfCancelled()

        assertEquals(0, fixture.actions.sleepRequests)
        assertTrue(fixture.actions.keepScreenOnState)
    }

    @Test
    fun countdownExpiryLocksWhenDeviceAdminIsActive() {
        val fixture = Fixture()
        fixture.actions.sleepResult = SleepRequestResult.REQUESTED
        fixture.controller.initialize(ExternalPowerState.OnBattery)

        fixture.scheduler.activeTasks.single().fire()

        assertEquals(1, fixture.actions.sleepRequests)
        assertTrue(fixture.actions.logs.contains("[Power] Sleep requested"))
    }

    @Test
    fun countdownExpiryDoesNotFailWhenDeviceAdminIsInactive() {
        val fixture = Fixture()
        fixture.actions.sleepResult = SleepRequestResult.DEVICE_ADMIN_INACTIVE
        fixture.controller.initialize(ExternalPowerState.OnBattery)

        fixture.scheduler.activeTasks.single().fire()

        assertEquals(1, fixture.actions.sleepRequests)
        assertTrue(
            fixture.actions.logs.contains(
                "[Power] Force sleep unavailable; Device Admin is not active",
            ),
        )
    }

    @Test
    fun neverDoesNotScheduleSleep() {
        val fixture = Fixture(
            settings = PowerSettings(sleepAfterPowerLoss = PowerOffDelay.NEVER),
        )

        fixture.controller.initialize(ExternalPowerState.OnBattery)

        assertTrue(fixture.scheduler.activeTasks.isEmpty())
        assertEquals(0, fixture.actions.sleepRequests)
    }

    @Test
    fun changingDelayDuringCountdownCancelsOldTaskAndStartsNewDelay() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val oldTask = fixture.scheduler.tasks.single()

        fixture.controller.updateSettings(
            PowerSettings(sleepAfterPowerLoss = PowerOffDelay.TEN_SECONDS),
        )

        val newTask = fixture.scheduler.activeTasks.single()
        assertTrue(oldTask.cancelled)
        assertEquals(10_000L, newTask.delayMillis)
        oldTask.fireEvenIfCancelled()
        assertEquals(0, fixture.actions.sleepRequests)
        newTask.fire()
        assertEquals(1, fixture.actions.sleepRequests)
    }

    @Test
    fun changingDelayToNeverCancelsActiveCountdown() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val oldTask = fixture.scheduler.tasks.single()

        fixture.controller.updateSettings(
            PowerSettings(sleepAfterPowerLoss = PowerOffDelay.NEVER),
        )
        oldTask.fireEvenIfCancelled()

        assertTrue(oldTask.cancelled)
        assertTrue(fixture.scheduler.activeTasks.isEmpty())
        assertEquals(0, fixture.actions.sleepRequests)
    }

    @Test
    fun repeatedConnectAndDisconnectSignalsDoNotDuplicateWork() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.Powered)
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)
        val firstBatteryTask = fixture.scheduler.tasks.single()
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)
        val secondBatteryTask = fixture.scheduler.activeTasks.single()

        firstBatteryTask.fireEvenIfCancelled()
        secondBatteryTask.fire()

        assertEquals(2, fixture.actions.wakeRequests)
        assertEquals(2, fixture.scheduler.tasks.size)
        assertEquals(1, fixture.actions.sleepRequests)
    }

    @Test
    fun activityRecreationReappliesFlagsWithoutRestartingCountdown() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val countdown = fixture.scheduler.activeTasks.single()
        val scheduledCount = fixture.scheduler.tasks.size

        fixture.controller.onActivityAttached()

        assertEquals(scheduledCount, fixture.scheduler.tasks.size)
        assertSame(countdown, fixture.scheduler.activeTasks.single())
        assertFalse(fixture.actions.keepScreenOnState)
        assertFalse(fixture.actions.turnScreenOnState)
        countdown.fire()
        assertEquals(1, fixture.actions.sleepRequests)
    }

    @Test
    fun staleGenerationCannotSleepDuringLaterBatteryCountdown() {
        val fixture = Fixture()
        fixture.controller.initialize(ExternalPowerState.OnBattery)
        val firstCountdown = fixture.scheduler.tasks.single()
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)
        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)
        val currentCountdown = fixture.scheduler.activeTasks.single()

        firstCountdown.fireEvenIfCancelled()
        assertEquals(0, fixture.actions.sleepRequests)
        currentCountdown.fire()

        assertEquals(1, fixture.actions.sleepRequests)
    }

    @Test
    fun keepScreenOnAlwaysTracksPoweredStateAndSetting() {
        val fixture = Fixture(
            settings = PowerSettings(keepScreenOnWhilePowered = false),
        )
        fixture.controller.initialize(ExternalPowerState.Powered)
        assertFalse(fixture.actions.keepScreenOnState)

        fixture.controller.updateSettings(PowerSettings(keepScreenOnWhilePowered = true))
        assertTrue(fixture.actions.keepScreenOnState)

        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.OnBattery)
        assertFalse(fixture.actions.keepScreenOnState)

        fixture.controller.onExternalPowerStateChanged(ExternalPowerState.Powered)
        assertTrue(fixture.actions.keepScreenOnState)
    }

    private class Fixture(settings: PowerSettings = PowerSettings()) {
        val scheduler = FakePowerScheduler()
        val actions = FakePowerActions()
        val controller = PowerBehaviorController(settings, scheduler, actions)
    }

    private class FakePowerScheduler : PowerScheduler {
        val tasks = mutableListOf<FakeScheduledTask>()
        val activeTasks: List<FakeScheduledTask>
            get() = tasks.filter { !it.cancelled && !it.fired }

        override fun schedule(
            delayMillis: Long,
            callback: () -> Unit,
        ): ScheduledPowerTask = FakeScheduledTask(delayMillis, callback).also(tasks::add)
    }

    private class FakeScheduledTask(
        val delayMillis: Long,
        private val callback: () -> Unit,
    ) : ScheduledPowerTask {
        var cancelled = false
            private set
        var fired = false
            private set

        override fun cancel() {
            cancelled = true
        }

        fun fire() {
            if (cancelled || fired) return
            fired = true
            callback()
        }

        fun fireEvenIfCancelled() {
            if (fired) return
            fired = true
            callback()
        }
    }

    private class FakePowerActions : PowerBehaviorActions {
        var keepScreenOnState = false
        var turnScreenOnState = false
        var wakeRequests = 0
        var sleepRequests = 0
        var sleepResult = SleepRequestResult.REQUESTED
        val logs = mutableListOf<String>()

        override fun setKeepScreenOn(enabled: Boolean) {
            keepScreenOnState = enabled
        }

        override fun setTurnScreenOn(enabled: Boolean) {
            turnScreenOnState = enabled
        }

        override fun requestWake() {
            wakeRequests++
        }

        override fun requestSleep(): SleepRequestResult {
            sleepRequests++
            return sleepResult
        }

        override fun log(message: String) {
            logs += message
        }
    }
}
