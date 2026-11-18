package com.panelos.panelhome.presentation

import android.content.Context
import com.panelos.panelhome.domain.protocol.ProcessSort

internal class MonitoringPreferencesStore(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences("monitoring", Context.MODE_PRIVATE)

    fun read() = MonitoringPreferences(
        computerPage = preferences.getBoolean("computer_page", true),
        processesPage = preferences.getBoolean("processes_page", true),
        windowMinutes = preferences.getInt("window_minutes", 5).takeIf { it in listOf(1, 5, 15) } ?: 5,
        sort = if (preferences.getString("sort", "cpu") == "memory") ProcessSort.MEMORY else ProcessSort.CPU,
        grouped = preferences.getBoolean("grouped", false),
        noticesPage = preferences.getBoolean("notices_page", true),
        noticesOnDesktop = preferences.getBoolean("notices_on_desktop", true),
        permissionSound = preferences.getBoolean("permission_sound", false),
    )

    fun write(value: MonitoringPreferences) {
        preferences.edit().putBoolean("computer_page", value.computerPage)
            .putBoolean("processes_page", value.processesPage).putInt("window_minutes", value.windowMinutes)
            .putString("sort", value.sort.wireValue).putBoolean("grouped", value.grouped).apply()
        preferences.edit().putBoolean("notices_page", value.noticesPage)
            .putBoolean("notices_on_desktop", value.noticesOnDesktop).putBoolean("permission_sound", value.permissionSound).apply()
    }
}
