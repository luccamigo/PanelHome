package com.panelos.panelhome.ui.wifi

import android.os.SystemClock
import android.util.Log
import android.util.Size
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.core.resolutionselector.ResolutionSelector
import androidx.camera.core.resolutionselector.ResolutionStrategy
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.panelos.panelhome.data.wifi.WifiQrFrameDecoder
import java.util.concurrent.Executors

/**
 * Live camera preview that reports the QR codes it can read. The same code is not reported again for a couple of seconds, so a
 * poster on the wall gives a notice and the camera keeps looking for the real one. The camera is bound to this screen's lifecycle and
 * released with it; frames are decoded in memory and dropped, nothing is saved, and the decoded text goes straight to
 * the pairing controller (it carries a one-use secret and is never logged).
 */
@Composable
internal fun QrScannerPreview(onQr: (ByteArray) -> Unit, onCameraUnavailable: () -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val executor = remember { Executors.newSingleThreadExecutor() }
    val currentOnQr by rememberUpdatedState(onQr)
    val analyzer = remember { QrAnalyzer { bytes -> currentOnQr(bytes) } }
    var providerHolder by remember { mutableStateOf<ProcessCameraProvider?>(null) }
    DisposableEffect(Unit) {
        onDispose {
            providerHolder?.unbindAll()
            executor.shutdown()
        }
    }
    AndroidView(
        modifier = modifier,
        factory = { ctx -> PreviewView(ctx).apply { implementationMode = PreviewView.ImplementationMode.COMPATIBLE; scaleType = PreviewView.ScaleType.FIT_CENTER } },
        update = { previewView ->
            val future = ProcessCameraProvider.getInstance(context)
            future.addListener({
                try {
                    val provider = future.get()
                    providerHolder = provider
                    val selector = when {
                        provider.hasCamera(CameraSelector.DEFAULT_BACK_CAMERA) -> CameraSelector.DEFAULT_BACK_CAMERA
                        provider.hasCamera(CameraSelector.DEFAULT_FRONT_CAMERA) -> CameraSelector.DEFAULT_FRONT_CAMERA
                        else -> { onCameraUnavailable(); return@addListener }
                    }
                    val preview = Preview.Builder().build().also { it.surfaceProvider = previewView.surfaceProvider }
                    val analysis = ImageAnalysis.Builder()
                        .setResolutionSelector(analysisResolutionSelector())
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .setOutputImageFormat(ImageAnalysis.OUTPUT_IMAGE_FORMAT_YUV_420_888)
                        .build().also { it.setAnalyzer(executor, analyzer) }
                    provider.unbindAll()
                    provider.bindToLifecycle(lifecycleOwner, selector, preview, analysis)
                    // What the device really delivers, for checking on the tablet; sizes only, never frame content.
                    Log.i(LOG_TAG, "QR analysis resolution=" + (analysis.resolutionInfo?.resolution?.let { "${it.width}x${it.height}" } ?: "unknown") +
                        " requested>=" + QR_ANALYSIS_MIN_WIDTH + "x" + QR_ANALYSIS_MIN_HEIGHT)
                } catch (_: Exception) {
                    onCameraUnavailable()
                }
            }, ContextCompat.getMainExecutor(context))
        },
    )
}

/** The default analysis size is 640x480, too coarse for a 100+ module QR read from a monitor: ask for 1280x720 or the closest larger one. */
internal const val QR_ANALYSIS_MIN_WIDTH = 1280
internal const val QR_ANALYSIS_MIN_HEIGHT = 720
private const val LOG_TAG = "PanelWifiQr"

private fun analysisResolutionSelector(): ResolutionSelector = ResolutionSelector.Builder()
    .setResolutionStrategy(
        ResolutionStrategy(Size(QR_ANALYSIS_MIN_WIDTH, QR_ANALYSIS_MIN_HEIGHT), ResolutionStrategy.FALLBACK_RULE_CLOSEST_HIGHER_THEN_LOWER),
    ).build()

/** Luminance plane in, QR bytes out, at most about eight frames a second; the first hit wins. */
private class QrAnalyzer(private val onQr: (ByteArray) -> Unit) : ImageAnalysis.Analyzer {
    private var last = 0L
    private var lastHash = 0
    private var lastDeliveredAt = 0L

    override fun analyze(image: ImageProxy) {
        try {
            val now = SystemClock.elapsedRealtime()
            if (now - last < MIN_INTERVAL_MILLIS) return
            last = now
            val plane = image.planes[0]
            val buffer = plane.buffer
            val width = image.width; val height = image.height; val stride = plane.rowStride
            val luma = ByteArray(stride * (height - 1) + width)
            buffer.rewind()
            buffer.get(luma, 0, minOf(luma.size, buffer.remaining()))
            WifiQrFrameDecoder.decode(luma, width, height, stride)?.let { bytes ->
                val hash = bytes.contentHashCode()
                if (hash != lastHash || now - lastDeliveredAt >= REPEAT_MILLIS) {
                    lastHash = hash; lastDeliveredAt = now
                    onQr(bytes)
                }
            }
        } catch (_: Exception) {
            // A bad frame is skipped; the next one is tried.
        } finally {
            image.close()
        }
    }

    private companion object { const val MIN_INTERVAL_MILLIS = 120L; const val REPEAT_MILLIS = 3_000L }
}