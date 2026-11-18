package com.panelos.panelhome.ui.wifi

import org.junit.Assert.assertTrue
import org.junit.Test

class QrScannerResolutionTest {
    @Test
    fun `analysis asks for at least 1280x720 instead of the 640x480 default`() {
        assertTrue(QR_ANALYSIS_MIN_WIDTH >= 1280)
        assertTrue(QR_ANALYSIS_MIN_HEIGHT >= 720)
    }
}