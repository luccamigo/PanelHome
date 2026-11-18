package com.panelos.panelhome.ui.ambient

import android.text.format.DateFormat
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalConfiguration
import com.panelos.panelhome.ui.components.PanelDateTime
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone

@Composable
internal fun rememberAmbientDateTime(currentTimeMillis: Long): PanelDateTime {
    val configuration = LocalConfiguration.current
    val locale = remember(configuration) {
        configuration.locales.takeUnless { it.isEmpty }?.get(0) ?: Locale.getDefault()
    }
    val timeZoneId = TimeZone.getDefault().id
    val datePattern = remember(locale) {
        DateFormat.getBestDateTimePattern(locale, "EEEE d MMMM")
    }
    val timeFormatter = remember(locale, timeZoneId) {
        SimpleDateFormat("HH:mm", locale).apply {
            timeZone = TimeZone.getTimeZone(timeZoneId)
        }
    }
    val dateFormatter = remember(locale, timeZoneId, datePattern) {
        SimpleDateFormat(datePattern, locale).apply {
            timeZone = TimeZone.getTimeZone(timeZoneId)
        }
    }

    return remember(currentTimeMillis, timeFormatter, dateFormatter) {
        val date = Date(currentTimeMillis)
        PanelDateTime(
            time = timeFormatter.format(date),
            date = dateFormatter.format(date),
        )
    }
}
