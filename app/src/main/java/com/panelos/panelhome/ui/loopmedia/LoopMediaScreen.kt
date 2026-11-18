package com.panelos.panelhome.ui.loopmedia

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.ImageDecoder
import android.graphics.Movie
import android.graphics.drawable.AnimatedImageDrawable
import android.media.AudioManager
import android.os.Build
import android.os.SystemClock
import android.view.Gravity
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.VideoView
import androidx.annotation.RequiresApi
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.protocol.LoopMediaKind
import com.panelos.panelhome.presentation.LoopMediaPhase
import com.panelos.panelhome.presentation.LoopMediaUiState
import java.io.File
import com.panelos.panelhome.ui.components.StatusBanner
import com.panelos.panelhome.ui.components.BannerAction
import com.panelos.panelhome.ui.components.BannerTone
import kotlin.math.min

/**
 * The full-screen media page: black background, scale-to-fit, endless loop and
 * no sound. It plays only while it is the pager's current page and the
 * Activity is resumed; otherwise it is paused.
 */
@Composable
fun LoopMediaScreen(
    state: LoopMediaUiState,
    isCurrentPage: Boolean,
    modifier: Modifier = Modifier,
    onReturnToPanel: () -> Unit = {},
) {
    val lifecycleState by LocalLifecycleOwner.current.lifecycle.currentStateAsState()
    val playing = isCurrentPage && lifecycleState.isAtLeast(Lifecycle.State.RESUMED)
    val file = state.file
    var playbackFailed by remember(file) { mutableStateOf(false) }

    Box(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black),
        contentAlignment = Alignment.Center,
    ) {
        when {
            state.phase == LoopMediaPhase.DOWNLOADING -> LoopMediaMessage(
                stringResource(R.string.loop_media_receiving, state.progressPercent),
            )
            state.phase != LoopMediaPhase.READY || file == null || playbackFailed ->
                StatusBanner(
                    title = "Não foi possível exibir esta mídia",
                    message = "O arquivo pode estar corrompido ou ser grande demais. Escolha outro no Control Center.",
                    tone = BannerTone.Error,
                    primary = BannerAction("Voltar ao painel", onReturnToPanel),
                    modifier = Modifier.padding(24.dp),
                )
            else -> key(file) {
                AndroidView(
                    factory = { context ->
                        createLoopPlayer(context, file, state.kind) { playbackFailed = true }
                            ?: View(context).also { playbackFailed = true }
                    },
                    update = { view -> (view as? LoopPlayer)?.setPlaying(playing) },
                    onRelease = { view -> (view as? LoopPlayer)?.release() },
                    modifier = Modifier.fillMaxSize(),
                )
            }
        }
    }
}

@Composable
private fun LoopMediaMessage(text: String) {
    Text(
        text = text,
        color = Color.White,
        style = MaterialTheme.typography.titleMedium,
        textAlign = TextAlign.Center,
        modifier = Modifier.padding(24.dp),
    )
}

/** What the page drives on a player view: pause/resume and final release. */
internal interface LoopPlayer {
    fun setPlaying(playing: Boolean)
    fun release()
}

/** Returns null when the file cannot be decoded. */
private fun createLoopPlayer(
    context: Context,
    file: File,
    kind: LoopMediaKind?,
    onError: () -> Unit,
): View? = when (kind) {
    LoopMediaKind.VIDEO -> LoopVideoView(context, file, onError)
    LoopMediaKind.GIF -> if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
        runCatching { AnimatedGifView(context, file) }.getOrNull()
    } else {
        MovieGifView.create(context, file)
    }
    null -> null
}

/** API 28+: the platform decoder, forced to loop forever whatever the GIF declares. */
@RequiresApi(Build.VERSION_CODES.P)
@SuppressLint("ViewConstructor")
private class AnimatedGifView(context: Context, file: File) : ImageView(context), LoopPlayer {
    private val animated: AnimatedImageDrawable?

    init {
        val drawable = ImageDecoder.decodeDrawable(ImageDecoder.createSource(file))
        animated = (drawable as? AnimatedImageDrawable)?.apply {
            repeatCount = AnimatedImageDrawable.REPEAT_INFINITE
        }
        scaleType = ScaleType.FIT_CENTER
        setImageDrawable(drawable)
    }

    override fun setPlaying(playing: Boolean) {
        val drawable = animated ?: return
        if (playing && !drawable.isRunning) drawable.start()
        if (!playing && drawable.isRunning) drawable.stop()
    }

    override fun release() {
        animated?.stop()
        setImageDrawable(null)
    }
}

/** API 24-27: android.graphics.Movie, looped by taking the time modulo its duration. */
@Suppress("DEPRECATION")
@SuppressLint("ViewConstructor")
private class MovieGifView private constructor(
    context: Context,
    private val movie: Movie,
) : View(context), LoopPlayer {
    private var playing = false
    private var positionMillis = 0L
    private var startedAt = 0L

    init {
        // Movie cannot draw on a hardware canvas.
        setLayerType(LAYER_TYPE_SOFTWARE, null)
    }

    override fun setPlaying(playing: Boolean) {
        if (playing == this.playing) return
        this.playing = playing
        if (playing) startedAt = SystemClock.uptimeMillis() - positionMillis
        invalidate()
    }

    override fun release() {
        playing = false
    }

    override fun onDraw(canvas: Canvas) {
        val duration = movie.duration().coerceAtLeast(1)
        if (playing) positionMillis = (SystemClock.uptimeMillis() - startedAt) % duration
        movie.setTime(positionMillis.toInt())
        val movieWidth = movie.width().coerceAtLeast(1)
        val movieHeight = movie.height().coerceAtLeast(1)
        val scale = min(width / movieWidth.toFloat(), height / movieHeight.toFloat())
        canvas.save()
        canvas.translate((width - movieWidth * scale) / 2f, (height - movieHeight * scale) / 2f)
        canvas.scale(scale, scale)
        movie.draw(canvas, 0f, 0f)
        canvas.restore()
        if (playing) postInvalidateOnAnimation()
    }

    companion object {
        fun create(context: Context, file: File): MovieGifView? =
            Movie.decodeFile(file.absolutePath)
                ?.takeIf { it.width() > 0 && it.height() > 0 }
                ?.let { MovieGifView(context, it) }
    }
}

/**
 * Muted, looping VideoView. VideoView already keeps the aspect ratio inside
 * the space it is given; the FrameLayout centres it on the black page.
 */
@SuppressLint("ViewConstructor")
internal class LoopVideoView(
    context: Context,
    file: File,
    onError: () -> Unit,
) : FrameLayout(context), LoopPlayer {
    internal val video = VideoView(context)

    init {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            // A muted loop must not pause whatever the user is listening to.
            video.setAudioFocusRequest(AudioManager.AUDIOFOCUS_NONE)
        }
        video.setOnPreparedListener { player ->
            player.isLooping = true
            player.setVolume(0f, 0f)
        }
        video.setOnErrorListener { _, _, _ ->
            onError()
            true
        }
        video.setVideoPath(file.absolutePath)
        addView(video, LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT, Gravity.CENTER))
    }

    override fun setPlaying(playing: Boolean) {
        if (playing && !video.isPlaying) video.start()
        // Unconditional: before prepare isPlaying is false, but start() already
        // set the play target; only pause() clears it.
        if (!playing) video.pause()
    }

    override fun release() {
        video.stopPlayback()
    }
}
