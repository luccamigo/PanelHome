package com.panelos.panelhome.data.mock

import com.panelos.panelhome.domain.model.CpuMetrics
import com.panelos.panelhome.domain.model.GpuMetrics
import com.panelos.panelhome.domain.model.MediaCapabilities
import com.panelos.panelhome.domain.model.MediaProgress
import com.panelos.panelhome.domain.model.MediaState
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.domain.model.RamMetrics
import com.panelos.panelhome.domain.model.VolumeState

object MockPanelData {
    val initial = PanelData(
        cpu = CpuMetrics(),
        gpu = GpuMetrics(),
        ram = RamMetrics(),
        storage = emptyList(),
        media = MediaState(
            available = false,
            track = null,
            isPlaying = false,
            progress = MediaProgress(positionMs = 0, durationMs = 0),
            capabilities = MediaCapabilities(
                canPlayPause = false,
                canPrevious = false,
                canNext = false,
            ),
            artwork = null,
        ),
        volume = VolumeState(),
    )
}
