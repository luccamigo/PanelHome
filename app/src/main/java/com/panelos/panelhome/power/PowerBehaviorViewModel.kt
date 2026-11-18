package com.panelos.panelhome.power

import android.app.Activity
import android.app.Application
import android.app.admin.DevicePolicyManager
import android.content.ComponentName
import android.content.Intent
import androidx.lifecycle.AndroidViewModel
import com.panelos.panelhome.R
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

class PowerBehaviorViewModel(application: Application) : AndroidViewModel(application) {
    private val settingsStore = PowerSettingsStore(application)
    private val actions = AndroidPowerBehaviorActions(application)
    private val controller = PowerBehaviorController(
        initialSettings = settingsStore.settings.value,
        scheduler = MainThreadPowerScheduler(),
        actions = actions,
    )
    private val monitor = AndroidPowerStateMonitor(
        context = application,
        onPowerStateChanged = controller::onExternalPowerStateChanged,
    )
    private val _deviceAdminActive = MutableStateFlow(actions.isDeviceAdminActive())

    val settings: StateFlow<PowerSettings> = settingsStore.settings
    val deviceAdminActive: StateFlow<Boolean> = _deviceAdminActive.asStateFlow()

    init {
        settingsStore.onSettingsChanged = controller::updateSettings
        controller.initialize(monitor.start())
    }

    fun attach(activity: Activity) {
        actions.attach(activity)
        controller.onActivityAttached()
    }

    fun detach(activity: Activity) {
        actions.detach(activity)
    }

    fun refreshDeviceAdminState() {
        _deviceAdminActive.value = actions.isDeviceAdminActive()
    }

    fun requestDeviceAdmin(activity: Activity) {
        val admin = ComponentName(activity, PanelDeviceAdminReceiver::class.java)
        val intent = Intent(DevicePolicyManager.ACTION_ADD_DEVICE_ADMIN).apply {
            putExtra(DevicePolicyManager.EXTRA_DEVICE_ADMIN, admin)
            putExtra(
                DevicePolicyManager.EXTRA_ADD_EXPLANATION,
                activity.getString(R.string.power_admin_explanation),
            )
        }
        activity.startActivity(intent)
    }

    fun setWakeOnPower(enabled: Boolean) = settingsStore.setWakeOnPower(enabled)

    fun setKeepScreenOnWhilePowered(enabled: Boolean) =
        settingsStore.setKeepScreenOnWhilePowered(enabled)

    fun setSleepAfterPowerLoss(delay: PowerOffDelay) =
        settingsStore.setSleepAfterPowerLoss(delay)

    override fun onCleared() {
        monitor.stop()
        settingsStore.close()
        controller.close()
        super.onCleared()
    }
}
