package com.panelos.panelhome.data.streamdeck

import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import java.io.File
import kotlin.io.encoding.Base64
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class StreamDeckIconStoreTest {
    private lateinit var directory: File
    private lateinit var store: FileStreamDeckIconStore

    @Before
    fun createDirectory() {
        directory = File.createTempFile("panelos-icons", "").let { file ->
            file.delete()
            file.mkdirs()
            file
        }
        store = FileStreamDeckIconStore(directory)
    }

    @After
    fun removeDirectory() {
        directory.deleteRecursively()
    }

    @Test
    fun anIconIsDecodedOnceAndKeptAsAPngFile() {
        assertTrue(store.store(iconMessage(ICON_ID, PNG)))

        val file = store.iconFile(ICON_ID)
        assertNotNull(file)
        assertArrayEqualsBytes(PNG, file!!.readBytes())
        assertEquals(setOf(ICON_ID), store.cachedIconIds())
    }

    @Test
    fun onlyTheMissingIconsAreRequested() {
        store.store(iconMessage(ICON_ID, PNG))

        val missing = store.missingIconIds(setOf(ICON_ID, OTHER_ICON_ID))

        // A cache hit costs nothing on the wire; only the unknown id is asked for.
        assertEquals(listOf(OTHER_ICON_ID), missing)
    }

    @Test
    fun aLayoutWhoseIconsAreAllCachedRequestsNothing() {
        store.store(iconMessage(ICON_ID, PNG))

        assertTrue(store.missingIconIds(setOf(ICON_ID)).isEmpty())
    }

    @Test
    fun aCacheIndexSurvivesANewStoreOverTheSameDirectory() {
        store.store(iconMessage(ICON_ID, PNG))

        val reopened = FileStreamDeckIconStore(directory)

        assertEquals(setOf(ICON_ID), reopened.cachedIconIds())
        assertTrue(reopened.missingIconIds(setOf(ICON_ID)).isEmpty())
    }

    @Test
    fun aMalformedIconIsRejectedAndNothingIsWritten() {
        val cases = listOf(
            iconMessage(ICON_ID, byteArrayOf(1, 2, 3, 4, 5, 6, 7, 8, 9)),
            iconMessage(ICON_ID, PNG).copy(mime = "image/jpeg"),
            iconMessage(ICON_ID, PNG).copy(dataBase64 = "not base64 @@@"),
            iconMessage(ICON_ID, PNG).copy(available = false),
            iconMessage(ICON_ID, ByteArray(0)),
        )

        cases.forEach { message ->
            assertFalse("expected $message to be refused", store.store(message))
        }
        assertNull(store.iconFile(ICON_ID))
        assertTrue(store.cachedIconIds().isEmpty())
    }

    @Test
    fun anIconLargerThanTheLimitIsNotStored() {
        val bounded = FileStreamDeckIconStore(directory, maxIconBytes = 24)

        assertFalse(bounded.store(iconMessage(ICON_ID, PNG + ByteArray(64))))
        assertNull(bounded.iconFile(ICON_ID))
    }

    @Test
    fun oversizedDimensionsAreRejectedBeforeDecode() {
        assertFalse(store.store(iconMessage(ICON_ID, png(4096, 4096))))
        assertTrue(directory.listFiles().orEmpty().isEmpty())
    }

    @Test
    fun cacheBudgetsEvictTheLeastRecentlyUsedIcon() {
        val bounded = FileStreamDeckIconStore(
            directory = directory,
            maxCacheBytes = 48,
            maxIconCount = 2,
        )
        assertTrue(bounded.store(iconMessage(ICON_ID, png(32, 32))))
        Thread.sleep(2)
        assertTrue(bounded.store(iconMessage(OTHER_ICON_ID, png(64, 64))))
        Thread.sleep(2)
        assertTrue(bounded.store(iconMessage("icon-cccccccccc", png(96, 96))))

        assertEquals(2, bounded.cachedIconIds().size)
        assertFalse(ICON_ID in bounded.cachedIconIds())
    }

    @Test
    fun anIconIdCanNeverEscapeTheCacheDirectory() {
        // The id is combined into a filename, so its shape is the whole defence.
        val hostile = listOf(
            "..\\..\\windows\\system32",
            "../../etc/passwd",
            "c:\\windows\\win.ini",
            "icon-abc/../x",
            "icon abc",
            "ICON-ABC",
            "abc",
        )

        hostile.forEach { candidate ->
            assertFalse(candidate, StreamDeckIconIds.isValid(candidate))
            assertNull(candidate, store.iconFile(candidate))
            assertFalse(candidate, store.store(iconMessage(candidate, PNG)))
        }
        assertTrue(directory.listFiles().orEmpty().isEmpty())
    }

    private fun iconMessage(iconId: String, bytes: ByteArray) = StreamDeckIconMessage(
        iconId = iconId,
        available = true,
        mime = "image/png",
        dataBase64 = Base64.Default.encode(bytes),
    )

    private fun assertArrayEqualsBytes(expected: ByteArray, actual: ByteArray) {
        assertEquals(expected.toList(), actual.toList())
    }

    private companion object {
        const val ICON_ID = "icon-aaaaaaaaaa"
        const val OTHER_ICON_ID = "icon-bbbbbbbbbb"

        val PNG = png(128, 128)

        fun png(width: Int, height: Int): ByteArray = ByteArray(24).apply {
            byteArrayOf(
                0x89.toByte(), 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            ).copyInto(this)
            this[11] = 13
            "IHDR".toByteArray().copyInto(this, destinationOffset = 12)
            writeBigEndian(width, 16)
            writeBigEndian(height, 20)
        }

        fun ByteArray.writeBigEndian(value: Int, offset: Int) {
            this[offset] = (value ushr 24).toByte()
            this[offset + 1] = (value ushr 16).toByte()
            this[offset + 2] = (value ushr 8).toByte()
            this[offset + 3] = value.toByte()
        }
    }
}
