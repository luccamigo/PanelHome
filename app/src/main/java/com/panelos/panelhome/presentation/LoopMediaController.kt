package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.protocol.LoopMediaChunkMessage
import com.panelos.panelhome.domain.protocol.LoopMediaKind
import com.panelos.panelhome.domain.protocol.LoopMediaLimits
import com.panelos.panelhome.domain.protocol.LoopMediaRequestCommand
import com.panelos.panelhome.domain.protocol.LoopMediaStateMessage
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.video.VideoLogger
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.security.MessageDigest
import kotlin.io.encoding.Base64
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Owns the full-screen loop media: the published state, the pull download into
 * a one-item cache and the verification of what arrived.
 *
 * shortcut: chunk writes and hashing run on the caller's thread like the icon
 * store does (256 KiB per step); move to Dispatchers.IO if it shows up in jank.
 */
internal class LoopMediaController(
    private val directory: File?,
    private val logger: VideoLogger,
    private val enqueueCommand: (PanelCommand) -> Boolean,
    private val onStateChanged: (LoopMediaUiState) -> Unit = {},
) {
    private val _state = MutableStateFlow(LoopMediaUiState())
    val state: StateFlow<LoopMediaUiState> = _state.asStateFlow()
    private var negotiated = false
    private var download: Download? = null

    /**
     * Requests of a cancelled download whose answer may still be on the wire.
     * Their chunk is dropped silently instead of failing the download that
     * replaced it.
     */
    private val abandonedRequests = mutableSetOf<Pair<String, Int>>()

    fun onConnected(features: Set<String>) {
        negotiated = directory != null && PanelProtocol.FEATURE_LOOP_MEDIA in features
    }

    /** A finished media keeps playing locally; anything partial is dropped. */
    fun onDisconnected() {
        negotiated = false
        abandonedRequests.clear()
        download?.let { abort(it) }
        if (_state.value.phase != LoopMediaPhase.READY) update(LoopMediaUiState())
    }

    fun applyState(message: LoopMediaStateMessage) {
        val directory = directory ?: return
        if (!negotiated) return
        if (!message.available) {
            cancelDownload()
            directory.listFiles()?.forEach(File::delete)
            update(LoopMediaUiState())
            logger.debug("[LoopMedia] Unavailable; cache cleared")
            return
        }
        val mediaId = message.mediaId?.takeIf(LoopMediaLimits::isValidMediaId) ?: return
        val kind = message.kind ?: return
        val sizeBytes = message.sizeBytes ?: return
        val current = _state.value
        if (current.mediaId == mediaId && current.phase in KEPT_PHASES) return

        cancelDownload()
        val target = File(directory, "$mediaId.${kind.extension}")
        if (target.isFile && target.length() == sizeBytes.toLong()) {
            deleteAllBut(directory, target)
            update(LoopMediaUiState(LoopMediaPhase.READY, mediaId, kind, target))
            logger.debug("[LoopMedia] Cached media=$mediaId")
            return
        }

        val part = File(directory, "$mediaId$PART_EXTENSION")
        try {
            directory.mkdirs()
            part.delete()
            part.createNewFile()
        } catch (_: IOException) {
            fail(mediaId, kind, "cannot create ${part.name}")
            return
        }
        val started = Download(mediaId, kind, sizeBytes, part, target)
        download = started
        update(LoopMediaUiState(LoopMediaPhase.DOWNLOADING, mediaId, kind))
        logger.debug("[LoopMedia] Download media=$mediaId size=$sizeBytes")
        request(started)
    }

    fun applyChunk(chunk: LoopMediaChunkMessage) {
        if (abandonedRequests.remove(chunk.mediaId to chunk.offset)) return
        val active = download ?: return
        val reason = when {
            chunk.mediaId != active.mediaId -> "mediaId ${chunk.mediaId}"
            chunk.error != null -> "error ${chunk.error}"
            chunk.offset != active.offset -> "offset ${chunk.offset} expected ${active.offset}"
            chunk.totalBytes != active.sizeBytes -> "totalBytes ${chunk.totalBytes}"
            else -> null
        }
        if (reason != null) return failDownload(active, reason)

        val bytes = runCatching { Base64.Default.decode(chunk.dataBase64.orEmpty()) }.getOrNull()
        if (bytes == null || bytes.isEmpty() || active.offset + bytes.size > active.sizeBytes) {
            return failDownload(active, "invalid data")
        }
        try {
            FileOutputStream(active.part, true).use { it.write(bytes) }
        } catch (_: IOException) {
            return failDownload(active, "write failed")
        }
        active.digest.update(bytes)
        active.offset += bytes.size

        if (!chunk.last) {
            update(_state.value.copy(progressPercent = active.progressPercent()))
            request(active)
            return
        }
        val verification = when {
            active.offset != active.sizeBytes -> "size ${active.offset} != ${active.sizeBytes}"
            active.digest.digest().toHex() != active.mediaId -> "sha-256 mismatch"
            !hasMagic(active.part, active.kind) -> "not a ${active.kind.wireValue}"
            else -> null
        }
        if (verification != null) return failDownload(active, verification)
        active.target.delete()
        if (!active.part.renameTo(active.target)) return failDownload(active, "rename failed")
        download = null
        directory?.let { deleteAllBut(it, active.target) }
        update(LoopMediaUiState(LoopMediaPhase.READY, active.mediaId, active.kind, active.target))
        logger.debug("[LoopMedia] Ready media=${active.mediaId}")
    }

    private fun request(active: Download) {
        if (!enqueueCommand(LoopMediaRequestCommand(active.mediaId, active.offset))) {
            failDownload(active, "request not queued")
        }
    }

    private fun cancelDownload() {
        val active = download ?: return
        abandonedRequests += active.mediaId to active.offset
        abort(active)
    }

    private fun abort(active: Download) {
        active.part.delete()
        download = null
    }

    private fun failDownload(active: Download, reason: String) {
        abort(active)
        fail(active.mediaId, active.kind, reason)
    }

    // No retry loop: the next loop_media.state or a reconnect starts over.
    private fun fail(mediaId: String, kind: LoopMediaKind, reason: String) {
        update(LoopMediaUiState(LoopMediaPhase.ERROR, mediaId, kind))
        logger.debug("[LoopMedia] Failed media=$mediaId reason=$reason")
    }

    private fun update(value: LoopMediaUiState) {
        _state.value = value
        onStateChanged(value)
    }

    private class Download(
        val mediaId: String,
        val kind: LoopMediaKind,
        val sizeBytes: Int,
        val part: File,
        val target: File,
    ) {
        var offset = 0
        val digest: MessageDigest = MessageDigest.getInstance("SHA-256")

        fun progressPercent(): Int = (offset.toLong() * 100 / sizeBytes).toInt()
    }

    private companion object {
        const val PART_EXTENSION = ".part"
        val KEPT_PHASES = setOf(LoopMediaPhase.DOWNLOADING, LoopMediaPhase.READY)

        fun deleteAllBut(directory: File, keep: File) {
            directory.listFiles()?.forEach { if (it != keep) it.delete() }
        }

        fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }

        fun hasMagic(file: File, kind: LoopMediaKind): Boolean {
            val header = ByteArray(8)
            val read = runCatching { file.inputStream().use { it.read(header) } }.getOrDefault(-1)
            if (read < header.size) return false
            val text = String(header, Charsets.ISO_8859_1)
            return when (kind) {
                LoopMediaKind.GIF -> text.startsWith("GIF87a") || text.startsWith("GIF89a")
                LoopMediaKind.VIDEO -> text.substring(4, 8) == "ftyp"
            }
        }
    }
}
