package com.panelos.panelhome.ui.desktop

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoState
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import com.panelos.panelhome.domain.video.LatestVideoFrameSource
import com.panelos.panelhome.domain.video.VideoSurfaceController

@Composable
fun DesktopScreen(
    status: DesktopStatus,
    errorCode: String?,
    videoState: DesktopVideoState,
    videoFrameStore: LatestVideoFrameSource,
    h264Decoder: VideoSurfaceController,
    touchFeatureNegotiated: Boolean,
    touchGeneration: Long?,
    onBackToDashboard: () -> Unit,
    onRetry: () -> Unit,
    onVideoFrameDrawn: (Long, Long) -> Unit,
    onVideoRenderError: (Long, Long, String, Throwable) -> Unit,
    onDesktopTouch: (DesktopTouchEvent) -> Unit,
    modifier: Modifier = Modifier,
) {
    if (status == DesktopStatus.RUNNING) {
        RunningDesktop(
            videoState = videoState,
            videoFrameStore = videoFrameStore,
            h264Decoder = h264Decoder,
            touchFeatureNegotiated = touchFeatureNegotiated,
            touchGeneration = touchGeneration,
            onBackToDashboard = onBackToDashboard,
            onVideoFrameDrawn = onVideoFrameDrawn,
            onVideoRenderError = onVideoRenderError,
            onDesktopTouch = onDesktopTouch,
            modifier = modifier,
        )
        return
    }

    Box(
        modifier = modifier
            .fillMaxSize()
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            modifier = Modifier.widthIn(max = 720.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(20.dp),
        ) {
            Text(
                text = stringResource(R.string.desktop_title),
                style = MaterialTheme.typography.displayMedium,
                textAlign = TextAlign.Center,
            )
            Text(
                text = stringResource(status.messageResource(errorCode)),
                style = MaterialTheme.typography.titleLarge,
                color = if (status == DesktopStatus.ERROR) {
                    MaterialTheme.colorScheme.error
                } else {
                    MaterialTheme.colorScheme.onSurfaceVariant
                },
                textAlign = TextAlign.Center,
            )
            if (status == DesktopStatus.STARTING || status == DesktopStatus.STOPPING) {
                CircularProgressIndicator()
            }
            if (status == DesktopStatus.STOPPED || status == DesktopStatus.ERROR) {
                Button(
                    onClick = onRetry,
                    modifier = Modifier.heightIn(min = 64.dp),
                ) {
                    Text(stringResource(R.string.desktop_retry))
                }
            }
            OutlinedButton(
                onClick = onBackToDashboard,
                enabled = status != DesktopStatus.STOPPING,
                modifier = Modifier.heightIn(min = 64.dp),
            ) {
                Text(stringResource(R.string.action_back_to_panel))
            }
        }
    }
}

@Composable
private fun RunningDesktop(
    videoState: DesktopVideoState,
    videoFrameStore: LatestVideoFrameSource,
    h264Decoder: VideoSurfaceController,
    touchFeatureNegotiated: Boolean,
    touchGeneration: Long?,
    onBackToDashboard: () -> Unit,
    onVideoFrameDrawn: (Long, Long) -> Unit,
    onVideoRenderError: (Long, Long, String, Throwable) -> Unit,
    onDesktopTouch: (DesktopTouchEvent) -> Unit,
    modifier: Modifier = Modifier,
) {
    Box(modifier = modifier.fillMaxSize()) {
        val sequence = videoState.sequence
        val generation = videoState.presentationGeneration
        if (videoState.codec == DesktopVideoCodec.H264 && generation != null) {
            H264DesktopVideoSurface(
                decoder = h264Decoder,
                generation = generation,
                sourceWidth = videoState.width ?: 0,
                sourceHeight = videoState.height ?: 0,
                touchFeatureNegotiated = touchFeatureNegotiated,
                touchGeneration = touchGeneration,
                onTouch = onDesktopTouch,
                modifier = Modifier.fillMaxSize(),
            )

            when {
                videoState.status == DesktopVideoStatus.PRESENTATION_ERROR -> VideoStatusOverlay(
                    text = stringResource(R.string.desktop_video_presentation_error),
                    isError = true,
                    modifier = Modifier.align(Alignment.Center),
                )

                videoState.presentedSequence == null -> VideoStatusOverlay(
                    text = stringResource(R.string.desktop_awaiting_first_frame),
                    isError = false,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
        } else if (sequence != null && generation != null) {
            DesktopVideoSurface(
                frameStore = videoFrameStore,
                frameSequence = sequence,
                frameGeneration = generation,
                sourceWidth = videoState.width ?: 0,
                sourceHeight = videoState.height ?: 0,
                touchFeatureNegotiated = touchFeatureNegotiated,
                touchGeneration = touchGeneration,
                onTouch = onDesktopTouch,
                onFrameDrawn = onVideoFrameDrawn,
                onRenderError = onVideoRenderError,
                modifier = Modifier.fillMaxSize(),
            )

            when {
                videoState.status == DesktopVideoStatus.PRESENTATION_ERROR -> VideoStatusOverlay(
                    text = stringResource(R.string.desktop_video_presentation_error),
                    isError = true,
                    modifier = Modifier.align(Alignment.Center),
                )

                videoState.presentedSequence == null -> VideoStatusOverlay(
                    text = stringResource(R.string.desktop_awaiting_first_frame),
                    isError = false,
                    modifier = Modifier.align(Alignment.Center),
                )
            }
        } else {
            Column(
                modifier = Modifier.align(Alignment.Center),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                Text(
                    text = stringResource(R.string.desktop_running),
                    style = MaterialTheme.typography.headlineMedium,
                    textAlign = TextAlign.Center,
                )
                Text(
                    text = stringResource(R.string.desktop_awaiting_first_frame),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    textAlign = TextAlign.Center,
                )
                CircularProgressIndicator()
            }
        }

        Button(
            onClick = onBackToDashboard,
            modifier = Modifier
                .align(Alignment.BottomEnd)
                .padding(24.dp)
                .fillMaxWidth(0.32f)
                .heightIn(min = 64.dp),
        ) {
            Text(stringResource(R.string.action_back_to_panel))
        }
    }
}

@Composable
private fun VideoStatusOverlay(
    text: String,
    isError: Boolean,
    modifier: Modifier = Modifier,
) {
    Surface(
        modifier = modifier.widthIn(max = 520.dp),
        color = MaterialTheme.colorScheme.surface.copy(alpha = 0.9f),
        shape = MaterialTheme.shapes.medium,
    ) {
        Text(
            text = text,
            modifier = Modifier.padding(horizontal = 24.dp, vertical = 16.dp),
            style = MaterialTheme.typography.titleMedium,
            color = if (isError) {
                MaterialTheme.colorScheme.error
            } else {
                MaterialTheme.colorScheme.onSurface
            },
            textAlign = TextAlign.Center,
        )
    }
}

internal fun DesktopStatus.messageResource(errorCode: String?): Int =
    when (this) {
        DesktopStatus.STOPPED -> R.string.desktop_stopped
        DesktopStatus.STARTING -> R.string.desktop_starting
        DesktopStatus.RUNNING -> R.string.desktop_running
        DesktopStatus.STOPPING -> R.string.desktop_stopping
        DesktopStatus.ERROR -> when (errorCode) {
            DesktopErrorCode.UNSUPPORTED -> R.string.desktop_error_unsupported
            DesktopErrorCode.CONNECTION_LOST -> R.string.desktop_error_connection_lost
            DesktopErrorCode.START_FAILED -> R.string.desktop_error_start_failed
            DesktopErrorCode.STOP_FAILED -> R.string.desktop_error_stop_failed
            DesktopErrorCode.INTERNAL_ERROR -> R.string.desktop_error
            DesktopErrorCode.DRIVER_NOT_FOUND -> R.string.desktop_error_driver_not_found
            DesktopErrorCode.DRIVER_ACCESS_DENIED -> R.string.desktop_error_driver_access_denied
            DesktopErrorCode.DRIVER_NOT_READY -> R.string.desktop_error_driver_not_ready
            DesktopErrorCode.DRIVER_IO_ERROR -> R.string.desktop_error_driver_io_error
            else -> R.string.desktop_error
        }
    }
