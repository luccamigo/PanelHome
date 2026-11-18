package com.panelos.panelhome.data.streamdeck

import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import java.io.File
import java.io.IOException
import kotlin.io.encoding.Base64

/**
 * Shape of an icon id, checked on both sides of the cable.
 *
 * An id is a cache key and never a path. Validating it here means the panel can
 * combine it into a filename without a traversal ever being possible - `..\..`,
 * `/`, `\` and an absolute path all fail the character test.
 */
internal object StreamDeckIconIds {
    private const val MIN_LENGTH = 5
    private const val MAX_LENGTH = 64

    fun isValid(iconId: String?): Boolean {
        if (iconId == null || iconId.length !in MIN_LENGTH..MAX_LENGTH) return false
        return iconId.all { character ->
            character in 'a'..'z' || character in '0'..'9' || character == '-'
        }
    }
}

/** What the panel keeps of the PC's button images. */
interface StreamDeckIconStore {
    /** Ids of the icons already stored, so only the missing ones are asked for. */
    fun cachedIconIds(): Set<String>

    /**
     * Which of [iconIds] are not cached yet. The result is what goes on the
     * wire, so a layout whose icons are all known costs nothing.
     */
    fun missingIconIds(iconIds: Set<String>): List<String> {
        val cached = cachedIconIds()
        return iconIds.filter { iconId ->
            StreamDeckIconIds.isValid(iconId) && iconId !in cached
        }
    }

    /**
     * Decodes and stores a received icon. Returns false when the message is not
     * a usable PNG, in which case nothing is written.
     */
    fun store(message: StreamDeckIconMessage): Boolean

    /** Local file holding the icon, or null when it is not cached. */
    fun iconFile(iconId: String?): File?
}

/**
 * Stores icons as PNG files in the app's private storage.
 *
 * Base64 is a transport encoding and is not kept: the bytes are decoded once,
 * checked for a PNG signature and written to disk, so what stays on the device
 * is an ordinary image the UI can decode directly.
 */
class FileStreamDeckIconStore(
    private val directory: File,
    private val maxIconBytes: Int = MAX_ICON_BYTES,
    private val maxIconDimension: Int = MAX_ICON_DIMENSION,
    private val maxCacheBytes: Long = MAX_CACHE_BYTES,
    private val maxIconCount: Int = MAX_ICON_COUNT,
) : StreamDeckIconStore {
    private val lock = Any()
    private val cached = mutableSetOf<String>()
    private var indexed = false

    override fun cachedIconIds(): Set<String> = synchronized(lock) {
        ensureIndexed()
        cached.toSet()
    }

    override fun store(message: StreamDeckIconMessage): Boolean {
        if (!message.available) return false
        if (!StreamDeckIconIds.isValid(message.iconId)) return false
        if (message.mime?.lowercase() != PNG_MIME) return false

        val encoded = message.dataBase64
            ?.takeIf { it.isNotBlank() && it.length <= maxBase64Characters }
            ?: return false
        val bytes = runCatching { Base64.Default.decode(encoded) }.getOrNull()
            ?.takeIf { it.isNotEmpty() && it.size <= maxIconBytes }
            ?: return false
        if (bytes.size > maxCacheBytes) return false
        if (!hasPngSignature(bytes)) return false
        val dimensions = pngDimensions(bytes) ?: return false
        if (dimensions.first !in 1..maxIconDimension ||
            dimensions.second !in 1..maxIconDimension
        ) {
            return false
        }

        return synchronized(lock) {
            ensureIndexed()
            trimToFit(message.iconId, bytes.size.toLong())
            writeAtomically(message.iconId, bytes).also { written ->
                if (written) cached += message.iconId
            }
        }
    }

    override fun iconFile(iconId: String?): File? {
        if (!StreamDeckIconIds.isValid(iconId)) return null
        val file = File(directory, "$iconId$FILE_EXTENSION")
        return file.takeIf(File::isFile)?.also { it.setLastModified(System.currentTimeMillis()) }
    }

    private val maxBase64Characters: Int
        get() = ((maxIconBytes + 2) / 3) * 4

    private fun ensureIndexed() {
        if (indexed) return
        indexed = true
        val files = directory.listFiles() ?: return
        files.forEach { file ->
            val iconId = file.name.removeSuffix(FILE_EXTENSION)
            if (file.isFile && file.length() in 1..maxIconBytes.toLong() &&
                file.name.endsWith(FILE_EXTENSION) &&
                StreamDeckIconIds.isValid(iconId) &&
                readPngDimensions(file)?.let { (width, height) ->
                    width in 1..maxIconDimension && height in 1..maxIconDimension
                } == true
            ) {
                cached += iconId
            } else if (file.isFile && file.name.endsWith(FILE_EXTENSION)) {
                file.delete()
            }
        }
        trimToFit(incomingId = null, incomingBytes = 0)
    }

    private fun trimToFit(incomingId: String?, incomingBytes: Long) {
        fun cachedFiles() = cached.map { File(directory, "$it$FILE_EXTENSION") }
        var files = cachedFiles()
        var bytes = files.sumOf(File::length) - files
            .firstOrNull { it.nameWithoutExtension == incomingId }
            ?.length().orZero() + incomingBytes
        var count = cached.size + if (incomingId != null && incomingId !in cached) 1 else 0
        for (file in files.sortedBy(File::lastModified)) {
            if (bytes <= maxCacheBytes && count <= maxIconCount) break
            if (file.nameWithoutExtension == incomingId) continue
            val length = file.length()
            if (file.delete()) {
                cached.remove(file.nameWithoutExtension)
                bytes -= length
                count--
            }
        }
    }

    private fun writeAtomically(iconId: String, bytes: ByteArray): Boolean = try {
        directory.mkdirs()
        val target = File(directory, "$iconId$FILE_EXTENSION")
        val temporary = File(directory, "$iconId$FILE_EXTENSION.tmp")
        temporary.writeBytes(bytes)

        // Replace rather than truncate-and-write: a half-written icon would be
        // decoded as a broken image on the next launch.
        if (target.exists()) target.delete()
        temporary.renameTo(target) || run {
            temporary.delete()
            false
        }
    } catch (_: IOException) {
        false
    } catch (_: SecurityException) {
        false
    }

    private companion object {
        const val FILE_EXTENSION = ".png"
        const val PNG_MIME = "image/png"
        const val MAX_ICON_BYTES = 512 * 1024
        const val MAX_ICON_DIMENSION = 512
        const val MAX_ICON_COUNT = 256
        const val MAX_CACHE_BYTES = 16L * 1024 * 1024

        val PNG_SIGNATURE = byteArrayOf(
            0x89.toByte(), 0x50, 0x4E, 0x47,
            0x0D, 0x0A, 0x1A, 0x0A,
        )

        fun hasPngSignature(bytes: ByteArray): Boolean =
            bytes.size >= PNG_SIGNATURE.size &&
                PNG_SIGNATURE.indices.all { index -> bytes[index] == PNG_SIGNATURE[index] }

        fun pngDimensions(bytes: ByteArray): Pair<Int, Int>? {
            if (bytes.size < 24 || !hasPngSignature(bytes) ||
                bytes[12] != 'I'.code.toByte() || bytes[13] != 'H'.code.toByte() ||
                bytes[14] != 'D'.code.toByte() || bytes[15] != 'R'.code.toByte()
            ) {
                return null
            }
            val width = readBigEndianInt(bytes, 16)
            val height = readBigEndianInt(bytes, 20)
            return width to height
        }

        fun readPngDimensions(file: File): Pair<Int, Int>? = runCatching {
            file.inputStream().use { input ->
                val header = ByteArray(24)
                var offset = 0
                while (offset < header.size) {
                    val read = input.read(header, offset, header.size - offset)
                    if (read < 0) return@use null
                    offset += read
                }
                pngDimensions(header)
            }
        }.getOrNull()

        fun readBigEndianInt(bytes: ByteArray, offset: Int): Int =
            ((bytes[offset].toInt() and 0xff) shl 24) or
                ((bytes[offset + 1].toInt() and 0xff) shl 16) or
                ((bytes[offset + 2].toInt() and 0xff) shl 8) or
                (bytes[offset + 3].toInt() and 0xff)

        fun Long?.orZero(): Long = this ?: 0L
    }
}

/** Used when no storage is available, for example in previews. */
object NoStreamDeckIconStore : StreamDeckIconStore {
    override fun cachedIconIds(): Set<String> = emptySet()

    override fun store(message: StreamDeckIconMessage): Boolean = false

    override fun iconFile(iconId: String?): File? = null
}
