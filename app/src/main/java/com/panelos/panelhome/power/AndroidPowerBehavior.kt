package com.panelos.panelhome.power

import android.app.Activity
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.util.Log
import android.view.WindowManager

class MainThreadPowerScheduler(
    private val handler: Handler = Handler(Looper.getMainLooper()),
) : PowerScheduler {
    override fun schedule(delayMillis: Long, callback: () -> Unit): ScheduledPowerTask {
        val runnable = Runnable(callback)
        handler.postDelayed(runnable, delayMillis)
        return ScheduledPowerTask { handler.removeCallbacks(runnable) }
    }
}

class AndroidPowerBehaviorActions(context: Context) : PowerBehaviorActions {
    private val appContext = context.applicationContext
    private val devicePolicyManager =
        appContext.getSystemService(DevicePolicyManager::class.java)
    private val adminComponent = ComponentName(appContext, PanelDeviceAdminReceiver::class.java)
    private var activityEffects: ActivityPowerEffects? = null
    private var keepScreenOn = false
    private var turnScreenOn = false
    private var wakePending = false

    @Synchronized
    fun attach(activity: Activity) {
        activityEffects = ActivityPowerEffects(activity)
        if (wakePending && turnScreenOn) {
            wakePending = false
            activityEffects?.requestWake()
        }
    }

    @Synchronized
    fun detach(activity: Activity) {
        if (activityEffects?.belongsTo(activity) == true) {
            activityEffects = null
        }
    }

    @Synchronized
    override fun setKeepScreenOn(enabled: Boolean) {
        keepScreenOn = enabled
        activityEffects?.setKeepScreenOn(enabled)
    }

    @Synchronized
    override fun setTurnScreenOn(enabled: Boolean) {
        turnScreenOn = enabled
        if (!enabled) wakePending = false
        activityEffects?.setTurnScreenOn(enabled)
    }

    @Synchronized
    override fun requestWake() {
        val effects = activityEffects
        if (effects == null) {
            wakePending = true
        } else {
            effects.requestWake()
        }
    }

    override fun requestSleep(): SleepRequestResult {
        if (!isDeviceAdminActive()) return SleepRequestResult.DEVICE_ADMIN_INACTIVE
        return try {
            devicePolicyManager.lockNow()
            SleepRequestResult.REQUESTED
        } catch (error: RuntimeException) {
            Log.e(LOG_TAG, "[Power] DevicePolicyManager.lockNow() failed", error)
            SleepRequestResult.FAILED
        }
    }

    fun isDeviceAdminActive(): Boolean = devicePolicyManager.isAdminActive(adminComponent)

    override fun log(message: String) {
        Log.i(LOG_TAG, message)
    }

    private companion object {
        const val LOG_TAG = "PanelHome"
    }
}

private class ActivityPowerEffects(private val activity: Activity) {
    fun belongsTo(candidate: Activity): Boolean = activity === candidate

    fun setKeepScreenOn(enabled: Boolean) {
        if (enabled) {
            activity.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        } else {
            activity.window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        }
    }

    fun setTurnScreenOn(enabled: Boolean) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            activity.setTurnScreenOn(enabled)
        } else {
            setLegacyTurnScreenOn(enabled)
        }
    }

    fun requestWake() {
        setTurnScreenOn(true)
        requestFallbackWakeIfNeeded()
    }

    @Suppress("DEPRECATION")
    private fun setLegacyTurnScreenOn(enabled: Boolean) {
        // FLAG_TURN_SCREEN_ON is the window-level compatibility API for minSdk 24-26.
        if (enabled) {
            activity.window.addFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        } else {
            activity.window.clearFlags(WindowManager.LayoutParams.FLAG_TURN_SCREEN_ON)
        }
    }

    @Suppress("DEPRECATION", "WakelockTimeout")
    private fun requestFallbackWakeIfNeeded() {
        val powerManager = activity.getSystemService(PowerManager::class.java)
        if (powerManager.isInteractive) return

        // SCREEN_BRIGHT_WAKE_LOCK is deprecated, but API 24-26 has no
        // Activity.setTurnScreenOn. It is also the minimal fallback when an already-stopped
        // Activity or vendor build does not wake promptly from setTurnScreenOn alone.
        // Acquire-and-release is deliberately temporary; FLAG_KEEP_SCREEN_ON owns steady state.
        val wakeLock = powerManager.newWakeLock(
            PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP,
            "${activity.packageName}:PowerConnectedWake",
        )
        try {
            wakeLock.acquire(LEGACY_WAKE_TIMEOUT_MILLIS)
        } finally {
            if (wakeLock.isHeld) wakeLock.release()
        }
    }

    private companion object {
        const val LEGACY_WAKE_TIMEOUT_MILLIS = 1_000L
    }
}
