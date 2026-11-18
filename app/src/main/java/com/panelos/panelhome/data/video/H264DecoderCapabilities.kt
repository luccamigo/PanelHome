package com.panelos.panelhome.data.video

import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.os.Build

data class H264DecoderCapability(
    val codecName: String,
    val isHardwareAccelerated: Boolean,
    val supportsLowLatency: Boolean,
    val isSoftwareOnly: Boolean = !isHardwareAccelerated,
    val isVendor: Boolean = false,
    val maxSupportedInstances: Int? = null,
    val clientMaxFps: Int = VideoClientCapabilities.preferredSessionFps,
)

fun interface H264DecoderCapabilityProvider {
    fun findDecoder(): H264DecoderCapability?
}

object AndroidH264DecoderCapabilityProvider : H264DecoderCapabilityProvider {
    override fun findDecoder(): H264DecoderCapability? {
        val candidates = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
            .asSequence()
            .filterNot(MediaCodecInfo::isEncoder)
            .filter { info ->
                info.supportedTypes.any { it.equals(MIME_TYPE, ignoreCase = true) }
            }
            .mapNotNull { info ->
                val codecCapabilities = runCatching {
                    info.getCapabilitiesForType(MIME_TYPE)
                }.getOrNull()
                val clientMaxFps = codecCapabilities
                    ?.videoCapabilities
                    ?.let(::maximumSupportedDesktopFps)
                    ?: return@mapNotNull null
                val hardware = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    info.isHardwareAccelerated
                } else {
                    !isKnownSoftwareCodec(info.name)
                }
                val lowLatency = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                    runCatching {
                        codecCapabilities.isFeatureSupported(
                            MediaCodecInfo.CodecCapabilities.FEATURE_LowLatency,
                        ) == true
                    }.getOrDefault(false)
                } else {
                    false
                }
                H264DecoderCapability(
                    codecName = info.name,
                    isHardwareAccelerated = hardware,
                    supportsLowLatency = lowLatency,
                    isSoftwareOnly = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                        info.isSoftwareOnly
                    } else {
                        isKnownSoftwareCodec(info.name)
                    },
                    isVendor = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q && info.isVendor,
                    maxSupportedInstances = codecCapabilities.maxSupportedInstances,
                    clientMaxFps = clientMaxFps,
                )
            }
            .sortedByDescending(H264DecoderCapability::isHardwareAccelerated)
            .toList()
        return candidates.firstOrNull()
    }

    private fun isKnownSoftwareCodec(name: String): Boolean {
        val normalized = name.lowercase()
        return normalized.startsWith("omx.google.") ||
            normalized.startsWith("c2.android.") ||
            normalized.contains("software")
    }

    private fun maximumSupportedDesktopFps(
        capabilities: MediaCodecInfo.VideoCapabilities,
    ): Int? = VideoClientCapabilities.desktopFpsCandidates.firstOrNull { fps ->
        runCatching {
            capabilities.areSizeAndRateSupported(
                VideoClientCapabilities.H264.maxWidth,
                VideoClientCapabilities.H264.maxHeight,
                fps.toDouble(),
            )
        }.getOrDefault(false)
    }

    const val MIME_TYPE = "video/avc"
}
