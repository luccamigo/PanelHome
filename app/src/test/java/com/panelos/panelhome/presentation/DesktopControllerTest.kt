package com.panelos.panelhome.presentation

import android.view.Surface
import com.panelos.panelhome.data.video.DecodedVideoFrame
import com.panelos.panelhome.data.video.H264DecoderController
import com.panelos.panelhome.data.video.H264PacketHeader
import com.panelos.panelhome.data.video.H264VideoPacket
import com.panelos.panelhome.data.video.LatestVideoFrameStore
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.data.video.VideoClientCloseReason
import com.panelos.panelhome.data.video.VideoConnection
import com.panelos.panelhome.data.video.VideoProtocol
import com.panelos.panelhome.data.video.VideoSessionInfo
import com.panelos.panelhome.domain.model.DesktopVideoStatus
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Test

class DesktopControllerTest {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val video = ControllerVideoConnection()
    private var desired = true
    private val controller = DesktopController(
        scope = scope,
        videoConnection = video,
        frameStore = LatestVideoFrameStore(),
        h264Decoder = ControllerDecoder(),
        logger = SilentVideoLogger,
        videoDesired = { desired },
    )

    @After
    fun tearDown() {
        scope.cancel()
    }

    @Test
    fun retryBackoffGrowsExponentiallyAndIsBounded() {
        val backoff = VideoRetryBackoff()
        assertEquals(listOf(1000L, 2000L, 4000L, 8000L, 16000L, 30000L, 30000L),
            List(7) { backoff.nextDelayMillis() })
        assertEquals(1000L, VideoRetryBackoff().nextDelayMillis())
    }

    // h264Flags != null connects as H.264 and delivers one packet per flag value (config or data).
    private class Script(
        val connect: Boolean,
        val frame: Boolean,
        val lastsMillis: Long = 0L,
        val h264Flags: List<Int>? = null,
    )

    private fun scriptedController(vararg sessions: Script): Pair<List<Long>, DesktopController> {
        val delays = mutableListOf<Long>()
        var now = 0L
        val queue = ArrayDeque(sessions.toList())
        val connection = object : VideoConnection by ControllerVideoConnection() {
            override suspend fun receiveVideo(
                onConnected: (VideoSessionInfo) -> Unit,
                onFrame: (DecodedVideoFrame) -> Unit,
                onH264Packet: (H264VideoPacket) -> Unit,
            ) {
                val next = queue.removeFirstOrNull() ?: run {
                    CompletableDeferred<Unit>().await()
                    return
                }
                if (next.connect) {
                    val codec = if (next.h264Flags != null) VideoProtocol.CODEC_H264 else VideoProtocol.CODEC_ZLIB_BGRA8888
                    onConnected(VideoSessionInfo(codec, 640, 400, 10))
                }
                if (next.frame) onFrame(DecodedVideoFrame(1, 0, 1, 1, ByteArray(4), 4))
                next.h264Flags?.forEach { flags ->
                    onH264Packet(H264VideoPacket(H264PacketHeader(1, 0, flags, 1, 0), ByteArray(1)))
                }
                now += next.lastsMillis
            }
        }
        val scripted = DesktopController(
            scope = scope, videoConnection = connection, frameStore = LatestVideoFrameStore(),
            h264Decoder = ControllerDecoder(), logger = SilentVideoLogger, videoDesired = { true },
            retryDelay = { delays += it }, elapsedMillis = { now },
        )
        scripted.reconcile(VideoClientCloseReason.DESKTOP_EXIT)
        return delays to scripted
    }

    @Test
    fun backoffResetsToOneSecondAfterASessionThatReceivedFrames() {
        // Five connect-only failures climb to 16 s; one framed session returns the next wait to 1 s.
        val (delays, _) = scriptedController(
            Script(connect = false, frame = false), Script(false, false), Script(false, false),
            Script(false, false), Script(false, false),
            Script(connect = true, frame = true), Script(false, false),
        )
        assertEquals(listOf(1000L, 2000L, 4000L, 8000L, 16000L, 1000L, 2000L), delays)
    }

    @Test
    fun h264DataPacketMarksTheSessionHealthyButConfigPacketDoesNot() {
        val config = VideoProtocol.H264_FLAG_CONFIG
        val data = VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT
        // Config-only sessions (a decoder that never gets a picture) keep climbing; a data packet resets.
        val (delays, _) = scriptedController(
            Script(true, false, h264Flags = listOf(config)), Script(true, false, h264Flags = listOf(config)),
            Script(true, false, h264Flags = listOf(config)), Script(true, false, h264Flags = listOf(config, data)),
            Script(true, false, h264Flags = listOf(config)),
        )
        assertEquals(listOf(1000L, 2000L, 4000L, 1000L, 2000L), delays)
    }

    @Test
    fun repeatedCableDropsAfterFramesNeverLeaveTheOneSecondRetry() {
        val (delays, _) = scriptedController(*Array(8) { Script(connect = true, frame = true) })
        assertEquals(List(8) { 1000L }, delays)
    }

    @Test
    fun longHealthySessionWithoutFramesAlsoResetsButShortOneDoesNot() {
        val (delays, _) = scriptedController(
            Script(true, false, lastsMillis = 1_000), Script(true, false, lastsMillis = 9_999),
            Script(true, false, lastsMillis = 10_000), Script(false, false),
        )
        assertEquals(listOf(1000L, 2000L, 1000L, 2000L), delays)
    }

    @Test
    fun desiredDesktopStartsVideoAndExitStopsAndClearsIt() {
        controller.reconcile(VideoClientCloseReason.DESKTOP_EXIT)
        assertEquals(DesktopVideoStatus.CONNECTING, controller.state.value.status)
        assertEquals(1, video.sessionsStarted)

        video.signalConnected()
        assertEquals(DesktopVideoStatus.WAITING_FOR_FIRST_FRAME, controller.state.value.status)

        desired = false
        controller.reconcile(VideoClientCloseReason.DESKTOP_EXIT)

        assertEquals(DesktopVideoStatus.IDLE, controller.state.value.status)
        assertEquals(VideoClientCloseReason.DESKTOP_EXIT, video.reasons.last())
    }
}

private class ControllerVideoConnection : VideoConnection {
    var sessionsStarted = 0
    val reasons = mutableListOf<VideoClientCloseReason>()
    private var onConnected: ((VideoSessionInfo) -> Unit)? = null
    private var completion = CompletableDeferred<Unit>()

    override suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    ) = receiveVideo({ onConnected() }, onFrame, {})

    override suspend fun receiveVideo(
        onConnected: (VideoSessionInfo) -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
        onH264Packet: (H264VideoPacket) -> Unit,
    ) {
        sessionsStarted++
        this.onConnected = onConnected
        completion = CompletableDeferred()
        completion.await()
    }

    override fun disconnect() = Unit

    override fun disconnect(reason: VideoClientCloseReason) {
        reasons += reason
    }

    override fun close() = Unit

    fun signalConnected() {
        requireNotNull(onConnected)(
            VideoSessionInfo(VideoProtocol.CODEC_ZLIB_BGRA8888, 640, 400, 10),
        )
    }
}

private class ControllerDecoder : H264DecoderController {
    override fun beginSession(
        generation: Long,
        session: VideoSessionInfo,
        onFrameRendered: (Long, Long, Double) -> Unit,
        onError: (Long, Long?, String, Throwable) -> Unit,
    ) = Unit

    override fun attachSurface(generation: Long, surface: Surface) = Unit
    override fun detachSurface(generation: Long, surface: Surface) = Unit
    override fun queuePacket(generation: Long, packet: H264VideoPacket) = Unit
    override fun stopSession() = Unit
    override fun close() = Unit
}
