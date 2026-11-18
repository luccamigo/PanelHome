package com.panelos.panelhome.data.video

import android.util.Log
import com.panelos.panelhome.domain.video.VideoLogger
import java.util.zip.CRC32

internal object AndroidVideoLogger : VideoLogger {
    override fun transition(message: String) {
        Log.i(TAG, "[Video] $message")
    }

    override fun debug(message: String) {
        Log.d(TAG, "[VideoDebug] $message")
    }

    override fun performance(message: String) {
        Log.i(TAG, "[VideoPerf] $message")
    }

    override fun error(stage: String, sequence: Long?, error: Throwable) {
        val details = buildString {
            append("[Video] Operation failed")
            append("\nstage=").append(stage)
            sequence?.let { append("\nsequence=").append(it) }
            append("\ntype=").append(error::class.java.simpleName)
            append("\nmessage=").append(error.message ?: "<no message>")
        }
        Log.e(TAG, details, error)
    }

    private const val TAG = "PanelOS"
}

/** Keeps host-side unit tests independent of the Android Log runtime stub. */
internal object SilentVideoLogger : VideoLogger {
    override fun transition(message: String) = Unit

    override fun debug(message: String) = Unit

    override fun error(stage: String, sequence: Long?, error: Throwable) = Unit
}

internal object VideoFrameDiagnostics {
    fun describeBgra(
        label: String,
        sequence: Long,
        width: Int,
        height: Int,
        pixels: ByteArray,
        byteCount: Int,
    ): String {
        val expectedBytes = VideoProtocol.checkedPixelBytes(width, height)
        require(byteCount == expectedBytes && pixels.size >= byteCount)

        val crc = CRC32().apply { update(pixels, 0, byteCount) }.value
        val samples = listOf(
            0 to 0,
            (width / 2) to (height / 2),
            (width - 1) to (height - 1),
        )
        val luminance = sampleLuminance(width, height, pixels)
        return buildString {
            append(label).append(':')
            append("\nsequence=").append(sequence)
            append("\nsize=").append(width).append('x').append(height)
            append("\nstride=").append(width * 4)
            append("\nformat=BGRA8888")
            append("\nbytes=").append(byteCount)
            append("\ncrc32=").append(crc.toString(16).padStart(8, '0'))
            append("\npixels:")
            for ((x, y) in samples) {
                append("\n(").append(x).append(',').append(y).append(")=")
                    .append(pixelBgra(pixels, width, x, y))
            }
            append("\nluminance: min=").append(luminance.minimum)
                .append(" max=").append(luminance.maximum)
                .append(" avg=").append(luminance.average)
                .append(" nonBlack=").append(luminance.nonBlackSamples)
                .append('/').append(luminance.sampleCount)
        }
    }

    private fun pixelBgra(pixels: ByteArray, width: Int, x: Int, y: Int): String {
        val offset = ((y * width) + x) * 4
        return buildString {
            append(pixels[offset].toInt() and 0xff).append(',')
            append(pixels[offset + 1].toInt() and 0xff).append(',')
            append(pixels[offset + 2].toInt() and 0xff).append(',')
            append(pixels[offset + 3].toInt() and 0xff)
        }
    }

    private fun sampleLuminance(
        width: Int,
        height: Int,
        pixels: ByteArray,
    ): LuminanceSummary {
        var minimum = 255
        var maximum = 0
        var total = 0L
        var nonBlack = 0
        var count = 0
        for (gridY in 0 until SAMPLE_GRID_SIZE) {
            val y = gridCoordinate(gridY, height)
            for (gridX in 0 until SAMPLE_GRID_SIZE) {
                val x = gridCoordinate(gridX, width)
                val offset = ((y * width) + x) * 4
                val blue = pixels[offset].toInt() and 0xff
                val green = pixels[offset + 1].toInt() and 0xff
                val red = pixels[offset + 2].toInt() and 0xff
                val value = (red + green + blue) / 3
                minimum = minOf(minimum, value)
                maximum = maxOf(maximum, value)
                total += value
                if (red != 0 || green != 0 || blue != 0) nonBlack++
                count++
            }
        }
        return LuminanceSummary(
            minimum = minimum,
            maximum = maximum,
            average = (total / count).toInt(),
            nonBlackSamples = nonBlack,
            sampleCount = count,
        )
    }

    private fun gridCoordinate(index: Int, dimension: Int): Int =
        if (dimension == 1) 0 else (index * (dimension - 1)) / (SAMPLE_GRID_SIZE - 1)

    private data class LuminanceSummary(
        val minimum: Int,
        val maximum: Int,
        val average: Int,
        val nonBlackSamples: Int,
        val sampleCount: Int,
    )

    private const val SAMPLE_GRID_SIZE = 16
}
