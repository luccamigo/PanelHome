package com.panelos.panelhome.data.connection

import com.panelos.panelhome.domain.protocol.CommandMessage
import com.panelos.panelhome.domain.protocol.DesktopStateMessage
import com.panelos.panelhome.domain.protocol.ErrorMessage
import com.panelos.panelhome.domain.protocol.HelloAckMessage
import com.panelos.panelhome.domain.protocol.HelloMessage
import com.panelos.panelhome.domain.protocol.LoopMediaChunkMessage
import com.panelos.panelhome.domain.protocol.LoopMediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelMessageRejectedException
import com.panelos.panelhome.domain.protocol.MediaArtworkMessage
import com.panelos.panelhome.domain.protocol.MediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.PanelProtocolErrorCode
import com.panelos.panelhome.domain.protocol.PanelProtocolException
import com.panelos.panelhome.domain.protocol.PanelProtocolJson
import com.panelos.panelhome.domain.protocol.PanelBuildIdentity
import com.panelos.panelhome.domain.protocol.PingMessage
import com.panelos.panelhome.domain.protocol.PongMessage
import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeResultMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.protocol.TelemetryMessage
import com.panelos.panelhome.domain.protocol.NetworkRatesMessage
import com.panelos.panelhome.domain.protocol.ProcessesMessage
import com.panelos.panelhome.domain.protocol.ProcessTerminateResultMessage
import com.panelos.panelhome.domain.protocol.PanelMessageType
import com.panelos.panelhome.domain.protocol.LocalNoticesMessage
import com.panelos.panelhome.domain.protocol.ClaudePermissionsMessage
import org.json.JSONObject
import com.panelos.panelhome.domain.protocol.TouchCommand
import com.panelos.panelhome.domain.protocol.TouchCommandAction
import com.panelos.panelhome.domain.protocol.VolumeStateMessage
import com.panelos.panelhome.data.video.AndroidVideoLogger
import com.panelos.panelhome.data.transport.AdbTcpTransport
import com.panelos.panelhome.data.transport.LogicalChannel
import com.panelos.panelhome.data.transport.PanelByteConnection
import com.panelos.panelhome.data.transport.PanelTransport
import java.io.BufferedWriter
import java.io.EOFException
import java.io.OutputStreamWriter
import java.net.SocketTimeoutException
import java.nio.charset.StandardCharsets
import java.util.Locale
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

class TransportPanelConnection(
    private val touchDebugLog: (String) -> Unit = AndroidVideoLogger::debug,
    private val transport: PanelTransport = AdbTcpTransport(),
    private val hello: HelloMessage = HelloMessage(
        buildIdentity = PanelBuildIdentity.current.identity,
        buildNumber = PanelBuildIdentity.current.number,
    ),
) : PanelConnection {
    private val mutableEvents = MutableSharedFlow<PanelConnectionEvent>(
        extraBufferCapacity = EVENT_BUFFER_CAPACITY,
    )
    override val events: Flow<PanelConnectionEvent> = mutableEvents.asSharedFlow()

    private val maintaining = AtomicBoolean(false)
    private val nextConnectionId = AtomicLong()
    private val writerMutex = Mutex()
    private val resourceLock = Any()
    private val touchSend = AtomicLong()
    private val touchMoveDebug = AtomicLong()

    @Volatile
    private var closed = false

    @Volatile
    private var activeConnection: PanelByteConnection? = null

    @Volatile
    private var activeWriterSession: ActiveWriterSession? = null

    override suspend fun maintainConnection() {
        if (!maintaining.compareAndSet(false, true)) return

        try {
            withContext(Dispatchers.IO) {
                var retryIndex = 0
                while (!closed) {
                    mutableEvents.emit(PanelConnectionEvent.Connecting)
                    var handshakeCompleted = false

                    try {
                        runSession {
                            handshakeCompleted = true
                            retryIndex = 0
                        }
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (error: Exception) {
                        // A failed connection is represented by the typed event below.
                        touchDebugLog(
                            "[ControlSession] ${error::class.java.simpleName}: " +
                                (error.message ?: "no message"),
                        )
                    } finally {
                        clearActiveResources()
                    }

                    if (!closed) {
                        mutableEvents.emit(PanelConnectionEvent.Disconnected)
                        val delayMillis = RETRY_DELAYS_MILLIS[
                            retryIndex.coerceAtMost(RETRY_DELAYS_MILLIS.lastIndex)
                        ]
                        if (handshakeCompleted || retryIndex < RETRY_DELAYS_MILLIS.lastIndex) {
                            retryIndex++
                        }
                        delay(delayMillis)
                    }
                }
            }
        } finally {
            maintaining.set(false)
            clearActiveResources()
            mutableEvents.tryEmit(PanelConnectionEvent.Disconnected)
        }
    }

    override suspend fun send(command: PanelCommand, expectedConnectionId: Long) {
        val writerSession = activeWriterSession
            ?.takeIf { it.connectionId == expectedConnectionId }
            ?: return
        try {
            withContext(Dispatchers.IO) {
                writeCommandIfActive(writerSession, CommandMessage(command))
            }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            if (command is TouchCommand && command.action != TouchCommandAction.MOVE) {
                touchDebugLog(
                    "[TouchDebug] SEND_FAILED action=${command.action} " +
                        "generation=${command.generation} type=${error::class.java.simpleName} " +
                        "message=${error.message ?: "none"}",
                )
            }
            closeConnectionUsing(writerSession)
        }
    }

    override fun close() {
        closed = true
        closeActiveConnection()
        mutableEvents.tryEmit(PanelConnectionEvent.Disconnected)
    }

    private suspend fun runSession(onHandshakeCompleted: () -> Unit) {
        val connection = transport.connect(LogicalChannel.CONTROL)
        synchronized(resourceLock) {
            if (closed) {
                connection.close()
                return
            }
            activeConnection = connection
        }

        connection.use { connectedConnection ->
            connectedConnection.setReadTimeout(HANDSHAKE_TIMEOUT_MILLIS)

            BoundedUtf8LineReader(connectedConnection.input).let { reader ->
                BufferedWriter(
                    OutputStreamWriter(connectedConnection.output, StandardCharsets.UTF_8),
                ).use { writer ->
                    writeMessage(writer, hello)
                    val response = reader.readLine()
                        ?: throw EOFException("PanelHost closed before the handshake")
                    val acknowledgement = PanelProtocolJson.decode(response)
                    validateHandshake(acknowledgement, writer)

                    val writerSession = writerMutex.withLock {
                        synchronized(resourceLock) {
                            if (activeConnection === connectedConnection && !closed) {
                                ActiveWriterSession(
                                    connectionId = nextConnectionId.incrementAndGet(),
                                    writer = writer,
                                ).also { activeWriterSession = it }
                            } else {
                                null
                            }
                        }
                    } ?: return

                    val helloAck = acknowledgement as HelloAckMessage
                    if (
                        helloAck.buildNumber != null &&
                        hello.buildNumber != null &&
                        helloAck.buildNumber > hello.buildNumber
                    ) {
                        touchDebugLog(
                            "[BuildIdentity] WARNING host=${helloAck.buildIdentity ?: "unknown"} " +
                                "(${helloAck.buildNumber}) is newer than " +
                                "client=${hello.buildIdentity ?: "unknown"} (${hello.buildNumber}); " +
                                "protocol ${helloAck.protocolVersion} remains compatible",
                        )
                    }
                    touchDebugLog(
                        "[TouchDebug] ACK features=${helloAck.features.sorted()} " +
                            "touch=${PanelProtocol.FEATURE_TOUCH in helloAck.features}",
                    )
                    mutableEvents.emit(
                        PanelConnectionEvent.Connected(
                            connectionId = writerSession.connectionId,
                            hostName = helloAck.hostName,
                            features = helloAck.features.toSet(),
                            hostBuildIdentity = helloAck.buildIdentity,
                            hostBuildNumber = helloAck.buildNumber,
                        ),
                    )
                    onHandshakeCompleted()

                    connectedConnection.setReadTimeout(READ_POLL_TIMEOUT_MILLIS)
                    receiveMessages(reader, writer, helloAck.features.toSet())
                }
            }
        }
    }

    private suspend fun validateHandshake(
        response: PanelMessage,
        writer: BufferedWriter,
    ) {
        when (response) {
            is HelloAckMessage -> {
                if (response.protocolVersion != PanelProtocol.PROTOCOL_VERSION) {
                    writeMessage(
                        writer,
                        ErrorMessage(PanelProtocolErrorCode.UNSUPPORTED_PROTOCOL),
                    )
                    throw PanelProtocolException(
                        "Unsupported protocol version: ${response.protocolVersion}",
                    )
                }
            }

            is ErrorMessage -> throw PanelProtocolException(
                "PanelHost rejected the handshake: ${response.code}",
            )

            else -> throw PanelProtocolException(
                "Expected hello_ack, received ${response.type.wireValue}",
            )
        }
    }

    private suspend fun receiveMessages(
        reader: BoundedUtf8LineReader,
        writer: BufferedWriter,
        features: Set<String>,
    ) {
        var lastInboundAt = System.nanoTime()
        var nextPingAt = lastInboundAt + HEARTBEAT_INTERVAL_NANOS

        while (!closed) {
            currentCoroutineContext().ensureActive()
            val now = System.nanoTime()
            if (now >= nextPingAt) {
                writeMessage(writer, PingMessage)
                nextPingAt = now + HEARTBEAT_INTERVAL_NANOS
            }
            if (now - lastInboundAt >= HEARTBEAT_TIMEOUT_NANOS) {
                throw SocketTimeoutException("PanelHost heartbeat timed out")
            }

            val line = try {
                reader.readLine()
            } catch (_: SocketTimeoutException) {
                continue
            } ?: throw EOFException("PanelHost closed the connection")

            lastInboundAt = System.nanoTime()
            val decoded = try {
                PanelProtocolJson.decode(line)
            } catch (rejected: PanelMessageRejectedException) {
                touchDebugLog("[ControlSession] Dropped message: ${rejected.message}")
                continue
            } catch (error: PanelProtocolException) {
                val type = runCatching { PanelMessageType.fromWireValue(JSONObject(line).getString("type")) }.getOrNull()
                val negotiated = when (type) {
                    PanelMessageType.TELEMETRY, PanelMessageType.NETWORK_RATES -> PanelProtocol.FEATURE_COMPUTER_STATS in features
                    PanelMessageType.PROCESSES, PanelMessageType.PROCESS_TERMINATE_RESULT -> PanelProtocol.FEATURE_PROCESSES in features
                    else -> false
                }
                if (!negotiated || type == null) throw error
                // A corrupt sample is a gap, not a reason to reset a healthy carrier.
                mutableEvents.emit(PanelConnectionEvent.MonitoringDataInvalid(type))
                continue
            }
            when (val message = decoded) {
                is LocalNoticesMessage -> mutableEvents.emit(PanelConnectionEvent.LocalNoticesReceived(message))
                is ClaudePermissionsMessage -> mutableEvents.emit(PanelConnectionEvent.ClaudePermissionsReceived(message))
                is NetworkRatesMessage -> mutableEvents.emit(PanelConnectionEvent.NetworkRatesReceived(message))
                is ProcessesMessage -> mutableEvents.emit(PanelConnectionEvent.ProcessesReceived(message))
                is ProcessTerminateResultMessage -> mutableEvents.emit(PanelConnectionEvent.ProcessTerminateResultReceived(message))
                is TelemetryMessage -> mutableEvents.emit(
                    PanelConnectionEvent.TelemetryReceived(message),
                )

                is MediaStateMessage -> mutableEvents.emit(
                    PanelConnectionEvent.MediaStateReceived(message),
                )

                is MediaArtworkMessage -> mutableEvents.emit(
                    PanelConnectionEvent.MediaArtworkReceived(
                        mediaId = message.mediaId,
                        artwork = MediaArtworkDecoder.decode(message),
                    ),
                )

                is VolumeStateMessage -> mutableEvents.emit(
                    PanelConnectionEvent.VolumeStateReceived(message),
                )

                is DesktopStateMessage -> mutableEvents.emit(
                    PanelConnectionEvent.DesktopStateReceived(message),
                )

                is StreamDeckLayoutMessage -> mutableEvents.emit(
                    PanelConnectionEvent.StreamDeckLayoutReceived(message),
                )

                is StreamDeckInvokeResultMessage -> mutableEvents.emit(
                    PanelConnectionEvent.StreamDeckInvokeResultReceived(message),
                )

                is StreamDeckIconMessage -> mutableEvents.emit(
                    PanelConnectionEvent.StreamDeckIconReceived(message),
                )

                is LoopMediaStateMessage -> mutableEvents.emit(
                    PanelConnectionEvent.LoopMediaStateReceived(message),
                )

                is LoopMediaChunkMessage -> mutableEvents.emit(
                    PanelConnectionEvent.LoopMediaChunkReceived(message),
                )

                PingMessage -> writeMessage(writer, PongMessage)
                PongMessage -> Unit
                is ErrorMessage -> throw PanelProtocolException(
                    "PanelHost reported protocol error: ${message.code}",
                )

                else -> Unit
            }
        }
    }

    private suspend fun writeMessage(writer: BufferedWriter, message: PanelMessage) {
        writerMutex.withLock {
            val line = PanelProtocolJson.encodeLine(message)
            requireOutgoingMessageFits(line)
            writer.write(line)
            writer.flush()
        }
    }

    private suspend fun writeCommandIfActive(
        writerSession: ActiveWriterSession,
        message: CommandMessage,
    ) {
        writerMutex.withLock {
            val isStillActive = synchronized(resourceLock) {
                activeWriterSession?.matches(
                    expectedConnectionId = writerSession.connectionId,
                    expectedWriter = writerSession.writer,
                ) == true
            }
            val touch = message.command as? TouchCommand
            if (!isStillActive) {
                if (touch != null && touch.action != TouchCommandAction.MOVE) {
                    touchDebugLog(
                        "[TouchDebug] SEND_REJECT reason=writer_session_inactive " +
                            "action=${touch.action} generation=${touch.generation}",
                    )
                }
                return
            }

            val logThisEvent = touch != null &&
                (touch.action != TouchCommandAction.MOVE || touchMoveDebug.getAndIncrement() < 3)
            if (touch != null && logThisEvent) {
                val coordinates = if (touch.x != null && touch.y != null) {
                    " norm=(%.3f,%.3f)".format(Locale.US, touch.x, touch.y)
                } else {
                    ""
                }
                touchDebugLog(
                    "[TouchDebug] SEND ${touch.action} pointer=${touch.pointerId} " +
                        "generation=${touch.generation}$coordinates",
                )
            }
            val line = PanelProtocolJson.encodeLine(message)
            requireOutgoingMessageFits(line)
            writerSession.writer.write(line)
            writerSession.writer.flush()
            if (touch != null) {
                val sent = touchSend.incrementAndGet()
                if (logThisEvent) {
                    touchDebugLog(
                        "[TouchDebug] SENT ${touch.action} touchSend=$sent",
                    )
                }
            }
        }
    }

    private fun clearActiveResources() {
        val connection = synchronized(resourceLock) {
            activeWriterSession = null
            activeConnection.also { activeConnection = null }
        }
        runCatching { connection?.close() }
    }

    private fun requireOutgoingMessageFits(line: String) {
        val bytes = line.dropLast(1).toByteArray(StandardCharsets.UTF_8).size
        if (bytes > PanelProtocol.MAX_CONTROL_MESSAGE_BYTES) {
            throw PanelProtocolException("Outgoing JSONL message exceeds the byte limit")
        }
    }

    private fun closeActiveConnection() {
        val connection = synchronized(resourceLock) {
            activeWriterSession = null
            activeConnection
        }
        runCatching { connection?.close() }
    }

    private fun closeConnectionUsing(writerSession: ActiveWriterSession) {
        val connection = synchronized(resourceLock) {
            if (activeWriterSession?.matches(
                    expectedConnectionId = writerSession.connectionId,
                    expectedWriter = writerSession.writer,
                ) != true
            ) {
                return
            }
            activeWriterSession = null
            activeConnection
        }
        runCatching { connection?.close() }
    }

    private companion object {
        const val HANDSHAKE_TIMEOUT_MILLIS = 5_000
        const val READ_POLL_TIMEOUT_MILLIS = 5_000
        const val EVENT_BUFFER_CAPACITY = 32
        const val HEARTBEAT_INTERVAL_MILLIS = 5_000L
        const val HEARTBEAT_TIMEOUT_MILLIS = 15_000L
        const val HEARTBEAT_INTERVAL_NANOS = HEARTBEAT_INTERVAL_MILLIS * 1_000_000
        const val HEARTBEAT_TIMEOUT_NANOS = HEARTBEAT_TIMEOUT_MILLIS * 1_000_000

        val RETRY_DELAYS_MILLIS = longArrayOf(1_000, 2_000, 3_000, 5_000)
    }
}

/** Source-compatible name retained for existing tests and callers. */
typealias TcpPanelConnection = TransportPanelConnection

internal data class ActiveWriterSession(
    val connectionId: Long,
    val writer: BufferedWriter,
) {
    fun matches(expectedConnectionId: Long, expectedWriter: BufferedWriter): Boolean =
        connectionId == expectedConnectionId && writer === expectedWriter
}
