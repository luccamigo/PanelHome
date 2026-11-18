package com.panelos.panelhome.power

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class PowerSettingsStore(context: Context) : AutoCloseable {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE,
    )
    private val _settings = MutableStateFlow(readSettings())
    val settings: StateFlow<PowerSettings> = _settings.asStateFlow()
    var onSettingsChanged: ((PowerSettings) -> Unit)? = null

    private val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key ->
        if (key !in POWER_KEYS) return@OnSharedPreferenceChangeListener
        val updated = readSettings()
        if (_settings.value != updated) {
            _settings.value = updated
            onSettingsChanged?.invoke(updated)
        }
    }

    init {
        preferences.registerOnSharedPreferenceChangeListener(listener)
    }

    fun setWakeOnPower(enabled: Boolean) {
        preferences.edit().putBoolean(KEY_WAKE_ON_POWER, enabled).apply()
    }

    fun setKeepScreenOnWhilePowered(enabled: Boolean) {
        preferences.edit().putBoolean(KEY_KEEP_SCREEN_ON, enabled).apply()
    }

    fun setSleepAfterPowerLoss(delay: PowerOffDelay) {
        preferences.edit().putString(KEY_SLEEP_DELAY, delay.storageValue).apply()
    }

    override fun close() {
        onSettingsChanged = null
        preferences.unregisterOnSharedPreferenceChangeListener(listener)
    }

    private fun readSettings(): PowerSettings = PowerSettings(
        wakeOnPower = preferences.getBoolean(KEY_WAKE_ON_POWER, true),
        keepScreenOnWhilePowered = preferences.getBoolean(KEY_KEEP_SCREEN_ON, true),
        sleepAfterPowerLoss = PowerOffDelay.fromStorageValue(
            preferences.getString(KEY_SLEEP_DELAY, PowerOffDelay.THIRTY_SECONDS.storageValue),
        ),
    )

    private companion object {
        const val PREFERENCES_NAME = "power_behavior"
        const val KEY_WAKE_ON_POWER = "wake_on_power"
        const val KEY_KEEP_SCREEN_ON = "keep_screen_on_while_powered"
        const val KEY_SLEEP_DELAY = "sleep_after_power_loss"
        val POWER_KEYS = setOf(KEY_WAKE_ON_POWER, KEY_KEEP_SCREEN_ON, KEY_SLEEP_DELAY)
    }
}
