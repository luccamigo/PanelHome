package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger

import com.panelos.panelhome.data.transport.AdbTcpTransport
import com.panelos.panelhome.data.transport.LogicalChannel
import com.panelos.panelhome.data.transport.PanelByteConnection
import com.panelos.panelhome.data.transport.PanelTransport
import java.io.EOFException
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext

class TransportVideoConnection(
    private val hello: VideoHello? = null,
    private val host: String = LOOPBACK_HOST,
    private val port: Int = VIDEO_PORT,
    private val logger: VideoLogger = AndroidVideoLogger,
    private val h264Capabilities: H264DecoderCapabilityProvider =
        AndroidH264DecoderCapabilityProvider,
    authenticationToken: ByteArray? = null,
    private val transport: PanelTransport = AdbTcpTransport(
        host = host,
        videoPort = port,
        authenticationToken = authenticationToken,
    ),
) : VideoConnection {
    private val sessionActive = AtomicBoolean(false)
    private val resourceLock = Any()

    @Volatile
    private var closed = false

    @Volatile
    private var activeConnection: PanelByteConnection? = null

    @Volatile
    private var requestedCloseReason: VideoSessionEndReason? = null

    @Volatile
    private var requestedClientCloseReason: VideoClientCloseReason? = null

    @Volatile
    private var activeMetrics: VideoClientSessionMetrics? = null

    @Volatile
    private var runtimeState = VideoClientRuntimeState()

    override suspend fun receiveFrames(
        onConnected: () -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
    ) = receiveVideo(
        onConnected = { session ->
            if (session.codec != VideoProtocol.CODEC_ZLIB_BGRA8888) {
                throw VideoProtocolException(
                    "Legacy video consumer cannot accept codec ${session.codec}",
                )
            }
            onConnected()
        },
        onFrame = onFrame,
        onH264Packet = {
            throw VideoProtocolException("Legacy video consumer received H.264")
        },
    )

    override suspend fun receiveVideo(
        onConnected: (VideoSessionInfo) -> Unit,
        onFrame: (DecodedVideoFrame) -> Unit,
        onH264Packet: (H264VideoPacket) -> Unit,
    ) {
        check(sessionActive.compareAndSet(false, true)) { "A video session is already active" }
        try {
            withContext(Dispatchers.IO) {
                if (closed) return@withContext
                val decoderCapability = if (hello == null) {
                    runCatching(h264Capabilities::findDecoder)
                        .onFailure { logger.error("decoder_capability", null, it) }
                        .getOrNull()
                } else {
                    null
                }
                val sessionHello = hello ?: VideoHelloNegotiator.create(decoderCapability).also {
                    if (decoderCapability != null) {
                        logger.transition(
                            "H.264 decoder: ${decoderCapability.codecName}, " +
                                "hardware=${decoderCapability.isHardwareAccelerated}, " +
                                "softwareOnly=${decoderCapability.isSoftwareOnly}, " +
                                "vendor=${decoderCapability.isVendor}, " +
                                "lowLatency=${decoderCapability.supportsLowLatency}, " +
                                "clientMaxFps=${decoderCapability.clientMaxFps}, " +
                                "maxSupportedInstances=" +
                                "${decoderCapability.maxSupportedInstances ?: "unknown"}",
                        )
                    }
                }
                logger.transition(
                    "Video capability: max=${sessionHello.maxWidth}x${sessionHello.maxHeight} " +
                        "maxFps=${sessionHello.maxFps} " +
                        "codecMask=0x${sessionHello.supportedCodecMask.toString(16)}",
                )
                val sessionMetrics = VideoClientSessionMetrics()
                val connection = transport.connect(LogicalChannel.VIDEO)
                synchronized(resourceLock) {
                    if (closed) {
                        sessionMetrics.logClose(
                            logger,
                            VideoClientCloseReason.APP_STOP,
                            null,
                            runtimeState,
                        )
                        connection.close()
                        return@withContext
                    }
                    requestedCloseReason = null
                    requestedClientCloseReason = null
                    activeConnection = connection
                    activeMetrics = sessionMetrics
                }

                var endReason = VideoSessionEndReason.UNKNOWN
                var failureCloseReason: VideoClientCloseReason? = null
                var closeException: Throwable? = null
                try {
                    logger.transition("Video transport connected: ${transport.name}")
                    connection.setReadTimeout(HANDSHAKE_TIMEOUT_MILLIS)
                    val output = connection.output
                    try {
                        output.write(VideoProtocol.encodeHello(sessionHello))
                        output.flush()
                    } catch (error: Exception) {
                        failureCloseReason = VideoClientCloseReason.WRITE_EXCEPTION
                        closeException = error
                        throw error
                    }
                    logger.transition("Video hello sent")

                    val handshakeBytes = ByteArray(VideoProtocol.ACK_SIZE)
                    VideoProtocol.readExactly(connection.input, handshakeBytes)
                    val accepted = when (val response = VideoProtocol.parseHandshake(handshakeBytes)) {
                        is VideoHandshakeResponse.Accepted -> response
                        is VideoHandshakeResponse.Rejected -> throw VideoProtocolException(
                            "PanelHost rejected video connection with code ${response.errorCode}",
                        )
                    }
                    if (accepted.maxWidth > sessionHello.maxWidth ||
                        accepted.maxHeight > sessionHello.maxHeight
                    ) {
                        throw VideoProtocolException("PanelHost selected dimensions above client limits")
                    }
                    if (accepted.maxFps > sessionHello.maxFps) {
                        throw VideoProtocolException("PanelHost selected FPS above the client limit")
                    }
                    val selectedMask = when (accepted.codec) {
                        VideoProtocol.CODEC_H264 -> VideoProtocol.CODEC_MASK_H264
                        VideoProtocol.CODEC_ZLIB_BGRA8888 ->
                            VideoProtocol.CODEC_MASK_ZLIB_BGRA8888
                        else -> 0
                    }
                    if (sessionHello.supportedCodecMask and selectedMask == 0) {
                        throw VideoProtocolException(
                            "PanelHost selected an unadvertised codec: ${accepted.codec}",
                        )
                    }
                    logger.transition(
                        "Video ACK received: ${accepted.maxWidth}x${accepted.maxHeight} " +
                            "at ${accepted.maxFps} FPS, codec=${accepted.codec}",
                    )

                    // A static desktop may legitimately produce no new swapchain frames. STOP and
                    // Closing the transport connection immediately unblocks this blocking read.
                    connection.setReadTimeout(0)
                    onConnected(
                        VideoSessionInfo(
                            codec = accepted.codec,
                            width = accepted.maxWidth,
                            height = accepted.maxHeight,
                            fps = accepted.maxFps,
                        ),
                    )
                    val input = connection.input
                    if (accepted.codec == VideoProtocol.CODEC_H264) {
                        receiveH264Packets(
                            input,
                            connection,
                            sessionMetrics,
                            onH264Packet,
                        )
                    } else {
                        VideoFrameDecoder(
                            maxWidth = accepted.maxWidth,
                            maxHeight = accepted.maxHeight,
                            logger = logger,
                        ).use { decoder ->
                            while (!closed && !connection.isClosed) {
                                currentCoroutineContext().ensureActive()
                                val frame = decoder.readFrame(input) ?: continue
                                try {
                                    onFrame(frame)
                                } catch (error: Exception) {
                                    logger.error("frame_callback", frame.sequence, error)
                                    throw error
                                }
                            }
                        }
                    }
                } catch (peerClosed: VideoPeerClosedException) {
                    endReason = VideoSessionEndReason.PEER_CLOSED
                    failureCloseReason = VideoClientCloseReason.READ_EOF
                    return@withContext
                } catch (cancelled: CancellationException) {
                    endReason = requestedCloseReason ?: VideoSessionEndReason.CANCELLATION
                    failureCloseReason = requestedClientCloseReason ?:
                        VideoClientCloseReason.CANCELLED
                    closeException = cancelled
                    throw cancelled
                } catch (eof: EOFException) {
                    if (isExpectedClosure(connection)) {
                        endReason = requestedCloseReason ?: VideoSessionEndReason.LOCAL_DISCONNECT
                        return@withContext
                    }
                    endReason = VideoSessionEndReason.PROTOCOL_ERROR
                    failureCloseReason = VideoClientCloseReason.PROTOCOL_ERROR
                    closeException = eof
                    logger.error("transport_read", null, eof)
                    throw eof
                } catch (error: Exception) {
                    if (isExpectedClosure(connection)) {
                        endReason = requestedCloseReason ?: VideoSessionEndReason.LOCAL_DISCONNECT
                        return@withContext
                    }
                    endReason = if (error is VideoProtocolException) {
                        VideoSessionEndReason.PROTOCOL_ERROR
                    } else {
                        VideoSessionEndReason.TRANSPORT_FAILURE
                    }
                    failureCloseReason = failureCloseReason ?: if (
                        error is VideoProtocolException
                    ) {
                        VideoClientCloseReason.PROTOCOL_ERROR
                    } else {
                        VideoClientCloseReason.READ_EXCEPTION
                    }
                    closeException = error
                    logger.error("video_session", null, error)
                    throw error
                } finally {
                    synchronized(resourceLock) {
                        if (activeConnection === connection) activeConnection = null
                        if (activeMetrics === sessionMetrics) activeMetrics = null
                    }
                    val clientCloseReason = requestedClientCloseReason ?:
                        failureCloseReason ?: when (endReason) {
                            VideoSessionEndReason.PEER_CLOSED ->
                                VideoClientCloseReason.READ_EOF
                            VideoSessionEndReason.CANCELLATION ->
                                VideoClientCloseReason.CANCELLED
                            VideoSessionEndReason.PROTOCOL_ERROR ->
                                VideoClientCloseReason.PROTOCOL_ERROR
                            VideoSessionEndReason.TRANSPORT_FAILURE ->
                                VideoClientCloseReason.READ_EXCEPTION
                            VideoSessionEndReason.CLIENT_SHUTDOWN ->
                                VideoClientCloseReason.APP_STOP
                            else -> VideoClientCloseReason.RECONNECT
                        }
                    sessionMetrics.logClose(
                        logger,
                        clientCloseReason,
                        closeException,
                        runtimeState,
                    )
                    closeConnection(connection, "transport_close")
                    if (endReason == VideoSessionEndReason.UNKNOWN) {
                        endReason = requestedCloseReason ?: when {
                            closed -> VideoSessionEndReason.CLIENT_SHUTDOWN
                            else -> VideoSessionEndReason.UNKNOWN
                        }
                    }
                    logger.transition("Video session ended: ${endReason.logName}")
                }
            }
        } finally {
            sessionActive.set(false)
        }
    }

    private suspend fun receiveH264Packets(
        input: java.io.InputStream,
        connection: PanelByteConnection,
        metrics: VideoClientSessionMetrics,
        onPacket: (H264VideoPacket) -> Unit,
    ) {
        val headerBytes = ByteArray(VideoProtocol.H264_PACKET_HEADER_SIZE)
        var debugPackets = 0
        while (!closed && !connection.isClosed) {
            currentCoroutineContext().ensureActive()
            if (!VideoProtocol.readMessageHeader(input, headerBytes)) {
                throw VideoPeerClosedException()
            }
            val header = VideoProtocol.parseH264PacketHeader(headerBytes)
            val payload = ByteArray(header.payloadBytes)
            VideoProtocol.readExactly(input, payload)
            metrics.packetReceived(
                VideoProtocol.H264_PACKET_HEADER_SIZE + header.payloadBytes,
            )
            // TCP already preserves encoder output order. Capture sequences are
            // presentation identities and can be non-monotonic in decode order
            // when an MFT emits reordered pictures, so do not drop by sequence.
            if (debugPackets++ < DEBUG_PACKET_LIMIT) {
                logger.debug(
                    "H.264 packet received: sequence=${header.sequence} " +
                        "flags=0x${header.flags.toString(16)} bytes=${header.payloadBytes} " +
                        "encode=${header.encodeDurationMicros / 1000.0}ms",
                )
            }
            try {
                onPacket(H264VideoPacket(header, payload))
                if (!header.isConfig) metrics.accessUnitQueued()
                metrics.maybeLog(logger, runtimeState)
            } catch (error: Exception) {
                logger.error("h264_packet_callback", header.sequence, error)
                throw error
            }
        }
    }

    override fun disconnect() {
        disconnect(VideoClientCloseReason.RECONNECT)
    }

    override fun disconnect(reason: VideoClientCloseReason) {
        val connection = synchronized(resourceLock) {
            if (requestedCloseReason == null) {
                requestedCloseReason = VideoSessionEndReason.LOCAL_DISCONNECT
            }
            requestedClientCloseReason = reason
            activeConnection.also { activeConnection = null }
        }
        activeMetrics?.logClose(logger, reason, null, runtimeState)
        connection?.let { closeConnection(it, "transport_disconnect") }
    }

    override fun updateRuntimeState(state: VideoClientRuntimeState) {
        runtimeState = state
    }

    override fun close() {
        closed = true
        requestedCloseReason = VideoSessionEndReason.CLIENT_SHUTDOWN
        disconnect(VideoClientCloseReason.APP_STOP)
    }

    private fun closeConnection(connection: PanelByteConnection, stage: String) {
        try {
            connection.close()
        } catch (error: Exception) {
            logger.error(stage, null, error)
        }
    }

    private fun isExpectedClosure(connection: PanelByteConnection): Boolean =
        closed || synchronized(resourceLock) { activeConnection !== connection }

    private companion object {
        const val LOOPBACK_HOST = "127.0.0.1"
        const val VIDEO_PORT = 8766
        const val HANDSHAKE_TIMEOUT_MILLIS = 5_000
        const val DEBUG_PACKET_LIMIT = 3
    }
}

/** Source-compatible name retained while ADB/TCP remains the default carrier. */
typealias TcpVideoConnection = TransportVideoConnection

internal enum class VideoSessionEndReason(val logName: String) {
    UNKNOWN("unknown"),
    PEER_CLOSED("peer_closed"),
    LOCAL_DISCONNECT("local_disconnect"),
    CLIENT_SHUTDOWN("client_shutdown"),
    CANCELLATION("cancellation"),
    PROTOCOL_ERROR("protocol_error"),
    TRANSPORT_FAILURE("transport_failure"),
}
