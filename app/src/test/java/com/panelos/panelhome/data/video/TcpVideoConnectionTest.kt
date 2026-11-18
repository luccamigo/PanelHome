package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.VideoLogger
import com.panelos.panelhome.data.transport.AdbTcpAuthentication
import com.panelos.panelhome.data.transport.LogicalChannel

import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TcpVideoConnectionTest {
    @Test
    fun handshakeSucceedsAndDisconnectUnblocksAStaticVideoRead() = runBlocking {
        val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val serverFailure = AtomicReference<Throwable?>()
        val helloWidth = AtomicReference<Int?>()
        val serverThread = Thread {
            try {
                server.use {
                    it.accept().use { socket ->
                        authenticateVideo(socket)
                        val hello = ByteArray(VideoProtocol.HELLO_SIZE)
                        VideoProtocol.readExactly(socket.getInputStream(), hello)
                        helloWidth.set(
                            ByteBuffer.wrap(hello).order(ByteOrder.LITTLE_ENDIAN).getInt(8),
                        )
                        socket.getOutputStream().apply {
                            write(ack())
                            flush()
                        }
                        // No frames are sent. The client must stay blocked until disconnect closes it.
                        socket.getInputStream().read()
                    }
                }
            } catch (error: Throwable) {
                serverFailure.set(error)
            }
        }.apply {
            name = "TcpVideoConnectionTest-server"
            isDaemon = true
            start()
        }

        val connected = CountDownLatch(1)
        val connection = TcpVideoConnection(
            hello = VideoHello(
                maxWidth = 640,
                maxHeight = 400,
                maxFps = 10,
                supportedCodecMask = VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
            ),
            host = requireNotNull(InetAddress.getLoopbackAddress().hostAddress),
            port = server.localPort,
            logger = SilentVideoLogger,
            authenticationToken = AUTH_TOKEN,
        )
        val session = async(Dispatchers.IO) {
            runCatching {
                connection.receiveFrames(
                    onConnected = { connected.countDown() },
                    onFrame = { error("No frame was expected") },
                )
            }
        }

        assertTrue(connected.await(5, TimeUnit.SECONDS))
        assertEquals(640, helloWidth.get())
        connection.disconnect()
        withTimeout(2_000) { session.await() }
        connection.close()
        serverThread.join(2_000)
        assertNull(serverFailure.get())
    }

    @Test
    fun h264HandshakeAndPacketAreDeliveredWithoutBitmapDecode() = runBlocking {
        val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val serverFailure = AtomicReference<Throwable?>()
        val helloFps = AtomicReference<Int?>()
        val serverThread = Thread {
            try {
                server.use {
                    it.accept().use { socket ->
                        authenticateVideo(socket)
                        val hello = ByteArray(VideoProtocol.HELLO_SIZE)
                        VideoProtocol.readExactly(socket.getInputStream(), hello)
                        helloFps.set(
                            ByteBuffer.wrap(hello).order(ByteOrder.LITTLE_ENDIAN).getInt(16),
                        )
                        socket.getOutputStream().apply {
                            write(ack(VideoProtocol.CODEC_H264, 1280, 800, 30))
                            write(h264Header())
                            write(byteArrayOf(0, 0, 0, 1, 0x65))
                            flush()
                        }
                    }
                }
            } catch (error: Throwable) {
                serverFailure.set(error)
            }
        }.apply {
            isDaemon = true
            start()
        }

        val packet = AtomicReference<H264VideoPacket?>()
        val logger = RecordingVideoLogger()
        val connection = TcpVideoConnection(
            hello = VideoHello(),
            host = requireNotNull(InetAddress.getLoopbackAddress().hostAddress),
            port = server.localPort,
            logger = logger,
            authenticationToken = AUTH_TOKEN,
        )

        val result = runCatching {
            connection.receiveVideo(
                onConnected = { assertEquals(VideoProtocol.CODEC_H264, it.codec) },
                onFrame = { error("H.264 must not use the ZLIB frame callback") },
                onH264Packet = { packet.set(it) },
            )
        }

        assertEquals(9L, packet.get()?.header?.sequence)
        assertTrue(packet.get()?.header?.isKeyFrame == true)
        assertEquals(60, helloFps.get())
        assertTrue(result.isSuccess)
        assertTrue(logger.transitions.contains("Video session ended: peer_closed"))
        assertTrue(
            logger.transitions.any {
                it.startsWith("[VideoClientClose] reason=read_eof")
            },
        )
        connection.close()
        serverThread.join(2_000)
        assertNull(serverFailure.get())
    }

    @Test
    fun cancellationClosesSocketWithExplicitReason() = runBlocking {
        val server = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        val serverFailure = AtomicReference<Throwable?>()
        val serverThread = Thread {
            try {
                server.use {
                    it.accept().use { socket ->
                        authenticateVideo(socket)
                        val hello = ByteArray(VideoProtocol.HELLO_SIZE)
                        VideoProtocol.readExactly(socket.getInputStream(), hello)
                        socket.getOutputStream().apply {
                            write(ack())
                            flush()
                        }
                        socket.getInputStream().read()
                    }
                }
            } catch (error: Throwable) {
                serverFailure.set(error)
            }
        }.apply {
            isDaemon = true
            start()
        }
        val connected = CountDownLatch(1)
        val logger = RecordingVideoLogger()
        val connection = TcpVideoConnection(
            hello = VideoHello(
                maxWidth = 640,
                maxHeight = 400,
                maxFps = 10,
                supportedCodecMask = VideoProtocol.CODEC_MASK_ZLIB_BGRA8888,
            ),
            host = requireNotNull(InetAddress.getLoopbackAddress().hostAddress),
            port = server.localPort,
            logger = logger,
            authenticationToken = AUTH_TOKEN,
        )
        val session = async(Dispatchers.IO) {
            runCatching {
                connection.receiveFrames(
                    onConnected = { connected.countDown() },
                    onFrame = { error("No frame was expected") },
                )
            }
        }

        assertTrue(connected.await(5, TimeUnit.SECONDS))
        connection.disconnect(VideoClientCloseReason.CANCELLED)
        withTimeout(2_000) { session.await() }

        assertTrue(
            logger.transitions.any {
                it.startsWith("[VideoClientClose] reason=cancelled")
            },
        )
        connection.close()
        serverThread.join(2_000)
        assertNull(serverFailure.get())
    }

    private fun ack(
        codec: Int = VideoProtocol.CODEC_ZLIB_BGRA8888,
        width: Int = 640,
        height: Int = 400,
        fps: Int = 10,
    ): ByteArray = ByteBuffer.allocate(VideoProtocol.ACK_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf(0x50, 0x4e, 0x4c, 0x56))
        .putShort(VideoProtocol.VERSION.toShort())
        .putShort(VideoProtocol.TYPE_ACK.toShort())
        .putInt(codec)
        .putInt(width)
        .putInt(height)
        .putInt(fps)
        .array()

    private fun h264Header(): ByteArray =
        ByteBuffer.allocate(VideoProtocol.H264_PACKET_HEADER_SIZE)
        .order(ByteOrder.LITTLE_ENDIAN)
        .put(byteArrayOf(0x50, 0x4e, 0x4c, 0x56))
        .putShort(VideoProtocol.VERSION.toShort())
        .putShort(VideoProtocol.TYPE_H264_PACKET.toShort())
        .putLong(9)
        .putLong(900)
        .putInt(
            VideoProtocol.H264_FLAG_KEYFRAME or
                VideoProtocol.H264_FLAG_END_OF_ACCESS_UNIT,
        )
        .putInt(5)
        .putLong(1_500)
        .array()

    private fun authenticateVideo(socket: Socket) {
        val expected = AdbTcpAuthentication.encodeRequest(LogicalChannel.VIDEO, AUTH_TOKEN)
        val supplied = ByteArray(expected.size)
        VideoProtocol.readExactly(socket.getInputStream(), supplied)
        check(supplied.contentEquals(expected))
        socket.getOutputStream().apply {
            write(byteArrayOf('P'.code.toByte(), 'A'.code.toByte(), 'D'.code.toByte(), 'O'.code.toByte(), 1, 2, 0, 0))
            flush()
        }
    }

    private companion object {
        val AUTH_TOKEN = ByteArray(AdbTcpAuthentication.TOKEN_BYTES) { it.toByte() }
    }

    private class RecordingVideoLogger : VideoLogger {
        val transitions = mutableListOf<String>()

        override fun transition(message: String) {
            transitions += message
        }

        override fun debug(message: String) = Unit

        override fun error(stage: String, sequence: Long?, error: Throwable) = Unit
    }
}
