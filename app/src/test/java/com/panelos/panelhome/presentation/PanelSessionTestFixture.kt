package com.panelos.panelhome.presentation

import android.view.Surface
import com.panelos.panelhome.data.connection.PanelConnection
import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.mock.MockPanelData
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264PacketHeader
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopVideoCodec
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.MediaArtwork
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.protocol.CpuTelemetry
import com.panelos.panelhome.domain.protocol.DesktopCommand
import com.panelos.panelhome.domain.protocol.DesktopErrorCode
import com.panelos.panelhome.domain.protocol.DesktopStateMessage
import com.panelos.panelhome.domain.protocol.GpuTelemetry
import com.panelos.panelhome.domain.protocol.MediaCommand
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.MemoryTelemetry
import com.panelos.panelhome.domain.protocol.StorageTelemetry
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.SetVolumeCommand
import com.panelos.panelhome.domain.protocol.ToggleVolumeMuteCommand
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import com.panelos.panelhome.domain.model.DesktopTouchEvent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

internal abstract class PanelSessionTestFixture {
    protected val connection = FakePanelConnection()
    protected val videoConnection = FakeVideoConnection()
    protected val videoFrameStore = LatestVideoFrameStore()
    protected val h264Decoder = FakeH264DecoderController()
    protected val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    protected val coordinator = PanelSessionCoordinator(
        connection = connection,
        videoConnection = videoConnection,
        videoFrameStore = videoFrameStore,
        h264Decoder = h264Decoder,
        videoLogger = SilentVideoLogger,
        communicationScope = scope,
    )

    @After
    fun tearDown() {
        scope.cancel()
        connection.close()
    }

    protected fun assertMetricEquals(expected: Double, actual: Double?) {
        assertEquals(expected, requireNotNull(actual), 0.0)
    }

    protected fun connectWithDesktop(connectionId: Long = 1L) {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = connectionId,
                hostName = "DEV-PC",
                features = setOf(PanelProtocol.FEATURE_DESKTOP),
            ),
        )
    }

    protected fun connectWithTouch(connectionId: Long = 1L) {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = connectionId,
                hostName = "DEV-PC",
                features = setOf(
                    PanelProtocol.FEATURE_DESKTOP,
                    PanelProtocol.FEATURE_TOUCH,
                ),
            ),
        )
    }

    protected fun connectWithVolume(connectionId: Long = 1L) {
        connection.emit(
            PanelConnectionEvent.Connected(
                connectionId = connectionId,
                hostName = "DEV-PC",
                features = setOf(PanelProtocol.FEATURE_VOLUME),
            ),
        )
    }

    protected fun emitAvailableVolume(
        volumePercent: Int = 72,
        isMuted: Boolean = false,
    ) {
        connection.emit(
            PanelConnectionEvent.VolumeStateReceived(
                VolumeStateMessage(
                    available = true,
                    volumePercent = volumePercent,
                    isMuted = isMuted,
                ),
            ),
        )
    }

    protected fun desktopState(
        status: DesktopStatus,
        errorCode: String? = null,
        generation: Long? = null,
    ) = PanelConnectionEvent.DesktopStateReceived(
        DesktopStateMessage(
            status = status,
            errorCode = errorCode,
            generation = generation,
        ),
    )

    protected fun touchEvent(
        action: TouchCommandAction,
        generation: Long,
    ) = DesktopTouchEvent(
        action = action,
        pointerId = 0,
        x = if (action == TouchCommandAction.CANCEL) null else 0.5,
        y = if (action == TouchCommandAction.CANCEL) null else 0.5,
        generation = generation,
    )

    protected fun connectedMedia(
        mediaId: String? = "media-1",
        sourceApp: String? = "Mock Player",
        album: String? = "Mock Album",
        isPlaying: Boolean = false,
        positionMs: Long = 0,
        durationMs: Long = 180_000,
        canPlayPause: Boolean = true,
        canPrevious: Boolean = true,
        canNext: Boolean = true,
    ) = MediaStateMessage(
        available = true,
        mediaId = mediaId,
        sourceApp = sourceApp,
        title = "Nightcall",
        artist = "Kavinsky",
        album = album,
        isPlaying = isPlaying,
        positionMs = positionMs,
        durationMs = durationMs,
        canPlayPause = canPlayPause,
        canPrevious = canPrevious,
        canNext = canNext,
    )

    protected fun artwork(mediaId: String) = MediaArtwork(
        mediaId = mediaId,
        mimeType = "image/png",
        bytes = byteArrayOf(1, 2, 3),
    )
}

class FakePanelConnection : PanelConnection {
    private val mutableEvents = MutableSharedFlow<PanelConnectionEvent>(
        extraBufferCapacity = 32,
    )

    override val events: Flow<PanelConnectionEvent> = mutableEvents
    val sentCommands = mutableListOf<PanelCommand>()
    val sentConnectionIds = mutableListOf<Long>()

    override suspend fun maintainConnection(): Nothing = awaitCancellation()

    override suspend fun send(command: PanelCommand, expectedConnectionId: Long) {
        sentCommands += command
        sentConnectionIds += expectedConnectionId
    }

    override fun close() = Unit

    fun emit(event: PanelConnectionEvent) {
        check(mutableEvents.tryEmit(event))
    }
}

class FakeVideoConnection : VideoConnection {
    var sessionsStarted = 0
        private set
    var sessionsDisconnected = 0
        private set
    private var active = false
    private var connectedCallback: ((VideoSessionInfo) -> Unit)? = null
    private var frameCallback: ((DecodedVideoFrame) -> Unit)? = null
    private var h264PacketCallback: ((H264VideoPacket) -> Unit)? = null
    private var sessionCompletion = CompletableDeferred<Unit>()
    val disconnectReasons = mutableListOf<VideoClientCloseReason>()

    override suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    ) = receiveVideo(
        onConnected = { onConnected() },
        onFrame = onFrame,
        onH264Packet = {},
    )

    override suspend fun receiveVideo(
        onConnected: (VideoSessionInfo) -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
        onH264Packet: (H264VideoPacket) -> Unit,
    ) {
        sessionsStarted++
        active = true
        connectedCallback = onConnected
        frameCallback = onFrame
        h264PacketCallback = onH264Packet
        sessionCompletion = CompletableDeferred()
        try {
            sessionCompletion.await()
        } finally {
            active = false
            connectedCallback = null
            frameCallback = null
            h264PacketCallback = null
        }
    }

    override fun disconnect() {
        if (active) sessionsDisconnected++
    }

    override fun disconnect(reason: VideoClientCloseReason) {
        disconnectReasons += reason
        disconnect()
    }

    override fun close() = disconnect()

    fun signalConnected() {
        requireNotNull(connectedCallback).invoke(
            VideoSessionInfo(
                codec = VideoProtocol.CODEC_ZLIB_BGRA8888,
                width = 640,
                height = 400,
                fps = 10,
            ),
        )
    }

    fun signalH264Connected() {
        requireNotNull(connectedCallback).invoke(
            VideoSessionInfo(
                codec = VideoProtocol.CODEC_H264,
                width = 1280,
                height = 800,
                fps = 30,
            ),
        )
    }

    fun emitFrame(sequence: Long, value: Int) {
        requireNotNull(frameCallback).invoke(frame(sequence, value))
    }

    fun captureFrameCallback(): (DecodedVideoFrame) -> Unit = requireNotNull(frameCallback)

    fun emitH264Packet(
        sequence: Long,
        isConfig: Boolean = false,
        isKeyFrame: Boolean = false,
    ) {
        var flags = if (isConfig) VideoProtocol.H264_FLAG_CONFIG else {
            VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT
        }
        if (isKeyFrame) flags = flags or VideoProtocol.H264_FLAG_KEYFRAME
        requireNotNull(h264PacketCallback).invoke(
            H264VideoPacket(
                header = H264PacketHeader(
                    sequence = sequence,
                    captureTimestampMicros = sequence * 100,
                    flags = flags,
                    payloadBytes = 5,
                    encodeDurationMicros = 100,
                ),
                payload = byteArrayOf(0, 0, 0, 1, if (isKeyFrame) 0x65 else 0x67),
                receivedAtNanos = sequence * 1_000,
            ),
        )
    }

    fun signalSessionEnded() {
        sessionCompletion.complete(Unit)
    }

    fun frame(sequence: Long, value: Int) = DecodedVideoFrame(
        sequence = sequence,
        captureTimestampMicros = sequence * 100,
        width = 1,
        height = 1,
        pixels = byteArrayOf(value.toByte(), 0, 0, 0xff.toByte()),
        pixelCountBytes = 4,
    )
}

class FakeH264DecoderController : H264DecoderController {
    var sessionsStarted = 0
        private set
    var sessionsStopped = 0
        private set
    val packets = mutableListOf<H264VideoPacket>()
    private var active = false
    private var renderedCallback: ((Long, Long, Double) -> Unit)? = null
    private var errorCallback: ((Long, Long?, String, Throwable) -> Unit)? = null
    private var activeGeneration = 0L

    override fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    ) {
        active = true
        activeGeneration = generation
        sessionsStarted++
        renderedCallback = onFrameRendered
        errorCallback = onError
    }

    override fun attachSurface(generation: Long, surface: Surface) = Unit

    override fun detachSurface(generation: Long, surface: Surface) = Unit

    override fun queuePacket(generation: Long, packet: H264VideoPacket) {
        packets += packet
    }

    override fun stopSession() {
        if (active) sessionsStopped++
        active = false
        renderedCallback = null
        errorCallback = null
        packets.clear()
    }

    override fun close() = stopSession()

    fun signalRendered(generation: Long, sequence: Long) {
        requireNotNull(renderedCallback).invoke(generation, sequence, 4.5)
    }

    fun signalError(error: Throwable) {
        requireNotNull(errorCallback).invoke(activeGeneration, null, "decoder_input", error)
    }
}
