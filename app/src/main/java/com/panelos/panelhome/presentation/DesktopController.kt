package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoFrameDiagnostics
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoState
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.video.VideoLogger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Owns desktop video transport, generations, frame store, and decoder lifecycle. */
internal class DesktopController(
    private val scope: CoroutineScope,
    private val videoConnection: VideoConnection,
    val frameStore: LatestVideoFrameStore,
    val h264Decoder: H264DecoderController,
    private val logger: VideoLogger,
    private val videoDesired: () -> Boolean,
    private val onStateChanged: (DesktopVideoState) -> Unit = {},
    private val retryDelay: suspend (Long) -> Unit = { delay(it) },
    private val elapsedMillis: () -> Long = { System.nanoTime() / 1_000_000L },
) {
    private val lock = Any()
    private var videoJob: Job? = null
    private var videoGeneration = 0L
    private var activeGeneration: Long? = null
    private var publishedDebugCount = 0
    private var h264RenderedDebugCount = 0
    private val _state = MutableStateFlow(DesktopVideoState())
    val state: StateFlow<DesktopVideoState> = _state.asStateFlow()

    init {
        h264Decoder.setDiagnosticsListener { diagnostics ->
            videoConnection.updateRuntimeState(diagnostics.toClientRuntimeState())
        }
    }

    fun reconcile(closeReason: VideoClientCloseReason) {
        if (videoDesired()) startIfNeeded() else stopAndClear(closeReason)
    }

    fun onFrameDrawn(generation: Long, sequence: Long) {
        synchronized(lock) {
            if (activeGeneration != generation) return
            val video = _state.value
            if (video.presentationGeneration != generation || video.sequence != sequence) return
            update(video.copy(status = DesktopVideoStatus.DISPLAYING, presentedSequence = sequence))
        }
    }

    fun onRenderError(generation: Long, sequence: Long, stage: String, error: Throwable) {
        synchronized(lock) {
            if (activeGeneration != generation) return
            val video = _state.value
            if (video.presentationGeneration != generation || video.sequence != sequence) return
            update(video.copy(status = DesktopVideoStatus.PRESENTATION_ERROR))
        }
    }

    fun stopAndClear(reason: VideoClientCloseReason) {
        synchronized(lock) {
            videoGeneration++
            activeGeneration = null
            updateRuntimeState()
            videoConnection.disconnect(reason)
            h264Decoder.stopSession()
            videoJob?.cancel()
            videoJob = null
            frameStore.clear()
            publishedDebugCount = 0
            h264RenderedDebugCount = 0
            if (_state.value != DesktopVideoState()) update(DesktopVideoState())
        }
    }

    fun close() {
        stopAndClear(VideoClientCloseReason.APP_STOP)
        videoConnection.close()
        h264Decoder.close()
    }

    private fun startIfNeeded() {
        val generation = synchronized(lock) {
            if (activeGeneration != null) return
            (++videoGeneration).also { generation ->
                activeGeneration = generation
                publishedDebugCount = 0
                h264RenderedDebugCount = 0
                frameStore.clear()
                update(DesktopVideoState(status = DesktopVideoStatus.CONNECTING))
            }
        }
        val launchedJob = scope.launch {
            val retryBackoff = VideoRetryBackoff()
            while (isDesired(generation)) {
                var connectedAt: Long? = null
                var receivedFrames = false
                try {
                    var negotiatedCodec: DesktopVideoCodec? = null
                    videoConnection.receiveVideo(
                        onConnected = { session ->
                            synchronized(lock) {
                                if (isDesiredLocked(generation)) {
                                    connectedAt = elapsedMillis()
                                    negotiatedCodec = if (session.codec == VideoProtocol.CODEC_H264) {
                                        DesktopVideoCodec.H264
                                    } else {
                                        DesktopVideoCodec.ZLIB_BGRA8888
                                    }
                                    if (negotiatedCodec == DesktopVideoCodec.H264) {
                                        h264Decoder.beginSession(
                                            generation = generation,
                                            session = session,
                                            onFrameRendered = ::onH264FrameRendered,
                                            onError = ::onH264DecoderError,
                                        )
                                        updateRuntimeState()
                                    }
                                    logger.transition(
                                        "Video transport ready: codec=$negotiatedCodec " +
                                            "effectiveSessionFps=${session.fps}",
                                    )
                                    val previous = _state.value
                                    val preservePresentation =
                                        previous.codec == negotiatedCodec &&
                                            previous.presentationGeneration == generation
                                    update(
                                        DesktopVideoState(
                                            status = if (negotiatedCodec == DesktopVideoCodec.H264) {
                                                DesktopVideoStatus.WAITING_FOR_CODEC_CONFIG
                                            } else {
                                                DesktopVideoStatus.WAITING_FOR_FIRST_FRAME
                                            },
                                            sequence = previous.sequence.takeIf { preservePresentation },
                                            presentationGeneration = generation,
                                            presentedSequence = previous.presentedSequence
                                                .takeIf { preservePresentation },
                                            width = session.width,
                                            height = session.height,
                                            codec = negotiatedCodec,
                                        ),
                                    )
                                }
                            }
                        },
                        onFrame = { frame ->
                            synchronized(lock) {
                                if (isDesiredLocked(generation)) receivedFrames = true
                                if (isDesiredLocked(generation) && frameStore.publish(frame)) {
                                    if (publishedDebugCount++ < VIDEO_DEBUG_FRAME_LIMIT) {
                                        logger.debug(
                                            VideoFrameDiagnostics.describeBgra(
                                                label = "Android published",
                                                sequence = frame.sequence,
                                                width = frame.width,
                                                height = frame.height,
                                                pixels = frame.pixels,
                                                byteCount = frame.pixelCountBytes,
                                            ),
                                        )
                                    }
                                    update(
                                        DesktopVideoState(
                                            status = DesktopVideoStatus.FRAME_RECEIVED,
                                            sequence = frame.sequence,
                                            presentationGeneration = generation,
                                            presentedSequence = _state.value.presentedSequence,
                                            width = frame.width,
                                            height = frame.height,
                                            codec = DesktopVideoCodec.ZLIB_BGRA8888,
                                        ),
                                    )
                                }
                            }
                        },
                        onH264Packet = { packet ->
                            synchronized(lock) {
                                if (!isDesiredLocked(generation) ||
                                    negotiatedCodec != DesktopVideoCodec.H264
                                ) {
                                    return@receiveVideo
                                }
                                if (!packet.header.isConfig) receivedFrames = true
                                h264Decoder.queuePacket(generation, packet)
                                updateRuntimeState()
                                if (packet.header.isConfig) {
                                    update(_state.value.copy(status = DesktopVideoStatus.WAITING_FOR_KEYFRAME))
                                }
                                if (!packet.header.isConfig) {
                                    update(
                                        _state.value.copy(
                                            status = DesktopVideoStatus.FRAME_RECEIVED,
                                            sequence = packet.header.sequence,
                                            presentationGeneration = generation,
                                            codec = DesktopVideoCodec.H264,
                                        ),
                                    )
                                }
                            }
                        },
                    )
                } catch (cancelled: CancellationException) {
                    throw cancelled
                } catch (error: Exception) {
                    logger.error("video_session", null, error)
                }

                val retry = synchronized(lock) {
                    if (isDesiredLocked(generation)) {
                        h264Decoder.stopSession()
                        val current = _state.value
                        val recovering = current.presentationGeneration == generation &&
                            current.presentedSequence != null
                        update(
                            if (recovering) current.copy(status = DesktopVideoStatus.RECOVERING)
                            else DesktopVideoState(status = DesktopVideoStatus.CONNECTING),
                        )
                        true
                    } else {
                        false
                    }
                }
                if (retry) {
                    val sessionMillis = connectedAt?.let { elapsedMillis() - it } ?: 0L
                    retryDelay(retryBackoff.nextDelayMillis(receivedFrames, sessionMillis))
                }
            }
        }
        synchronized(lock) {
            if (activeGeneration == generation) videoJob = launchedJob else launchedJob.cancel()
        }
    }

    private fun onH264FrameRendered(
        generation: Long,
        sequence: Long,
        receiveToRenderMillis: Double,
    ) {
        synchronized(lock) {
            if (!isDesiredLocked(generation)) return
            val video = _state.value
            if (video.codec != DesktopVideoCodec.H264 ||
                video.presentationGeneration != generation ||
                sequence <= (video.presentedSequence ?: 0L)
            ) {
                return
            }
            if (h264RenderedDebugCount++ < VIDEO_DEBUG_FRAME_LIMIT) {
                logger.debug(
                    "H.264 frame rendered: sequence=$sequence " +
                        "androidReceiveToRender=${"%.1f".format(receiveToRenderMillis)}ms",
                )
            }
            update(video.copy(status = DesktopVideoStatus.DISPLAYING, presentedSequence = sequence))
            updateRuntimeState()
        }
    }

    private fun onH264DecoderError(
        generation: Long,
        sequence: Long?,
        stage: String,
        error: Throwable,
    ) {
        synchronized(lock) {
            if (!isDesiredLocked(generation)) return
            logger.error(stage, sequence, error)
            updateRuntimeState()
            update(_state.value.copy(status = DesktopVideoStatus.PRESENTATION_ERROR))
            videoConnection.disconnect(VideoClientCloseReason.DECODER_ERROR)
        }
    }

    private fun updateRuntimeState() {
        videoConnection.updateRuntimeState(h264Decoder.diagnostics().toClientRuntimeState())
    }

    private fun isDesired(generation: Long): Boolean = synchronized(lock) {
        isDesiredLocked(generation)
    }

    private fun isDesiredLocked(generation: Long): Boolean =
        generation == videoGeneration && activeGeneration == generation && videoDesired()

    private fun update(value: DesktopVideoState) {
        _state.value = value
        onStateChanged(value)
    }

    private companion object {
        const val VIDEO_DEBUG_FRAME_LIMIT = 3
    }
}

// Exponential 1 s..30 s. A connect that never produced a frame must not reset it (a flapping WiFi peer
// would otherwise retry every second), but a session that delivered frames or lasted HEALTHY_SESSION_MILLIS
// proves the link worked, so the next failure starts again at 1 s. Otherwise the cable would stay at 30 s
// after five ordinary drops over the life of the app.
internal class VideoRetryBackoff {
    private var next = INITIAL_MILLIS

    fun nextDelayMillis(sessionReceivedFrames: Boolean = false, sessionMillis: Long = 0L): Long {
        if (sessionReceivedFrames || sessionMillis >= HEALTHY_SESSION_MILLIS) next = INITIAL_MILLIS
        return next.also { next = (next * 2).coerceAtMost(MAX_MILLIS) }
    }

    companion object {
        const val INITIAL_MILLIS = 1_000L
        const val MAX_MILLIS = 30_000L
        const val HEALTHY_SESSION_MILLIS = 10_000L
    }
}
