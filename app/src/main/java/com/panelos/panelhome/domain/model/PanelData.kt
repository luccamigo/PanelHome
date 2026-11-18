package com.panelos.panelhome.domain.model

data class CpuMetrics(
    val name: String? = null,
    val temperatureCelsius: Double? = null,
    val usagePercent: Double? = null,
    val frequencyGhz: Double? = null,
    val powerWatts: Double? = null,
)

data class GpuMetrics(
    val name: String? = null,
    val temperatureCelsius: Double? = null,
    val usagePercent: Double? = null,
    val frequencyGhz: Double? = null,
    val memoryUsagePercent: Double? = null,
    val memoryUsedGb: Double? = null,
    val memoryTotalGb: Double? = null,
    val powerWatts: Double? = null,
)

data class RamMetrics(
    val usedGb: Double? = null,
    val totalGb: Double? = null,
    val usagePercent: Double? = null,
)

data class VolumeState(
    val available: Boolean = false,
    val volumePercent: Int = 0,
    val isMuted: Boolean = false,
)

data class StorageMetrics(
    val name: String? = null,
    val temperatureCelsius: Double? = null,
    val usagePercent: Double? = null,
)

data class MediaTrack(
    val mediaId: String?,
    val sourceApp: String?,
    val title: String,
    val artist: String,
    val album: String?,
)

data class MediaProgress(
    val positionMs: Long,
    val durationMs: Long,
)

data class MediaCapabilities(
    val canPlayPause: Boolean,
    val canPrevious: Boolean,
    val canNext: Boolean,
)

class MediaArtwork(
    val mediaId: String,
    val mimeType: String,
    bytes: ByteArray,
) {
    val bytes: ByteArray = bytes.copyOf()

    override fun equals(other: Any?): Boolean =
        other is MediaArtwork &&
            mediaId == other.mediaId &&
            mimeType == other.mimeType &&
            bytes.contentEquals(other.bytes)

    override fun hashCode(): Int {
        var result = mediaId.hashCode()
        result = 31 * result + mimeType.hashCode()
        result = 31 * result + bytes.contentHashCode()
        return result
    }

    override fun toString(): String =
        "MediaArtwork(mediaId=$mediaId, mimeType=$mimeType, size=${bytes.size})"
}

data class MediaState(
    val available: Boolean,
    val track: MediaTrack?,
    val isPlaying: Boolean,
    val progress: MediaProgress,
    val capabilities: MediaCapabilities,
    val artwork: MediaArtwork?,
)

data class PanelData(
    val cpu: CpuMetrics,
    val gpu: GpuMetrics,
    val ram: RamMetrics,
    val storage: List<StorageMetrics> = emptyList(),
    val media: MediaState,
    val volume: VolumeState = VolumeState(),
)
