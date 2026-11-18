package com.panelos.panelhome.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.IconButton
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.VolumeState
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelSize
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelSurfaceElevated
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import kotlin.math.roundToInt

/**
 * Master volume on one line: speaker (taps to mute), a 6 dp track, a single
 * round 22 dp thumb with a ring in the card colour, and the value. The
 * Material slider's pill-plus-dot is replaced by custom [Slider] parts. The
 * whole control is 48 dp tall to touch.
 *
 * [large] is for the idle media card, where the volume is the main thing.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
internal fun VolumeControl(
    volume: VolumeState,
    enabled: Boolean,
    onVolumeChange: (Int) -> Unit,
    onVolumeChangeFinished: (Int) -> Unit,
    onToggleMute: () -> Unit,
    modifier: Modifier = Modifier,
    large: Boolean = false,
    ringColor: Color = PanelSurface,
) {
    val authoritative = volume.volumePercent.coerceIn(0, 100)
    var sliderValue by remember { mutableFloatStateOf(authoritative.toFloat()) }
    var isDragging by remember { mutableStateOf(false) }
    LaunchedEffect(authoritative, enabled) {
        if (!enabled || !isDragging) {
            isDragging = false
            sliderValue = authoritative.toFloat()
        }
    }
    val displayed = sliderValue.roundToInt().coerceIn(0, 100)
    val muteDescription = stringResource(if (volume.isMuted) R.string.volume_unmute else R.string.volume_mute)
    val sliderDescription = stringResource(R.string.volume_slider_description)

    Row(
        modifier = modifier.fillMaxWidth().alpha(if (enabled) 1f else 0.4f),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconButton(
            onClick = onToggleMute,
            enabled = enabled,
            modifier = Modifier.size(PanelSize.minTouch).semantics { contentDescription = muteDescription },
        ) {
            PanelIcon(
                volumeIcon(volume.isMuted),
                contentDescription = null,
                size = PanelSize.navIcon,
                tint = PanelTextMuted,
            )
        }
        Slider(
            value = sliderValue,
            onValueChange = {
                isDragging = true
                sliderValue = it.coerceIn(0f, 100f)
                onVolumeChange(sliderValue.roundToInt())
            },
            onValueChangeFinished = {
                isDragging = false
                onVolumeChangeFinished(sliderValue.roundToInt().coerceIn(0, 100))
            },
            valueRange = 0f..100f,
            enabled = enabled,
            modifier = Modifier.weight(1f).padding(horizontal = 4.dp).semantics { contentDescription = sliderDescription },
            thumb = {
                // 22 dp dot inside a 3 dp ring of the card colour: one thumb, not two shapes.
                Box(Modifier.size(28.dp).clip(CircleShape).background(ringColor).padding(3.dp).clip(CircleShape).background(PanelAccent))
            },
            track = { state ->
                val fraction = ((state.value - state.valueRange.start) / (state.valueRange.endInclusive - state.valueRange.start)).coerceIn(0f, 1f)
                Box(Modifier.fillMaxWidth().height(6.dp).clip(CircleShape).background(PanelSurfaceElevated)) {
                    Box(Modifier.fillMaxWidth(fraction).height(6.dp).background(PanelAccent))
                }
            },
        )
        if (volume.available) {
            // The value never reads "—": when the PC reports no volume, the control is disabled and has no number.
            Text(
                text = stringResource(R.string.volume_percent_format, displayed),
                style = if (large) PanelType.numeralS else PanelType.value,
                color = if (enabled) PanelText else PanelTextMuted,
                maxLines = 1,
                modifier = Modifier.width(if (large) 96.dp else 56.dp).padding(start = 8.dp).clearAndSetSemantics {},
            )
        }
    }
}

internal fun volumeIcon(isMuted: Boolean): Int = if (isMuted) R.drawable.ic_volume_muted else R.drawable.ic_volume
