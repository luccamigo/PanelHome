package com.panelos.panelhome.ui.desktop

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertSame
import org.junit.Test

class DesktopVideoRendererTest {
    @Test
    fun bgraRedAndBlueAreConvertedToArgbWithoutSwappingColors() {
        val converter = BgraToArgbConverter()
        val result = converter.convert(
            byteArrayOf(
                0x00, 0x00, 0xff.toByte(), 0xff.toByte(), // opaque red in BGRA
                0xff.toByte(), 0x00, 0x00, 0xff.toByte(), // opaque blue in BGRA
            ),
            width = 2,
            height = 1,
        )

        assertEquals(0xffff0000L, result[0].toLong() and 0xffff_ffffL)
        assertEquals(0xff0000ffL, result[1].toLong() and 0xffff_ffffL)
        assertSame(result, converter.convert(ByteArray(8), width = 2, height = 1))
    }

    @Test
    fun bgraConversionForcesOpaqueAlphaAndUsesExplicitArgbChannelOrder() {
        val result = BgraToArgbConverter().convert(
            byteArrayOf(0x33, 0x22, 0x11, 0x00),
            width = 1,
            height = 1,
        )

        assertEquals(0xff112233L, result.single().toLong() and 0xffff_ffffL)
    }

    @Test
    fun rgbwPatternRetainsAllFourCanonicalColors() {
        val result = BgraToArgbConverter().convert(
            byteArrayOf(
                0x00, 0x00, 0xff.toByte(), 0x00, // red, deliberately transparent source
                0x00, 0xff.toByte(), 0x00, 0x00, // green
                0xff.toByte(), 0x00, 0x00, 0x00, // blue
                0xff.toByte(), 0xff.toByte(), 0xff.toByte(), 0x00, // white
            ),
            width = 2,
            height = 2,
        )

        assertEquals(0xffff0000L, result[0].toLong() and 0xffff_ffffL)
        assertEquals(0xff00ff00L, result[1].toLong() and 0xffff_ffffL)
        assertEquals(0xff0000ffL, result[2].toLong() and 0xffff_ffffL)
        assertEquals(0xffffffffL, result[3].toLong() and 0xffff_ffffL)
    }

    @Test
    fun framePublishedBeforeSurfaceIsRenderedWhenSurfaceAppears() {
        val lifecycle = DesktopFramePresentationLifecycle()

        assertNull(lifecycle.onFrameAvailable(10))
        assertEquals(10L, lifecycle.onSurfaceAvailable())
    }

    @Test
    fun framePublishedAfterSurfaceIsRenderedImmediately() {
        val lifecycle = DesktopFramePresentationLifecycle()

        assertNull(lifecycle.onSurfaceAvailable())
        assertEquals(11L, lifecycle.onFrameAvailable(11))
    }

    @Test
    fun surfaceRecreationAndDrawFailureKeepTheLatestFrame() {
        val lifecycle = DesktopFramePresentationLifecycle()
        lifecycle.onSurfaceAvailable()
        assertEquals(12L, lifecycle.onFrameAvailable(12))
        lifecycle.onDrawSucceeded(12)

        // No new frame is needed to preserve the last successfully presented pixels.
        assertNull(lifecycle.onFrameAvailable(12))

        lifecycle.onSurfaceDestroyed()
        assertEquals(12L, lifecycle.onSurfaceAvailable())
        lifecycle.onDrawFailed(12)
        assertEquals(12L, lifecycle.onFrameAvailable(12))
        lifecycle.onDrawSucceeded(12)
        assertNull(lifecycle.onFrameAvailable(12))
    }

    @Test
    fun aspectFitPreservesRatioAndCentersLetterbox() {
        val horizontal = aspectFitRect(640, 400, 1_280, 800)
        assertEquals(0f, horizontal.left, 0f)
        assertEquals(0f, horizontal.top, 0f)
        assertEquals(1_280f, horizontal.right, 0f)
        assertEquals(800f, horizontal.bottom, 0f)

        val letterboxed = aspectFitRect(640, 400, 1_000, 1_000)
        assertEquals(0f, letterboxed.left, 0.01f)
        assertEquals(187.5f, letterboxed.top, 0.01f)
        assertEquals(1_000f, letterboxed.right, 0.01f)
        assertEquals(812.5f, letterboxed.bottom, 0.01f)
    }
}
