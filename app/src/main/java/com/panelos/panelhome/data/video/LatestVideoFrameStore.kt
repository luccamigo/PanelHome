package com.panelos.panelhome.data.video

import com.panelos.panelhome.domain.video.LatestVideoFrame
import com.panelos.panelhome.domain.video.LatestVideoFrameSource

/** Latest-frame-only store. Its backing pixel array is reused while dimensions remain unchanged. */
class LatestVideoFrameStore : LatestVideoFrameSource {
    private val lock = Any()
    private var sequence = 0L
    private var captureTimestampMicros = 0L
    private var width = 0
    private var height = 0
    private var pixels = ByteArray(0)

    fun publish(frame: DecodedVideoFrame): Boolean = synchronized(lock) {
        if (frame.sequence <= sequence) return@synchronized false
        val expectedBytes = VideoProtocol.checkedPixelBytes(frame.width, frame.height)
        if (frame.pixelCountBytes != expectedBytes || frame.pixels.size < expectedBytes) {
            throw VideoProtocolException("Decoded frame payload has an invalid size")
        }
        if (pixels.size != expectedBytes) pixels = ByteArray(expectedBytes)
        frame.pixels.copyInto(pixels, endIndex = expectedBytes)
        sequence = frame.sequence
        captureTimestampMicros = frame.captureTimestampMicros
        width = frame.width
        height = frame.height
        true
    }

    fun clear() = synchronized(lock) {
        sequence = 0L
        captureTimestampMicros = 0L
        width = 0
        height = 0
        pixels = ByteArray(0)
    }

    override fun <T> withLatest(
        expectedSequence: Long?,
        block: (LatestVideoFrame) -> T,
    ): T? =
        synchronized(lock) {
            if (sequence == 0L || (expectedSequence != null && sequence != expectedSequence)) {
                return@synchronized null
            }
            block(
                LatestVideoFrame(
                    sequence = sequence,
                    captureTimestampMicros = captureTimestampMicros,
                    width = width,
                    height = height,
                    pixels = pixels,
                ),
            )
        }
}
