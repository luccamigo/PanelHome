package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.connection.TransportPanelConnection
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.streamdeck.NoStreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckLayoutMapper
import com.panelos.panelhome.data.transport.PanelTransport
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.AndroidVideoLogger
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.MediaCodecH264DecoderController
import com.panelos.panelhome.data.video.TransportVideoConnection
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoFrameDiagnostics
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import com.panelos.panelhome.domain.video.VideoLogger
import com.panelos.panelhome.domain.model.DesktopVideoState
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.GpuMetrics
import com.panelos.panelhome.domain.model.MediaCapabilities
import com.panelos.panelhome.domain.model.MediaProgress
import com.panelos.panelhome.domain.model.MediaState
import com.panelos.panelhome.domain.model.MediaTrack
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.model.RamMetrics
import com.panelos.panelhome.domain.model.StorageMetrics
import com.panelos.panelhome.domain.model.StreamDeckLayout
import com.panelos.panelhome.domain.model.VolumeState
import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.DesktopCommand
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.StreamDeckIconsRequestCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeResultMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.protocol.ToggleVolumeMuteCommand
import java.io.File
import java.util.Locale
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

internal class PanelSessionCoordinator(
    private val connection: PanelConnection = TransportPanelConnection(),
    private val videoConnection: VideoConnection = TransportVideoConnection(),
    override val videoFrameStore: LatestVideoFrameStore = LatestVideoFrameStore(),
    override val h264Decoder: H264DecoderController = MediaCodecH264DecoderController(),
    private val videoLogger: VideoLogger = AndroidVideoLogger,
    override val streamDeckIcons: StreamDeckIconStore = NoStreamDeckIconStore,
    loopMediaDirectory: File? = null,
    communicationScope: CoroutineScope,
    private val ownedTransport: PanelTransport? = null,
    monitoringPreferences: MonitoringPreferences = MonitoringPreferences(),
    private val saveMonitoringPreferences: (MonitoringPreferences) -> Unit = {},
    monitoringClock: () -> Long = { System.nanoTime() / 1_000_000 },
    initialMonitoringState: MonitoringState? = null,
    private val onPermissionReady: () -> Unit = {},
    initialIntegrationsState: LocalIntegrationsState? = null,
) : PanelSession {
    private val sessionJob = SupervisorJob(communicationScope.coroutineContext[Job])
    private val scope = CoroutineScope(communicationScope.coroutineContext + sessionJob)
    private val outgoingCommands = PanelOutgoingCommandQueue(OUTGOING_COMMAND_CAPACITY)
    private var activeConnectionId: Long? = null
    private var hostFeatures: Set<String> = emptySet()
    private var desktopExitPending = false
    private var awaitingInitialDesktopState = false
    private val _uiState = MutableStateFlow(
        PanelUiState(
            mode = PanelMode.DASHBOARD,
            panelData = MockPanelData.initial,
        ),
    )
    override val uiState: StateFlow<PanelUiState> = _uiState.asStateFlow()
    private val monitoringController = MonitoringController(preferences = monitoringPreferences,
        initialState = initialMonitoringState, now = monitoringClock, send = ::enqueueCommand) { monitoring ->
        _uiState.update { it.copy(monitoring = monitoring) }
    }
    private val integrationsController = LocalIntegrationsController(now = monitoringClock, send = ::enqueueCommand,
        onPermissionReady = onPermissionReady, initialState = initialIntegrationsState) { integrations ->
        _uiState.update { it.copy(integrations = integrations) }
    }
    private val desktopController = DesktopController(
        scope = scope,
        videoConnection = videoConnection,
        frameStore = videoFrameStore,
        h264Decoder = h264Decoder,
        logger = videoLogger,
        videoDesired = {
            val state = _uiState.value
            state.mode == PanelMode.DESKTOP &&
                state.desktopStatus == DesktopStatus.RUNNING &&
                state.connectionStatus == ConnectionStatus.CONNECTED
        },
        onStateChanged = { video ->
            _uiState.update { it.copy(desktopVideo = video) }
        },
    )
    private val telemetryController = TelemetryController { telemetry ->
        _uiState.update { state ->
            state.copy(
                fps = telemetry.fps,
                panelData = state.panelData.copy(
                    cpu = telemetry.cpu,
                    gpu = telemetry.gpu,
                    ram = telemetry.ram,
                    storage = telemetry.storage,
                ),
            )
        }
    }
    private val mediaController = MediaController(
        scope = scope,
        enqueueCommand = ::enqueueCommand,
        onStateChanged = { media ->
            _uiState.update { state ->
                state.copy(
                    panelData = state.panelData.copy(
                        media = media.media,
                        volume = media.volume,
                    ),
                )
            }
        },
    )
    private val streamDeckController = StreamDeckController(
        scope = scope,
        icons = streamDeckIcons,
        logger = videoLogger,
        enqueueCommand = ::enqueueCommand,
        onStateChanged = { streamDeck ->
            _uiState.update { it.copy(streamDeck = streamDeck) }
        },
    )
    private val touchController = TouchController(
        scope = scope,
        sessionState = { _uiState.value },
        enqueueCommand = ::enqueueCommand,
        logger = videoLogger,
    )
    private val loopMediaController = LoopMediaController(
        directory = loopMediaDirectory,
        logger = videoLogger,
        enqueueCommand = ::enqueueCommand,
        onStateChanged = { loopMedia ->
            _uiState.update { it.copy(loopMedia = loopMedia) }
        },
    )

    init {
        _uiState.update { it.copy(monitoring = monitoringController.state, integrations = integrationsController.state) }
        scope.launch {
            while (true) {
                delay(100)
                integrationsController.tick()
            }
        }
        scope.launch {
            while (true) {
                delay(1_000)
                monitoringController.tick()
            }
        }
        scope.launch {
            processOutgoingCommands()
        }
        scope.launch {
            connection.events.collect(::applyConnectionEvent)
        }
        scope.launch {
            connection.maintainConnection()
        }
        scope.launch {
            var lastMetrics = PanelOutgoingQueueMetrics()
            while (true) {
                delay(QUEUE_METRICS_LOG_INTERVAL_MILLIS)
                val metrics = outgoingCommands.metrics()
                if (metrics != lastMetrics) {
                    videoLogger.performance(
                        "[QueueMetrics] queueDrop=${metrics.queueDrop} " +
                            "queueReject=${metrics.queueReject} " +
                            "queueHighWatermark=${metrics.queueHighWatermark}",
                    )
                    lastMetrics = metrics
                }
            }
        }
    }

    override fun openDesktop() {
        val state = _uiState.value
        if (state.mode == PanelMode.DESKTOP ||
            state.connectionStatus != ConnectionStatus.CONNECTED
        ) {
            return
        }

        desktopExitPending = false
        touchController.reset()
        desktopController.stopAndClear(VideoClientCloseReason.RECONNECT)
        if (PanelProtocol.FEATURE_DESKTOP !in hostFeatures) {
            _uiState.update {
                it.copy(
                    mode = PanelMode.DESKTOP,
                    desktopStatus = DesktopStatus.ERROR,
                    desktopErrorCode = DesktopErrorCode.UNSUPPORTED,
                )
            }
            return
        }

        _uiState.update {
            it.copy(
                mode = PanelMode.DESKTOP,
                desktopStatus = DesktopStatus.STARTING,
                desktopErrorCode = null,
                desktopTouchGeneration = null,
            )
        }
        enqueueCommand(DesktopCommand.START)
    }

    override fun openSettings() = setMode(PanelMode.SETTINGS)

    override fun updateMonitoringPreferences(preferences: MonitoringPreferences) {
        val previous = monitoringController.state.preferences
        monitoringController.setPreferences(preferences)
        saveMonitoringPreferences(monitoringController.state.preferences)
        if ((previous.sort != preferences.sort || previous.grouped != preferences.grouped) &&
            PanelProtocol.FEATURE_PROCESSES in hostFeatures) {
            enqueueCommand(com.panelos.panelhome.domain.protocol.ProcessesOptionsCommand(preferences.sort, preferences.grouped))
        }
    }

    override fun setProcessListInteracting(active: Boolean) = monitoringController.setInteracting(active)
    override fun requestTerminateProcess(key: String) = monitoringController.requestTerminate(key)
    override fun dismissTermination() = monitoringController.dismissTermination()
    override fun setUiTouchActive(active: Boolean) = integrationsController.setTouchActive(active)
    override fun markNoticesSeen() = integrationsController.markNoticesSeen()
    override fun dismissNoticeBanner() = integrationsController.dismissBanner()
    override fun permissionShown(requestId: String) = integrationsController.permissionShown(requestId)
    override fun respondToPermission(requestId: String, decision: String) = integrationsController.respond(requestId, decision)

    override fun enterAmbient() = setMode(PanelMode.AMBIENT)

    override fun returnToDashboard() {
        val state = _uiState.value
        if (state.mode != PanelMode.DESKTOP) {
            setMode(PanelMode.DASHBOARD)
            return
        }
        if (desktopExitPending) {
            if (state.desktopStatus == DesktopStatus.ERROR &&
                state.connectionStatus == ConnectionStatus.CONNECTED &&
                PanelProtocol.FEATURE_DESKTOP in hostFeatures
            ) {
                _uiState.update {
                    it.copy(desktopStatus = DesktopStatus.STOPPING, desktopErrorCode = null)
                }
                enqueueCommand(DesktopCommand.STOP)
            }
            return
        }

        val canStop = state.connectionStatus == ConnectionStatus.CONNECTED &&
            PanelProtocol.FEATURE_DESKTOP in hostFeatures &&
            state.desktopStatus != DesktopStatus.STOPPED
        if (!canStop) {
            desktopExitPending = false
            touchController.cancelActive()
            desktopController.stopAndClear(VideoClientCloseReason.DESKTOP_EXIT)
            _uiState.update {
                it.copy(
                    mode = PanelMode.DASHBOARD,
                    desktopStatus = DesktopStatus.STOPPED,
                    desktopErrorCode = null,
                    desktopTouchGeneration = null,
                )
            }
            return
        }

        desktopExitPending = true
        touchController.cancelActive()
        desktopController.stopAndClear(VideoClientCloseReason.DESKTOP_EXIT)
        _uiState.update {
            it.copy(
                desktopStatus = DesktopStatus.STOPPING,
                desktopErrorCode = null,
                desktopTouchGeneration = null,
            )
        }
        enqueueCommand(DesktopCommand.STOP)
    }

    override fun retryDesktop() {
        val state = _uiState.value
        if (state.mode != PanelMode.DESKTOP ||
            state.connectionStatus != ConnectionStatus.CONNECTED
        ) {
            return
        }
        if (PanelProtocol.FEATURE_DESKTOP !in hostFeatures) {
            _uiState.update {
                it.copy(
                    desktopStatus = DesktopStatus.ERROR,
                    desktopErrorCode = DesktopErrorCode.UNSUPPORTED,
                )
            }
            return
        }

        if (desktopExitPending) {
            if (state.desktopStatus != DesktopStatus.ERROR) return
            _uiState.update {
                it.copy(desktopStatus = DesktopStatus.STOPPING, desktopErrorCode = null)
            }
            enqueueCommand(DesktopCommand.STOP)
        } else {
            if (state.desktopStatus !in setOf(DesktopStatus.STOPPED, DesktopStatus.ERROR)) return
            touchController.reset()
            desktopController.stopAndClear(VideoClientCloseReason.RECONNECT)
            _uiState.update {
                it.copy(
                    desktopStatus = DesktopStatus.STARTING,
                    desktopErrorCode = null,
                    desktopTouchGeneration = null,
                )
            }
            enqueueCommand(DesktopCommand.START)
        }
    }

    override fun updateDeviceProfile(deviceProfile: DeviceProfile?) {
        _uiState.update { it.copy(deviceProfile = deviceProfile) }
    }

    override fun onVideoFrameDrawn(generation: Long, sequence: Long) =
        desktopController.onFrameDrawn(generation, sequence)

    override fun onVideoRenderError(
        generation: Long,
        sequence: Long,
        stage: String,
        error: Throwable,
    ) = desktopController.onRenderError(generation, sequence, stage, error)

    override fun previousTrack() = mediaController.previousTrack()

    override fun nextTrack() = mediaController.nextTrack()

    override fun togglePlayback() = mediaController.togglePlayback()

    override fun setVolume(volumePercent: Int) = mediaController.setVolume(volumePercent)

    override fun finishVolumeChange(volumePercent: Int) =
        mediaController.finishVolumeChange(volumePercent)

    override fun toggleVolumeMute() = mediaController.toggleVolumeMute()

    /**
     * Sends a button press. Only the id and the revision of the layout being
     * drawn leave the tablet; the PC decides what that button does.
     *
     * The button shows as pressed straight away, but never as done: success is
     * only what `streamdeck.invoke_result` reports.
     */
    override fun invokeStreamDeckButton(buttonId: String) = streamDeckController.invoke(buttonId)

    /** Clears the discreet error mark once the user has seen it. */
    override fun clearStreamDeckFailure() = streamDeckController.clearFailure()

    override fun onDesktopTouch(event: DesktopTouchEvent) = touchController.submit(event)

    fun close() {
        sessionJob.cancel()
        mediaController.close()
        touchController.cancelActive()
        outgoingCommands.close()
        desktopController.close()
        connection.close()
        ownedTransport?.close()
    }

    private fun applyConnectionEvent(event: PanelConnectionEvent) {
        when (event) {
            PanelConnectionEvent.Connecting -> {
                integrationsController.onDisconnected()
                monitoringController.onDisconnected()
                mediaController.onConnecting()
                touchController.cancelActive()
                activeConnectionId = null
                hostFeatures = emptySet()
                awaitingInitialDesktopState = false
                _uiState.update {
                    it.copy(
                        connectionStatus = ConnectionStatus.CONNECTING,
                        hostName = null,
                        desktopTouchAvailable = false,
                        desktopTouchGeneration = null,
                    )
                }
                streamDeckController.onDisconnected()
                loopMediaController.onDisconnected()
                reconcileVideoLifecycle()
            }

            is PanelConnectionEvent.Connected -> {
                mediaController.onConnected(event.features)
                touchController.reset()
                activeConnectionId = event.connectionId
                hostFeatures = event.features
                awaitingInitialDesktopState = PanelProtocol.FEATURE_DESKTOP in event.features
                _uiState.update {
                    it.copy(
                        connectionStatus = ConnectionStatus.CONNECTED,
                        hostName = event.hostName,
                        desktopTouchAvailable = PanelProtocol.FEATURE_TOUCH in event.features,
                        desktopTouchGeneration = null,

                        // The layout is not carried over: PanelHost sends the
                        // current one immediately after the handshake, so a
                        // reconnect never draws what a previous session knew.
                    )
                }
                streamDeckController.onConnected(event.features)
                monitoringController.onConnected(event.features, event.hostName)
                integrationsController.onConnected(event.features)
                if (PanelProtocol.FEATURE_PROCESSES in event.features) {
                    val preferences = monitoringController.state.preferences
                    enqueueCommand(com.panelos.panelhome.domain.protocol.ProcessesOptionsCommand(preferences.sort, preferences.grouped))
                }
                loopMediaController.onConnected(event.features)
                videoLogger.debug(
                    "[TouchDebug] HANDSHAKE hostFeatures=${event.features.sorted()} " +
                        "touchNegotiated=${PanelProtocol.FEATURE_TOUCH in event.features}",
                )
                reconcileVideoLifecycle()
            }

            is PanelConnectionEvent.TelemetryReceived -> {
                telemetryController.apply(if (PanelProtocol.FEATURE_FOREGROUND_PRESENTS in hostFeatures) event.telemetry else event.telemetry.copy(fps = null))
                monitoringController.applyTelemetry(event.telemetry)
            }

            is PanelConnectionEvent.NetworkRatesReceived -> monitoringController.applyNetwork(event.rates)
            is PanelConnectionEvent.ProcessesReceived -> monitoringController.applyProcesses(event.processes)
            is PanelConnectionEvent.ProcessTerminateResultReceived -> monitoringController.applyTerminateResult(event.message)
            is PanelConnectionEvent.MonitoringDataInvalid -> monitoringController.onInvalidData(event.type)
            is PanelConnectionEvent.LocalNoticesReceived -> integrationsController.apply(event.message)
            is PanelConnectionEvent.ClaudePermissionsReceived -> integrationsController.apply(event.message)

            is PanelConnectionEvent.MediaStateReceived ->
                mediaController.applyMediaState(event.mediaState)

            is PanelConnectionEvent.MediaArtworkReceived ->
                mediaController.applyArtwork(event.mediaId, event.artwork)

            is PanelConnectionEvent.VolumeStateReceived -> {
                mediaController.applyVolume(event.volumeState)
            }

            is PanelConnectionEvent.DesktopStateReceived -> {
                val currentGeneration = _uiState.value.desktopTouchGeneration
                val incomingGeneration = event.desktopState.generation
                    ?.takeIf { it > 0 && event.desktopState.status == DesktopStatus.RUNNING }
                if (currentGeneration != null && currentGeneration != incomingGeneration) {
                    touchController.cancelActive()
                }
                applyDesktopState(
                    status = event.desktopState.status,
                    errorCode = event.desktopState.errorCode,
                    generation = incomingGeneration,
                )
                videoLogger.debug(
                    "[TouchDebug] DESKTOP state=${event.desktopState.status} " +
                        "hostGeneration=${event.desktopState.generation ?: "missing"} " +
                        "acceptedGeneration=${incomingGeneration ?: "missing"}",
                )
                reconcileVideoLifecycle()
            }

            is PanelConnectionEvent.StreamDeckLayoutReceived ->
                streamDeckController.applyLayout(event.layout)

            is PanelConnectionEvent.StreamDeckInvokeResultReceived ->
                streamDeckController.applyInvokeResult(event.result)

            is PanelConnectionEvent.StreamDeckIconReceived -> {
                streamDeckController.applyIcon(event.icon)
            }

            is PanelConnectionEvent.LoopMediaStateReceived ->
                loopMediaController.applyState(event.state)

            is PanelConnectionEvent.LoopMediaChunkReceived ->
                loopMediaController.applyChunk(event.chunk)

            PanelConnectionEvent.Disconnected -> {
                integrationsController.onDisconnected()
                monitoringController.onDisconnected()
                mediaController.onDisconnected()
                telemetryController.reset()
                touchController.cancelActive()
                activeConnectionId = null
                hostFeatures = emptySet()
                desktopExitPending = false
                awaitingInitialDesktopState = false
                _uiState.update {
                    val desktopWasOpen = it.mode == PanelMode.DESKTOP
                    it.copy(
                        mode = if (desktopWasOpen) PanelMode.DASHBOARD else it.mode,
                        connectionStatus = ConnectionStatus.DISCONNECTED,
                        hostName = null,
                        desktopStatus = if (desktopWasOpen) {
                            DesktopStatus.ERROR
                        } else {
                            DesktopStatus.STOPPED
                        },
                        desktopErrorCode = if (desktopWasOpen) {
                            DesktopErrorCode.CONNECTION_LOST
                        } else {
                            null
                        },
                        desktopTouchAvailable = false,
                        desktopTouchGeneration = null,
                    )
                }
                streamDeckController.onDisconnected()
                loopMediaController.onDisconnected()
                reconcileVideoLifecycle()
            }
        }
    }

    private fun applyDesktopState(
        status: DesktopStatus,
        errorCode: String?,
        generation: Long?,
    ) {
        if (awaitingInitialDesktopState) {
            awaitingInitialDesktopState = false
            val currentState = _uiState.value
            if (status == DesktopStatus.STOPPED &&
                currentState.mode == PanelMode.DESKTOP &&
                currentState.desktopStatus in setOf(
                    DesktopStatus.STARTING,
                    DesktopStatus.STOPPING,
                )
            ) {
                return
            }
        }

        if (desktopExitPending) {
            when (status) {
                DesktopStatus.STARTING,
                DesktopStatus.RUNNING,
                -> return

                DesktopStatus.STOPPED -> {
                    desktopExitPending = false
                    _uiState.update {
                        it.copy(
                            mode = PanelMode.DASHBOARD,
                            desktopStatus = DesktopStatus.STOPPED,
                            desktopErrorCode = null,
                            desktopTouchGeneration = null,
                        )
                    }
                }

                DesktopStatus.STOPPING -> _uiState.update {
                    it.copy(
                        desktopStatus = DesktopStatus.STOPPING,
                        desktopErrorCode = null,
                        desktopTouchGeneration = null,
                    )
                }

                DesktopStatus.ERROR -> _uiState.update {
                    it.copy(
                        desktopStatus = DesktopStatus.ERROR,
                        desktopErrorCode = errorCode,
                        desktopTouchGeneration = null,
                    )
                }
            }
            return
        }

        _uiState.update { state ->
            state.copy(
                mode = if (
                    status == DesktopStatus.STOPPED && state.mode == PanelMode.DESKTOP
                ) {
                    PanelMode.DASHBOARD
                } else {
                    state.mode
                },
                desktopStatus = status,
                desktopErrorCode = errorCode.takeIf { status == DesktopStatus.ERROR },
                desktopTouchGeneration = generation.takeIf {
                    status == DesktopStatus.RUNNING &&
                        state.desktopTouchAvailable
                },
            )
        }
    }

    private fun enqueueCommand(command: PanelCommand): Boolean {
        if (_uiState.value.connectionStatus != ConnectionStatus.CONNECTED) return false
        val connectionId = activeConnectionId ?: return false
        return when (outgoingCommands.offer(command, connectionId)) {
            PanelOutgoingOffer.ENQUEUED,
            PanelOutgoingOffer.COALESCED,
            -> true

            PanelOutgoingOffer.REJECTED,
            PanelOutgoingOffer.CLOSED,
            -> {
                if (command is TouchCommand && command.action != TouchCommandAction.MOVE) {
                    touchController.reset()
                }
                false
            }
        }
    }

    private fun reconcileVideoLifecycle() {
        val state = _uiState.value
        val closeReason = when {
            state.connectionStatus != ConnectionStatus.CONNECTED ->
                VideoClientCloseReason.RECONNECT
            state.mode != PanelMode.DESKTOP || state.desktopStatus != DesktopStatus.RUNNING ->
                VideoClientCloseReason.DESKTOP_EXIT
            else -> VideoClientCloseReason.APP_STOP
        }
        desktopController.reconcile(closeReason)
    }

    private suspend fun processOutgoingCommands() {
        while (true) {
            val queued = outgoingCommands.take() ?: return
            if (queued.connectionId != activeConnectionId ||
                _uiState.value.connectionStatus != ConnectionStatus.CONNECTED
            ) {
                continue
            }
            try {
                connection.send(
                    command = queued.command,
                    expectedConnectionId = queued.connectionId,
                )
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (_: Exception) {
                // Connection lifecycle events reconcile the visible state.
            }
        }
    }

    private fun setMode(mode: PanelMode) {
        if (_uiState.value.mode == PanelMode.DESKTOP && mode != PanelMode.DESKTOP) {
            touchController.cancelActive()
        }
        _uiState.update {
            it.copy(
                mode = mode,
                desktopTouchGeneration = if (mode == PanelMode.DESKTOP) {
                    it.desktopTouchGeneration
                } else {
                    null
                },
            )
        }
    }

    private companion object {
        const val VIDEO_RETRY_DELAY_MILLIS = 1_000L
        const val VIDEO_DEBUG_FRAME_LIMIT = 3
        const val OUTGOING_COMMAND_CAPACITY = 64
        const val QUEUE_METRICS_LOG_INTERVAL_MILLIS = 5_000L
    }
}

internal data class QueuedPanelCommand(
    val command: PanelCommand,
    val connectionId: Long,
)

internal enum class PanelOutgoingOffer {
    ENQUEUED,
    COALESCED,
    REJECTED,
    CLOSED,
}

internal data class PanelOutgoingQueueMetrics(
    val queueDrop: Long = 0,
    val queueReject: Long = 0,
    val queueHighWatermark: Int = 0,
)

/** Bounded ordered queue with semantic coalescing for state and MOVE. */
internal class PanelOutgoingCommandQueue(private val capacity: Int) {
    private val lock = Any()
    private val pending = ArrayList<QueuedPanelCommand>(capacity)
    private val available = Channel<Unit>(Channel.CONFLATED)
    private var closed = false
    private var queueDrop = 0L
    private var queueReject = 0L
    private var queueHighWatermark = 0

    init {
        require(capacity > 0)
    }

    fun offer(command: PanelCommand, connectionId: Long): PanelOutgoingOffer = synchronized(lock) {
        if (closed) return@synchronized PanelOutgoingOffer.CLOSED
        val queued = QueuedPanelCommand(command, connectionId)
        val replaceIndex = findReplaceableIndex(command, connectionId)
        if (replaceIndex >= 0) {
            pending[replaceIndex] = queued
            queueDrop++
            return@synchronized PanelOutgoingOffer.COALESCED
        }

        val orderedLimit = if (capacity >= 8 && !command.isTouchBoundary()) {
            capacity - TOUCH_BOUNDARY_RESERVE
        } else {
            capacity
        }
        if (pending.size >= orderedLimit) {
            val evictIndex = pending.indexOfFirst { it.command.isCoalescibleState() }
            if (evictIndex >= 0) {
                pending.removeAt(evictIndex)
                queueDrop++
            } else if (command.isCoalescibleState()) {
                queueDrop++
                return@synchronized PanelOutgoingOffer.COALESCED
            } else {
                queueReject++
                return@synchronized PanelOutgoingOffer.REJECTED
            }
        }

        pending += queued
        queueHighWatermark = maxOf(queueHighWatermark, pending.size)
        available.trySend(Unit)
        PanelOutgoingOffer.ENQUEUED
    }

    suspend fun take(): QueuedPanelCommand? {
        while (true) {
            if (available.receiveCatching().isClosed) return null
            val next = synchronized(lock) {
                if (pending.isEmpty()) {
                    null
                } else {
                    pending.removeAt(0).also {
                        if (pending.isNotEmpty()) available.trySend(Unit)
                    }
                }
            }
            if (next != null) return next
        }
    }

    fun metrics(): PanelOutgoingQueueMetrics = synchronized(lock) {
        PanelOutgoingQueueMetrics(queueDrop, queueReject, queueHighWatermark)
    }

    fun size(): Int = synchronized(lock) { pending.size }

    fun close() = synchronized(lock) {
        if (!closed) {
            closed = true
            pending.clear()
            available.close()
        }
    }

    private fun findReplaceableIndex(command: PanelCommand, connectionId: Long): Int {
        if (command is SetVolumeCommand) {
            return pending.indexOfLast {
                it.connectionId == connectionId && it.command is SetVolumeCommand
            }
        }
        if (command !is TouchCommand || command.action != TouchCommandAction.MOVE) return -1
        for (index in pending.lastIndex downTo 0) {
            val existing = pending[index]
            val touch = existing.command as? TouchCommand ?: continue
            if (existing.connectionId != connectionId || touch.generation != command.generation) {
                continue
            }
            if (touch.action == TouchCommandAction.MOVE) return index
            break
        }
        return -1
    }

    private fun PanelCommand.isCoalescibleState(): Boolean =
        this is SetVolumeCommand ||
            this is TouchCommand && action == TouchCommandAction.MOVE

    private fun PanelCommand.isTouchBoundary(): Boolean =
        this is TouchCommand && action != TouchCommandAction.MOVE

    private companion object {
        const val TOUCH_BOUNDARY_RESERVE = 4
    }
}
