package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.connection.PanelConnectionEvent
import com.panelos.panelhome.data.video.SilentVideoLogger
import com.panelos.panelhome.domain.protocol.LoopMediaChunkMessage
import com.panelos.panelhome.domain.protocol.LoopMediaErrorCode
import com.panelos.panelhome.domain.protocol.LoopMediaKind
import com.panelos.panelhome.domain.protocol.LoopMediaLimits
import com.panelos.panelhome.domain.protocol.LoopMediaRequestCommand
import com.panelos.panelhome.domain.protocol.LoopMediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelProtocol
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest
import kotlin.io.encoding.Base64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The tablet half of the loop media download, driven through the real
 * coordinator and its ordered command queue with a fake connection and a
 * temporary cache directory.
 */
class LoopMediaControllerTest {
    private val directory: File = Files.createTempDirectory("loop-media").toFile()
    private val connection = FakePanelConnection()
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Unconfined)
    private val coordinator = PanelSessionCoordinator(
        connection = connection,
        videoConnection = FakeVideoConnection(),
        h264Decoder = FakeH264DecoderController(),
        videoLogger = SilentVideoLogger,
        loopMediaDirectory = directory,
        communicationScope = scope,
    )

    private val gif = "GIF89a".toByteArray() + ByteArray(600_000) { (it % 251).toByte() }
    private val gifId = sha256(gif)
    private val loopMedia get() = coordinator.uiState.value.loopMedia
    private val requests get() = connection.sentCommands.filterIsInstance<LoopMediaRequestCommand>()

    @After
    fun tearDown() {
        coordinator.close()
        scope.cancel()
        directory.deleteRecursively()
    }

    @Test
    fun withoutAStateThereIsNoMediaPage() {
        connect()

        assertFalse(loopMedia.hasPage)
        assertEquals(LoopMediaPhase.HIDDEN, loopMedia.phase)
    }

    @Test
    fun aNewMediaIsPulledChunkByChunkVerifiedAndBecomesTheOnlyCachedFile() {
        File(directory, "${"b".repeat(64)}.mp4").writeBytes(byteArrayOf(1))
        File(directory, "stray.part").writeBytes(byteArrayOf(1))
        connect()

        publish(gifId, gif.size)
        assertEquals(LoopMediaPhase.DOWNLOADING, loopMedia.phase)
        assertTrue(loopMedia.hasPage)
        serve(gif)

        assertEquals(listOf(0, 262_144, 524_288), requests.map { it.offset })
        assertTrue(requests.all { it.mediaId == gifId })
        val target = File(directory, "$gifId.gif")
        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
        assertEquals(target, loopMedia.file)
        assertEquals(listOf(target.name), directory.list()!!.toList())
        assertTrue(gif.contentEquals(target.readBytes()))
    }

    @Test
    fun progressIsReportedWhileDownloading() {
        connect()
        publish(gifId, gif.size)

        answer(gif, offset = 0)

        assertEquals(262_144 * 100 / gif.size, loopMedia.progressPercent)
    }

    @Test
    fun aCachedMediaIsShownWithoutAnyRequest() {
        File(directory, "$gifId.gif").writeBytes(gif)
        connect()

        publish(gifId, gif.size)

        assertTrue(requests.isEmpty())
        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
    }

    @Test
    fun aHashMismatchDeletesThePartAndLeavesNoFinalFile() {
        connect()
        val tampered = gif.copyOf().also { it[100] = (it[100] + 1).toByte() }

        publish(gifId, gif.size)
        serve(tampered)

        assertFailedWithoutFiles()
    }

    @Test
    fun aFileWithoutTheExpectedMagicIsRejected() {
        connect()
        val notAGif = "PNG89a".toByteArray() + ByteArray(100)

        publish(sha256(notAGif), notAGif.size)
        serve(notAGif)

        assertFailedWithoutFiles()
    }

    @Test
    fun aVideoNeedsFtypAtBytes4To7() {
        connect()
        val mp4 = byteArrayOf(0, 0, 0, 0x18) + "ftypisom".toByteArray() + ByteArray(50)

        publish(sha256(mp4), mp4.size, LoopMediaKind.VIDEO)
        serve(mp4)

        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
        assertEquals("${sha256(mp4)}.mp4", loopMedia.file?.name)
    }

    @Test
    fun aStaleMediaErrorDeletesThePart() {
        connect()
        publish(gifId, gif.size)
        answer(gif, offset = 0)

        emitChunk(LoopMediaChunkMessage(gifId, 262_144, error = LoopMediaErrorCode.STALE_MEDIA))

        assertFailedWithoutFiles()
        assertEquals(2, requests.size)
    }

    @Test
    fun anUnexpectedOffsetIsDroppedAndFailsTheDownload() {
        connect()
        publish(gifId, gif.size)

        answer(gif, offset = 262_144)

        assertFailedWithoutFiles()
        assertEquals(1, requests.size)
    }

    @Test
    fun aChunkForAnotherMediaFailsTheDownload() {
        connect()
        publish(gifId, gif.size)

        answer(gif, offset = 0, mediaId = "c".repeat(64))

        assertFailedWithoutFiles()
    }

    @Test
    fun anErrorIsRetriedOnlyByTheNextState() {
        connect()
        publish(gifId, gif.size)
        answer(gif, offset = 262_144)
        assertEquals(1, requests.size)

        publish(gifId, gif.size)
        assertEquals(listOf(0, 0), requests.map { it.offset })
        serve(gif)

        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
    }

    @Test
    fun aRepeatedStateDoesNotRestartTheDownload() {
        connect()
        publish(gifId, gif.size)
        answer(gif, offset = 0)

        publish(gifId, gif.size)

        assertEquals(listOf(0, 262_144), requests.map { it.offset })
    }

    @Test
    fun aNewStateCancelsTheDownloadInProgress() {
        connect()
        publish(gifId, gif.size)
        val other = "GIF87a".toByteArray() + ByteArray(10)
        val otherId = sha256(other)

        publish(otherId, other.size)
        // The answer to the abandoned request still arrives and is ignored.
        answer(gif, offset = 0, mediaId = gifId)
        serve(other)

        assertFalse(File(directory, "$gifId.part").exists())
        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
        assertEquals(listOf("$otherId.gif"), directory.list()!!.toList())
    }

    @Test
    fun disconnectingMidDownloadDropsThePartAndReconnectStartsOver() {
        connect()
        publish(gifId, gif.size)
        answer(gif, offset = 0)
        assertTrue(File(directory, "$gifId.part").exists())

        connection.emit(PanelConnectionEvent.Disconnected)

        assertFalse(File(directory, "$gifId.part").exists())
        assertFalse(loopMedia.hasPage)
        assertEquals(2, requests.size)

        connect(connectionId = 2)
        publish(gifId, gif.size)
        assertEquals(0, requests.last().offset)
        serve(gif)
        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
    }

    @Test
    fun aFinishedMediaSurvivesADisconnect() {
        connect()
        publish(gifId, gif.size)
        serve(gif)

        connection.emit(PanelConnectionEvent.Disconnected)

        assertEquals(LoopMediaPhase.READY, loopMedia.phase)
        assertTrue(File(directory, "$gifId.gif").exists())
    }

    @Test
    fun unavailableClearsTheCacheAndRemovesThePage() {
        connect()
        publish(gifId, gif.size)
        serve(gif)

        connection.emit(
            PanelConnectionEvent.LoopMediaStateReceived(LoopMediaStateMessage(available = false)),
        )

        assertFalse(loopMedia.hasPage)
        assertNull(loopMedia.file)
        assertTrue(directory.list()!!.isEmpty())
    }

    @Test
    fun aHostWithoutTheFeatureNeverGetsARequest() {
        connect(features = setOf(PanelProtocol.FEATURE_STREAM_DECK))

        publish(gifId, gif.size)

        assertTrue(requests.isEmpty())
        assertFalse(loopMedia.hasPage)
    }

    private fun connect(
        connectionId: Long = 1,
        features: Set<String> = setOf(PanelProtocol.FEATURE_LOOP_MEDIA),
    ) = connection.emit(PanelConnectionEvent.Connected(connectionId, "PC", features))

    private fun publish(mediaId: String, size: Int, kind: LoopMediaKind = LoopMediaKind.GIF) =
        connection.emit(
            PanelConnectionEvent.LoopMediaStateReceived(
                LoopMediaStateMessage(true, mediaId, kind, size),
            ),
        )

    /** Answers every request the controller makes until it stops asking. */
    private fun serve(bytes: ByteArray) {
        while (true) {
            val before = requests.size
            val request = requests.last()
            answer(bytes, request.offset, request.mediaId)
            if (requests.size == before) return
        }
    }

    private fun answer(bytes: ByteArray, offset: Int, mediaId: String = requests.last().mediaId) {
        val end = minOf(bytes.size, offset + LoopMediaLimits.MAX_CHUNK_BYTES)
        emitChunk(
            LoopMediaChunkMessage(
                mediaId = mediaId,
                offset = offset,
                totalBytes = bytes.size,
                dataBase64 = Base64.Default.encode(bytes.copyOfRange(offset, end)),
                last = end == bytes.size,
            ),
        )
    }

    private fun emitChunk(chunk: LoopMediaChunkMessage) =
        connection.emit(PanelConnectionEvent.LoopMediaChunkReceived(chunk))

    private fun assertFailedWithoutFiles() {
        assertEquals(LoopMediaPhase.ERROR, loopMedia.phase)
        assertTrue(loopMedia.hasPage)
        assertNull(loopMedia.file)
        assertTrue(directory.list()!!.toList().toString(), directory.list()!!.isEmpty())
    }

    private fun sha256(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
}
