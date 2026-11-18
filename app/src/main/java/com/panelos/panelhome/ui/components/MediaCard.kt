package com.panelos.panelhome.ui.components

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.model.MediaState
import com.panelos.panelhome.domain.model.VolumeState
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelHairline
import com.panelos.panelhome.ui.theme.PanelOnAccent
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** True when something is playing or paused on the PC: only then does the big media card exist. */
internal fun hasMedia(media: MediaState): Boolean = media.available && media.track != null

/**
 * The big media card (only while there is media): artwork, title, artist,
 * source, progress, transport and volume. Vector icons only; the play button
 * is the one cyan circle.
 */
@Composable
internal fun MediaCard(
    media: MediaState,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    volume: VolumeState,
    volumeEnabled: Boolean,
    onVolumeChange: (Int) -> Unit,
    onVolumeChangeFinished: (Int) -> Unit,
    onToggleVolumeMute: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val track = media.track
    val shape = RoundedCornerShape(PanelRadius.banner)
    Column(
        modifier = modifier
            .clip(shape)
            .background(PanelSurface, shape)
            .border(1.dp, PanelHairline, shape)
            .padding(24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(20.dp), verticalAlignment = Alignment.CenterVertically) {
            AlbumArtwork(media = media, size = 168.dp)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(
                    text = track?.title?.ifBlank { null } ?: stringResource(R.string.media_unknown_title),
                    style = PanelType.cardTitle, color = PanelText, maxLines = 2, overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = track?.artist?.ifBlank { null } ?: stringResource(R.string.media_unknown_artist),
                    style = PanelType.body, color = PanelTextMuted, maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
                track?.sourceApp?.takeIf { it.isNotBlank() }?.let { source ->
                    Text(
                        text = source,
                        style = PanelType.caption.copy(fontWeight = FontWeight.SemiBold),
                        color = PanelText,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier
                            .border(1.dp, PanelOutline, RoundedCornerShape(PanelRadius.pill))
                            .padding(horizontal = 12.dp, vertical = 4.dp),
                    )
                }
            }
        }
        MediaProgress(media)
        Box(Modifier.weight(1f, fill = true))
        MediaControls(media, onPrevious, onPlayPause, onNext, Modifier.fillMaxWidth())
        Box(Modifier.weight(1f, fill = true))
        VolumeControl(volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute)
    }
}

/**
 * What the media slot shows when nothing plays. [compact] is the 72 dp line at
 * the bottom of the Painel (the FPS card is above it); otherwise it is the
 * larger card that takes the FPS row when the PC offers no FPS.
 */
@Composable
internal fun IdleMediaCard(
    volume: VolumeState,
    volumeEnabled: Boolean,
    onVolumeChange: (Int) -> Unit,
    onVolumeChangeFinished: (Int) -> Unit,
    onToggleVolumeMute: () -> Unit,
    modifier: Modifier = Modifier,
    compact: Boolean = true,
) {
    val shape = RoundedCornerShape(PanelRadius.card)
    val frame = modifier.clip(shape).background(PanelSurface, shape).border(1.dp, PanelHairline, shape)
    val title = stringResource(R.string.media_idle)
    if (compact) {
        Row(
            frame.padding(horizontal = 20.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            PanelIcon(R.drawable.ic_note, contentDescription = null, size = 26.dp, tint = PanelTextMuted)
            Text(title, style = PanelType.body, color = PanelTextMuted, maxLines = 1, modifier = Modifier.weight(0.45f))
            VolumeControl(volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute, Modifier.weight(1f))
        }
    } else {
        Column(frame.padding(horizontal = 24.dp, vertical = 16.dp), verticalArrangement = Arrangement.Center) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                PanelIcon(R.drawable.ic_note, contentDescription = null, size = 26.dp, tint = PanelTextMuted)
                Text(title, style = PanelType.value, color = PanelTextMuted, maxLines = 1)
            }
            Box(Modifier.height(8.dp))
            VolumeControl(volume, volumeEnabled, onVolumeChange, onVolumeChangeFinished, onToggleVolumeMute, large = true)
        }
    }
}

@Composable
private fun MediaProgress(media: MediaState) {
    val durationMs = media.progress.durationMs
    if (durationMs <= 0) return
    val positionMs = media.progress.positionMs.coerceIn(0, durationMs)
    val progress = positionMs.toFloat() / durationMs.toFloat()
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Box(Modifier.fillMaxWidth().height(6.dp).clip(CircleShape).background(PanelSurfaceElevated)) {
            Box(Modifier.fillMaxWidth(progress).height(6.dp).background(PanelAccent))
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(formatMediaDuration(positionMs), style = PanelType.caption, color = PanelTextMuted)
            Text(formatMediaDuration(durationMs), style = PanelType.caption, color = PanelTextMuted)
        }
    }
}

internal fun formatMediaDuration(milliseconds: Long): String {
    val totalSeconds = milliseconds.coerceAtLeast(0) / 1_000
    val hours = totalSeconds / 3_600
    val minutes = (totalSeconds % 3_600) / 60
    val seconds = totalSeconds % 60
    return if (hours > 0) {
        "$hours:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}"
    } else {
        "$minutes:${seconds.toString().padStart(2, '0')}"
    }
}

@Composable
private fun MediaControls(
    media: MediaState,
    onPrevious: () -> Unit,
    onPlayPause: () -> Unit,
    onNext: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val playPauseLabel = stringResource(if (media.isPlaying) R.string.media_pause else R.string.media_play)
    Row(modifier, horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        TransportButton(R.drawable.ic_prev, stringResource(R.string.media_previous), media.available && media.capabilities.canPrevious, 56.dp, false, onPrevious)
        Box(Modifier.size(24.dp))
        TransportButton(
            if (media.isPlaying) R.drawable.ic_pause else R.drawable.ic_play,
            playPauseLabel, media.available && media.capabilities.canPlayPause, 72.dp, true, onPlayPause,
        )
        Box(Modifier.size(24.dp))
        TransportButton(R.drawable.ic_next, stringResource(R.string.media_next), media.available && media.capabilities.canNext, 56.dp, false, onNext)
    }
}

@Composable
private fun TransportButton(iconRes: Int, label: String, enabled: Boolean, size: Dp, primary: Boolean, onClick: () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    Box(
        modifier = Modifier
            .pressScale(interaction)
            .size(size)
            .alpha(if (enabled) 1f else 0.4f)
            .clip(CircleShape)
            .then(if (primary) Modifier.background(PanelAccent) else Modifier)
            .clickable(interactionSource = interaction, indication = null, enabled = enabled, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) {
        PanelIcon(iconRes, contentDescription = null, size = if (primary) 32.dp else 28.dp, tint = if (primary) PanelOnAccent else PanelText)
    }
}

@Composable
private fun AlbumArtwork(media: MediaState, size: Dp) {
    val description = stringResource(R.string.media_album_art_description)
    val targetSizePx = with(LocalDensity.current) { size.roundToPx() }
    val artworkBitmap by rememberArtworkBitmap(media.artwork, targetSizePx)
    Box(
        modifier = Modifier
            .size(size)
            .clip(RoundedCornerShape(PanelRadius.button))
            .background(Brush.linearGradient(listOf(Color(0xFF1B4F6B), Color(0xFF2A7FA0), Color(0xFF58A9C9))))
            .semantics { contentDescription = description },
        contentAlignment = Alignment.Center,
    ) {
        val bitmap = artworkBitmap
        if (bitmap != null) {
            Image(bitmap = bitmap, contentDescription = null, modifier = Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        } else {
            PanelIcon(R.drawable.ic_note, contentDescription = null, size = 72.dp, tint = Color.White.copy(alpha = 0.85f))
        }
    }
}

@Composable
private fun rememberArtworkBitmap(artwork: MediaArtwork?, targetSizePx: Int): State<ImageBitmap?> = produceState<ImageBitmap?>(
    initialValue = null,
    key1 = artwork,
    key2 = targetSizePx,
) {
    value = withContext(Dispatchers.Default) {
        artwork?.let { decodeSampledArtwork(it.bytes, targetSizePx) }
    }
}

private fun decodeSampledArtwork(bytes: ByteArray, targetSizePx: Int): ImageBitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    val target = targetSizePx.coerceAtLeast(1)
    var sampleSize = 1
    while (bounds.outWidth / sampleSize > target * 2 || bounds.outHeight / sampleSize > target * 2) {
        sampleSize *= 2
    }
    val options = BitmapFactory.Options().apply { inSampleSize = sampleSize }
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)?.asImageBitmap()
}
