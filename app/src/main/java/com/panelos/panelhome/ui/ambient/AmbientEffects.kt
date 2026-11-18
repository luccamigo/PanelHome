package com.panelos.panelhome.ui.ambient

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive

@Composable
internal fun ApplyAmbientWindowBrightness() {
    val context = LocalContext.current
    val window = remember(context) { context.findActivity()?.window } ?: return

    DisposableEffect(window) {
        val previousBrightness = window.attributes.screenBrightness
        window.attributes = window.attributes.apply {
            screenBrightness = AMBIENT_WINDOW_BRIGHTNESS
        }

        onDispose {
            window.attributes = window.attributes.apply {
                screenBrightness = previousBrightness
            }
        }
    }
}

@Composable
internal fun rememberAmbientBurnInOffset(): AmbientBurnInOffset {
    var step by remember { mutableIntStateOf(0) }

    LaunchedEffect(Unit) {
        while (isActive) {
            delay(AMBIENT_BURN_IN_INTERVAL_MILLIS)
            step = if (step == Int.MAX_VALUE) 0 else step + 1
        }
    }

    return ambientBurnInOffsetForStep(step)
}

private fun Context.findActivity(): Activity? {
    var current = this
    while (current is ContextWrapper) {
        if (current is Activity) return current
        val baseContext = current.baseContext
        if (baseContext === current) return null
        current = baseContext
    }
    return current as? Activity
}
