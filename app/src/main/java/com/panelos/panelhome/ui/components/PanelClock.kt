package com.panelos.panelhome.ui.components

import android.text.format.DateFormat
import androidx.compose.runtime.Composable
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import java.text.SimpleDateFormat
import java.util.Date
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

data class PanelDateTime(
    val time: String,
    val date: String,
    /** "quarta-feira": the first short line next to the clock in the top bar. */
    val weekday: String = "",
    /** "18 de novembro": the second short line. */
    val dayMonth: String = "",
)

@Composable
fun rememberCurrentTimeMillis(): Long = produceState(
    initialValue = System.currentTimeMillis(),
) {
    while (isActive) {
        val now = System.currentTimeMillis()
        value = now
        delay(1_000L - now % 1_000L)
    }
}.value

@Composable
fun rememberPanelDateTime(currentTimeMillis: Long): PanelDateTime {
    val context = LocalContext.current
    val configuration = LocalConfiguration.current
    val timeFormatter = remember(context, configuration) {
        DateFormat.getTimeFormat(context)
    }
    val dateFormatter = remember(context, configuration) {
        DateFormat.getLongDateFormat(context)
    }
    return remember(currentTimeMillis, timeFormatter, dateFormatter) {
        val date = Date(currentTimeMillis)
        val locale = configuration.locales[0]
        fun pattern(skeleton: String) = SimpleDateFormat(DateFormat.getBestDateTimePattern(locale, skeleton), locale).format(date)
        PanelDateTime(
            time = timeFormatter.format(date),
            date = dateFormatter.format(date),
            weekday = pattern("EEEE"),
            dayMonth = pattern("dMMMM"),
        )
    }
}
