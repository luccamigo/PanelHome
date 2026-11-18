package com.panelos.panelhome.ui.loopmedia

import android.content.Context
import android.widget.VideoView
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Test
import org.junit.runner.RunWith

/**
 * The real VideoView, before any surface or prepare: hiding the page must clear
 * the play target that start() recorded, or playback begins once prepared.
 */
@RunWith(AndroidJUnit4::class)
class LoopVideoViewTest {
    @Test
    fun hidingBeforePrepareCancelsThePendingStart() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            val context = ApplicationProvider.getApplicationContext<Context>()
            val player = LoopVideoView(context, File(context.cacheDir, "never-opened.mp4")) {}
            // mTargetState is VideoView's own record of what it will do once prepared.
            val target = VideoView::class.java.getDeclaredField("mTargetState").apply { isAccessible = true }
            try {
                player.video.pause()
                val paused = target.getInt(player.video)

                player.setPlaying(true)
                assertFalse(player.video.isPlaying)
                assertNotEquals(paused, target.getInt(player.video))

                player.setPlaying(false)
                assertEquals(paused, target.getInt(player.video))
            } finally {
                player.release()
            }
        }
    }
}
