package com.panelos.panelhome.ui.desktop

import android.util.Log
import com.panelos.panelhome.domain.video.VideoLogger

internal object DesktopVideoLogger : VideoLogger {
    override fun transition(message: String) {
        Log.i(TAG, "[Video] $message")
    }

    override fun debug(message: String) {
        Log.d(TAG, "[VideoDebug] $message")
    }

    override fun error(stage: String, sequence: Long?, error: Throwable) {
        val details = buildString {
            append("[Video] Operation failed")
            append("\nstage=").append(stage)
            sequence?.let { append("\nsequence=").append(it) }
            append("\ntype=").append(error::class.java.simpleName)
            append("\nmessage=").append(error.message ?: "<no message>")
        }
        Log.e(TAG, details, error)
    }

    private const val TAG = "PanelOS"
}
