package com.panelos.panelhome.ui

import android.view.Window
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat

/**
 * Full-screen immersive mode: the status and navigation bars are hidden and a
 * swipe from the edge shows them for a moment, then they go away on their own.
 *
 * Call it from onCreate, onResume and whenever the window regains focus: the
 * system shows the bars again after a permission dialog or the keyboard, and
 * the hidden state is not remembered across them.
 */
fun Window.enterImmersiveMode() {
    val controller = WindowCompat.getInsetsController(this, decorView)
    controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
    controller.hide(WindowInsetsCompat.Type.systemBars())
}
