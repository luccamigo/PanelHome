package com.panelos.panelhome.power

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import android.os.Build

interface PowerStateMonitor {
    fun start(): ExternalPowerState

    fun stop()
}

class AndroidPowerStateMonitor(
    context: Context,
    private val onPowerStateChanged: (ExternalPowerState) -> Unit,
) : PowerStateMonitor {
    private val appContext = context.applicationContext
    private var started = false
    private val receiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                Intent.ACTION_POWER_CONNECTED ->
                    onPowerStateChanged(ExternalPowerState.Powered)
                Intent.ACTION_POWER_DISCONNECTED ->
                    onPowerStateChanged(ExternalPowerState.OnBattery)
            }
        }
    }

    override fun start(): ExternalPowerState {
        if (!started) {
            val filter = IntentFilter().apply {
                addAction(Intent.ACTION_POWER_CONNECTED)
                addAction(Intent.ACTION_POWER_DISCONNECTED)
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                appContext.registerReceiver(receiver, filter, Context.RECEIVER_NOT_EXPORTED)
            } else {
                @Suppress("DEPRECATION", "UnspecifiedRegisterReceiverFlag")
                appContext.registerReceiver(receiver, filter)
            }
            started = true
        }

        val battery = appContext.registerReceiver(
            null,
            IntentFilter(Intent.ACTION_BATTERY_CHANGED),
        )
        val plugged = battery?.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) ?: 0
        return if (plugged != 0) {
            ExternalPowerState.Powered
        } else {
            ExternalPowerState.OnBattery
        }
    }

    override fun stop() {
        if (!started) return
        appContext.unregisterReceiver(receiver)
        started = false
    }
}
