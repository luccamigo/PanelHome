package com.panelos.panelhome.ui.ambient

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.ui.components.PanelDateTime
import com.panelos.panelhome.ui.components.formatTemperatureCelsius
import com.panelos.panelhome.ui.components.formatUsagePercent

@Composable
fun AmbientScreen(
    data: PanelData,
    dateTime: PanelDateTime,
    connectionStatus: ConnectionStatus,
    onBackToDashboard: () -> Unit,
    modifier: Modifier = Modifier,
) {
    ApplyAmbientWindowBrightness()

    val presentation = ambientPresentation(data)
    val burnInOffset = rememberAmbientBurnInOffset()
    val backLabel = stringResource(R.string.action_back_to_panel)
    val backgroundInteractionSource = remember { MutableInteractionSource() }

    BoxWithConstraints(
        modifier = modifier
            .fillMaxSize()
            .background(Color.Black),
    ) {
        val fontScale = LocalDensity.current.fontScale.coerceAtLeast(1f)
        val compact = maxWidth < 700.dp || maxHeight < (480 * fontScale).dp
        val horizontalPadding = if (compact) 24.dp else 48.dp
        val verticalPadding = if (compact) 18.dp else 28.dp

        Box(
            modifier = Modifier
                .matchParentSize()
                .clickable(
                    interactionSource = backgroundInteractionSource,
                    indication = null,
                    onClickLabel = backLabel,
                    onClick = onBackToDashboard,
                ),
        )

        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(
                    horizontal = horizontalPadding,
                    vertical = verticalPadding,
                )
                .offset(
                    x = burnInOffset.xDp.dp,
                    y = burnInOffset.yDp.dp,
                ),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentAlignment = Alignment.Center,
            ) {
                AmbientPrimaryContent(
                    presentation = presentation,
                    dateTime = dateTime,
                    compact = compact,
                )
            }
            Spacer(modifier = Modifier.height(if (compact) 8.dp else 14.dp))
            AmbientConnectionStatus(
                connectionStatus = connectionStatus,
                compact = compact,
            )
        }
    }
}

@Composable
private fun AmbientPrimaryContent(
    presentation: AmbientPresentation,
    dateTime: PanelDateTime,
    compact: Boolean,
    modifier: Modifier = Modifier,
) {
    val media = presentation.media
    Column(
        modifier = modifier
            .widthIn(max = if (compact) 620.dp else 760.dp)
            .fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        AmbientClock(dateTime = dateTime, compact = compact)

        media?.let {
            Spacer(modifier = Modifier.height(if (compact) 16.dp else 28.dp))
            AmbientMedia(media = it, compact = compact)
        }

        Spacer(
            modifier = Modifier.height(
                when {
                    compact -> 18.dp
                    media != null -> 34.dp
                    else -> 28.dp
                },
            ),
        )
        AmbientMetrics(
            metrics = presentation.metrics,
            compact = compact,
        )
    }
}

@Composable
private fun AmbientClock(
    dateTime: PanelDateTime,
    compact: Boolean,
) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(
            text = dateTime.time,
            style = MaterialTheme.typography.displayLarge.copy(
                fontSize = if (compact) 68.sp else 96.sp,
                lineHeight = if (compact) 72.sp else 100.sp,
            ),
            textAlign = TextAlign.Center,
            maxLines = 1,
        )
        Text(
            text = dateTime.date,
            style = if (compact) {
                MaterialTheme.typography.titleMedium
            } else {
                MaterialTheme.typography.titleLarge
            },
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun AmbientMedia(
    media: AmbientMediaPresentation,
    compact: Boolean,
) {
    val title = media.title.ifBlank { stringResource(R.string.media_unknown_title) }
    val artist = media.artist.ifBlank { stringResource(R.string.media_unknown_artist) }

    Column(
        modifier = Modifier
            .widthIn(max = if (compact) 480.dp else 560.dp)
            .fillMaxWidth(),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(
            text = "$title — $artist",
            style = if (compact) {
                MaterialTheme.typography.titleMedium
            } else {
                MaterialTheme.typography.headlineMedium
            },
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        media.progress?.let { progress ->
            Spacer(modifier = Modifier.height(if (compact) 10.dp else 14.dp))
            LinearProgressIndicator(
                progress = { progress },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(2.dp),
                color = MaterialTheme.colorScheme.primary.copy(alpha = 0.72f),
                trackColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.12f),
            )
        }
    }
}

@Composable
private fun AmbientMetrics(
    metrics: AmbientMetricsPresentation,
    compact: Boolean,
) {
    Row(
        horizontalArrangement = Arrangement.spacedBy(if (compact) 18.dp else 36.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        AmbientMetric(
            label = stringResource(R.string.metric_cpu),
            value = formatTemperatureCelsius(metrics.cpuTemperatureCelsius),
            compact = compact,
        )
        AmbientMetric(
            label = stringResource(R.string.metric_gpu),
            value = formatTemperatureCelsius(metrics.gpuTemperatureCelsius),
            compact = compact,
        )
        AmbientMetric(
            label = stringResource(R.string.metric_ram),
            value = formatUsagePercent(metrics.ramUsagePercent),
            compact = compact,
        )
    }
}

@Composable
private fun AmbientMetric(
    label: String,
    value: String,
    compact: Boolean,
) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(
            text = label,
            style = if (compact) {
                MaterialTheme.typography.labelLarge
            } else {
                MaterialTheme.typography.titleMedium
            },
            color = MaterialTheme.colorScheme.primary.copy(alpha = 0.8f),
        )
        Spacer(modifier = Modifier.width(if (compact) 6.dp else 8.dp))
        Text(
            text = value,
            style = if (compact) {
                MaterialTheme.typography.bodyLarge
            } else {
                MaterialTheme.typography.titleLarge
            },
        )
    }
}

@Composable
private fun AmbientConnectionStatus(
    connectionStatus: ConnectionStatus,
    compact: Boolean,
    modifier: Modifier = Modifier,
) {
    val label = when (connectionStatus) {
        ConnectionStatus.CONNECTED -> R.string.connection_pc_connected
        ConnectionStatus.CONNECTING -> R.string.connection_pc_connecting
        ConnectionStatus.DISCONNECTED -> R.string.connection_pc_disconnected
    }
    Text(
        text = stringResource(label),
        modifier = modifier,
        style = if (compact) {
            MaterialTheme.typography.labelLarge
        } else {
            MaterialTheme.typography.bodyMedium
        },
        color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.68f),
        maxLines = 1,
    )
}
