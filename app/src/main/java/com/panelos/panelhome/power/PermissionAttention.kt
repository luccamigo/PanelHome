package com.panelos.panelhome.power

import android.content.Context
import android.media.AudioManager
import android.media.ToneGenerator
import android.os.Handler
import android.os.Looper
import android.os.PowerManager

/** Same short compatibility wake as power-on, only for a negotiated permission. */
internal object PermissionAttention {
    @Suppress("DEPRECATION", "WakelockTimeout")
    fun request(context: Context, sound: Boolean) {
        val power = context.getSystemService(PowerManager::class.java)
        if (!power.isInteractive) {
            val lock = power.newWakeLock(PowerManager.SCREEN_BRIGHT_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP,
                "${context.packageName}:ClaudePermissionWake")
            try { lock.acquire(1_000) } finally { if (lock.isHeld) lock.release() }
        }
        if (sound) runCatching {
            val tone = ToneGenerator(AudioManager.STREAM_NOTIFICATION, 45)
            tone.startTone(ToneGenerator.TONE_PROP_BEEP, 150)
            Handler(Looper.getMainLooper()).postDelayed({ tone.release() }, 200)
        }
    }
}
