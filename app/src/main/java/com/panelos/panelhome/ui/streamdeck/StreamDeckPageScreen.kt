package com.panelos.panelhome.ui.streamdeck

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Image
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.compositeOver
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import com.panelos.panelhome.ui.theme.LocalPanelReducedMotion
import com.panelos.panelhome.ui.theme.PanelAccent
import com.panelos.panelhome.ui.theme.PanelAccentSoft
import com.panelos.panelhome.ui.theme.PanelError
import com.panelos.panelhome.ui.theme.PanelMotion
import com.panelos.panelhome.ui.theme.PanelOutline
import com.panelos.panelhome.ui.theme.PanelRadius
import com.panelos.panelhome.ui.theme.PanelSpacing
import com.panelos.panelhome.ui.theme.PanelSurface
import com.panelos.panelhome.ui.theme.PanelText
import com.panelos.panelhome.ui.theme.PanelTextMuted
import com.panelos.panelhome.ui.theme.PanelType
import com.panelos.panelhome.R
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.domain.model.StreamDeckPage
import com.panelos.panelhome.presentation.StreamDeckUiState

/**
 * One Stream Deck page: the grid the Control Center configured, sized to the
 * screen.
 *
 * The grid never scrolls. Rows and columns each take an equal share of the
 * space, so every button stays reachable without a gesture, and nothing inside
 * a page competes horizontally with the carousel that owns the swipe.
 */
@Composable
fun StreamDeckPageScreen(
    page: StreamDeckPage,
    streamDeck: StreamDeckUiState,
    icons: StreamDeckIconStore,
    onButtonPressed: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(horizontal = PanelSpacing.page)
            .padding(top = PanelSpacing.sm, bottom = PanelSpacing.lg),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(
            // Weight, not fillMaxSize: the grid takes what is left after the
            // page name instead of measuring against the whole screen.
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            for (row in 0 until page.rows) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    for (column in 0 until page.columns) {
                        val button = page.buttonAt(row, column)
                        if (button == null) {
                            // An empty slot stays empty. The "+" affordance
                            // belongs to the Control Center's editor; the panel
                            // is not an editing surface.
                            Box(modifier = Modifier.weight(1f))
                        } else {
                            StreamDeckButtonTile(
                                label = button.label,
                                glyph = button.glyph,
                                icon = rememberStreamDeckIcon(
                                    icons = icons,
                                    iconId = button.iconId,
                                    generation = streamDeck.iconGeneration,
                                ),
                                enabled = button.enabled,
                                isPending = streamDeck.pendingButtonId == button.id,
                                hasFailed = streamDeck.failedButtonId == button.id,
                                onClick = { onButtonPressed(button.id) },
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxSize(),
                            )
                        }
                    }
                }
            }
        }
    }
}

/** Shown instead of a page when the PC has nothing configured. */
@Composable
fun StreamDeckEmptyScreen(modifier: Modifier = Modifier) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = stringResource(R.string.streamdeck_title),
            style = MaterialTheme.typography.headlineMedium,
        )
        Text(
            text = stringResource(R.string.streamdeck_empty_hint),
            style = MaterialTheme.typography.bodyLarge,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = 12.dp),
        )
    }
}

@Composable
private fun StreamDeckButtonTile(
    label: String,
    glyph: String?,
    icon: ImageBitmap?,
    enabled: Boolean,
    isPending: Boolean,
    hasFailed: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val interactionSource = remember { MutableInteractionSource() }
    val isPressed by interactionSource.collectIsPressedAsState()
    val haptics = LocalHapticFeedback.current
    val reducedMotion = LocalPanelReducedMotion.current

    // Pressed is shown immediately and held until the PC answers; it never
    // means the action succeeded.
    val down = isPressed || isPending
    val scale by animateFloatAsState(
        targetValue = if (down && !reducedMotion) PanelMotion.PressedScale else 1f,
        animationSpec = tween(PanelMotion.PressMillis, easing = PanelMotion.EaseOut),
        label = "streamDeckButtonScale",
    )
    val border = when {
        hasFailed -> PanelError
        down -> PanelAccent
        else -> PanelOutline
    }
    val fill = if (down) PanelAccentSoft.compositeOver(PanelSurface) else PanelSurface
    val content = when {
        down -> PanelAccent
        enabled -> PanelTextMuted
        else -> PanelTextMuted
    }
    val failedText = stringResource(R.string.streamdeck_failed)
    val shape = RoundedCornerShape(PanelRadius.button)

    Box(
        modifier = modifier
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clip(shape)
            .background(fill, shape)
            .border(1.dp, border, shape)
            .clickable(
                interactionSource = interactionSource,
                indication = null,
                enabled = enabled,
                role = Role.Button,
                onClick = {
                    haptics.performHapticFeedback(HapticFeedbackType.VirtualKey)
                    onClick()
                },
            )
            .semantics { if (hasFailed) stateDescription = failedText },
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(10.dp)
                .alpha(if (enabled) 1f else DISABLED_ALPHA),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box(
                modifier = Modifier
                    .weight(1f)
                    .fillMaxWidth(),
                contentAlignment = Alignment.Center,
            ) {
                if (icon != null) {
                    Image(
                        bitmap = icon,
                        contentDescription = null,
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize(ICON_FRACTION),
                    )
                } else {
                    StreamDeckGlyph(
                        glyph = glyph,
                        color = content,
                        modifier = Modifier.size(GLYPH_SIZE),
                    )
                }
            }
            Text(
                text = label,
                style = PanelType.label.copy(letterSpacing = TextUnit.Unspecified),
                color = if (enabled) PanelText else PanelTextMuted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth(),
            )
            if (hasFailed) {
                Text(
                    text = failedText,
                    style = PanelType.caption.copy(fontWeight = FontWeight.SemiBold),
                    color = PanelError,
                    maxLines = 1,
                    textAlign = TextAlign.Center,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        }
    }
}

/**
 * Decodes a cached icon, re-reading it when a new one arrives. Icons live on
 * disk rather than in the UI state, so [generation] is what invalidates this.
 */
@Composable
private fun rememberStreamDeckIcon(
    icons: StreamDeckIconStore,
    iconId: String?,
    generation: Int,
): ImageBitmap? = remember(iconId, generation) {
    val file = icons.iconFile(iconId) ?: return@remember null
    val bounds = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
    android.graphics.BitmapFactory.decodeFile(file.path, bounds)
    if (bounds.outWidth !in 1..MAX_ICON_SOURCE_DIMENSION ||
        bounds.outHeight !in 1..MAX_ICON_SOURCE_DIMENSION
    ) {
        return@remember null
    }
    var sampleSize = 1
    while (bounds.outWidth / sampleSize > ICON_DECODE_SIZE ||
        bounds.outHeight / sampleSize > ICON_DECODE_SIZE
    ) {
        sampleSize *= 2
    }
    runCatching {
        android.graphics.BitmapFactory.decodeFile(
            file.path,
            android.graphics.BitmapFactory.Options().apply { inSampleSize = sampleSize },
        )
    }
        .getOrNull()
        ?.asImageBitmap()
}

private const val DISABLED_ALPHA = 0.4f
private const val ICON_FRACTION = 0.72f
private const val ICON_DECODE_SIZE = 128
private const val MAX_ICON_SOURCE_DIMENSION = 512
private val GLYPH_SIZE = 40.dp
