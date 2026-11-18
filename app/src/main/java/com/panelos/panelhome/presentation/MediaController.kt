package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.model.MediaCapabilities
import com.panelos.panelhome.domain.model.MediaProgress
import com.panelos.panelhome.domain.model.MediaState
import com.panelos.panelhome.domain.model.MediaTrack
import com.panelos.panelhome.domain.model.VolumeState
import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.ToggleVolumeMuteCommand
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

internal data class MediaControllerState(
    val media: MediaState = MockPanelData.initial.media,
    val volume: VolumeState = VolumeState(),
)

/** Owns remote media, volume state, commands, and latest-wins volume writes. */
internal class MediaController(
    scope: CoroutineScope,
    private val enqueueCommand: (PanelCommand) -> Boolean,
    private val onStateChanged: (MediaControllerState) -> Unit = {},
) {
    private val _state = MutableStateFlow(MediaControllerState())
    val state: StateFlow<MediaControllerState> = _state.asStateFlow()
    private var connected = false
    private var volumeNegotiated = false
    private val volumeCommands = VolumeCommandThrottler(
        scope = scope,
        onSend = { enqueueCommand(SetVolumeCommand(it)) },
    )

    fun onConnecting() {
        connected = false
        volumeNegotiated = false
        volumeCommands.cancel()
        update(_state.value.copy(volume = VolumeState()))
    }

    fun onConnected(features: Set<String>) {
        connected = true
        volumeNegotiated = PanelProtocol.FEATURE_VOLUME in features
        volumeCommands.cancel()
        update(_state.value.copy(volume = VolumeState()))
    }

    fun onDisconnected() {
        connected = false
        volumeNegotiated = false
        volumeCommands.cancel()
        update(MediaControllerState())
    }

    fun applyMediaState(message: MediaStateMessage) {
        val mediaId = message.mediaId?.takeIf(String::isNotBlank)
        val available = message.available
        val durationMs = message.durationMs.coerceAtLeast(0)
        val positionMs = message.positionMs.coerceAtLeast(0).let { position ->
            if (durationMs > 0) position.coerceAtMost(durationMs) else position
        }
        val currentArtwork = _state.value.media.artwork
            ?.takeIf { artwork -> available && artwork.mediaId == mediaId }
        update(
            _state.value.copy(
                media = MediaState(
                    available = available,
                    track = if (available) {
                        MediaTrack(
                            mediaId = mediaId,
                            sourceApp = message.sourceApp?.takeIf(String::isNotBlank),
                            title = message.title,
                            artist = message.artist,
                            album = message.album?.takeIf(String::isNotBlank),
                        )
                    } else {
                        null
                    },
                    isPlaying = available && message.isPlaying,
                    progress = if (available) {
                        MediaProgress(positionMs = positionMs, durationMs = durationMs)
                    } else {
                        MediaProgress(positionMs = 0, durationMs = 0)
                    },
                    capabilities = MediaCapabilities(
                        canPlayPause = available && message.canPlayPause,
                        canPrevious = available && message.canPrevious,
                        canNext = available && message.canNext,
                    ),
                    artwork = currentArtwork,
                ),
            ),
        )
    }

    fun applyArtwork(mediaId: String, artwork: MediaArtwork?) {
        val media = _state.value.media
        if (!media.available || media.track?.mediaId != mediaId) return
        update(_state.value.copy(media = media.copy(artwork = artwork)))
    }

    fun applyVolume(message: VolumeStateMessage) {
        val available = message.available && connected && volumeNegotiated
        if (!available) volumeCommands.cancel()
        update(
            _state.value.copy(
                volume = VolumeState(
                    available = available,
                    volumePercent = normalizeVolumePercent(message.volumePercent),
                    isMuted = message.isMuted,
                ),
            ),
        )
    }

    fun previousTrack() = sendMedia(MediaCommand.PREVIOUS) { it.capabilities.canPrevious }
    fun nextTrack() = sendMedia(MediaCommand.NEXT) { it.capabilities.canNext }
    fun togglePlayback() = sendMedia(MediaCommand.PLAY_PAUSE) { it.capabilities.canPlayPause }

    fun setVolume(volumePercent: Int) {
        if (canControlVolume()) volumeCommands.submit(volumePercent)
    }

    fun finishVolumeChange(volumePercent: Int) {
        if (canControlVolume()) volumeCommands.finish(volumePercent) else volumeCommands.cancel()
    }

    fun toggleVolumeMute() {
        if (canControlVolume()) enqueueCommand(ToggleVolumeMuteCommand)
    }

    fun close() = volumeCommands.cancel()

    private fun sendMedia(command: MediaCommand, supported: (MediaState) -> Boolean) {
        val media = _state.value.media
        if (connected && media.available && supported(media)) enqueueCommand(command)
    }

    private fun canControlVolume(): Boolean =
        connected && volumeNegotiated && _state.value.volume.available

    private fun update(value: MediaControllerState) {
        _state.value = value
        onStateChanged(value)
    }
}

internal const val VOLUME_COMMAND_THROTTLE_MILLIS = 50L

internal fun normalizeVolumePercent(volumePercent: Int): Int = volumePercent.coerceIn(0, 100)

internal class VolumeCommandThrottler(
    private val scope: CoroutineScope,
    private val intervalMillis: Long = VOLUME_COMMAND_THROTTLE_MILLIS,
    private val wait: suspend (Long) -> Unit = { delay(it) },
    private val onSend: (Int) -> Unit,
) {
    private val lock = Any()
    private var generation = 0L
    private var pendingVolumePercent: Int? = null
    private var worker: Job? = null

    init {
        require(intervalMillis > 0)
    }

    fun submit(volumePercent: Int) {
        synchronized(lock) {
            pendingVolumePercent = normalizeVolumePercent(volumePercent)
            if (worker?.isActive != true) {
                val workerGeneration = generation
                worker = scope.launch { drain(workerGeneration) }
            }
        }
    }

    fun finish(volumePercent: Int) {
        val jobToCancel = synchronized(lock) {
            generation++
            pendingVolumePercent = null
            worker.also {
                worker = null
                onSend(normalizeVolumePercent(volumePercent))
            }
        }
        jobToCancel?.cancel()
    }

    fun cancel() {
        val jobToCancel = synchronized(lock) {
            generation++
            pendingVolumePercent = null
            worker.also { worker = null }
        }
        jobToCancel?.cancel()
    }

    private suspend fun drain(workerGeneration: Long) {
        while (true) {
            val next = synchronized(lock) {
                if (workerGeneration != generation) return
                pendingVolumePercent?.also { pendingVolumePercent = null }
                    ?: run {
                        worker = null
                        return
                    }
            }
            synchronized(lock) {
                if (workerGeneration != generation) return
                onSend(next)
            }
            wait(intervalMillis)
        }
    }
}
