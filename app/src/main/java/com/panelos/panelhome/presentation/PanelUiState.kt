package com.panelos.panelhome.presentation

import com.panelos.panelhome.domain.model.ConnectionPhase
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.model.DesktopStatus
import com.panelos.panelhome.domain.model.DesktopVideoState
import com.panelos.panelhome.domain.model.DeviceProfile
import com.panelos.panelhome.domain.model.FpsState
import com.panelos.panelhome.domain.model.PanelData
import com.panelos.panelhome.domain.model.PanelMode
import com.panelos.panelhome.domain.model.StreamDeckLayout
import com.panelos.panelhome.domain.protocol.LoopMediaKind
import java.io.File

data class PanelUiState(
    val mode: PanelMode,
    val panelData: PanelData,
    val connectionStatus: ConnectionStatus = ConnectionStatus.DISCONNECTED,
    /**
     * How far the carrier itself has got. [connectionStatus] answers only
     * whether the application protocol is up, which cannot tell an unplugged
     * cable from a host that has not answered yet.
     */
    val connectionPhase: ConnectionPhase = ConnectionPhase.DISCONNECTED,
    val hostName: String? = null,
    val desktopStatus: DesktopStatus = DesktopStatus.STOPPED,
    val desktopErrorCode: String? = null,
    val desktopTouchAvailable: Boolean = false,
    val desktopTouchGeneration: Long? = null,
    val desktopVideo: DesktopVideoState = DesktopVideoState(),
    val deviceProfile: DeviceProfile? = null,
    val streamDeck: StreamDeckUiState = StreamDeckUiState(),
    val monitoring: MonitoringState = MonitoringState(),
    val integrations: LocalIntegrationsState = LocalIntegrationsState(),
    val loopMedia: LoopMediaUiState = LoopMediaUiState(),
    /** FPS of the app in front on the PC. Born [FpsState.Unavailable]: nothing produces it yet. */
    val fps: FpsState = FpsState.Unavailable,
)

enum class LoopMediaPhase {
    /** Nothing published: the carousel has no media page. */
    HIDDEN,
    DOWNLOADING,
    READY,
    ERROR,
}

/** The full-screen media page. [file] is set only when [phase] is READY. */
data class LoopMediaUiState(
    val phase: LoopMediaPhase = LoopMediaPhase.HIDDEN,
    val mediaId: String? = null,
    val kind: LoopMediaKind? = null,
    val file: File? = null,
    val progressPercent: Int = 0,
) {
    val hasPage: Boolean get() = phase != LoopMediaPhase.HIDDEN
}

/**
 * Stream Deck as the home carousel needs it: the layout to draw plus the
 * in-flight state of the button being pressed.
 */
data class StreamDeckUiState(
    /** Whether the host negotiated the feature for this connection. */
    val available: Boolean = false,
    val layout: StreamDeckLayout = StreamDeckLayout.EMPTY,

    /**
     * Button waiting for its `streamdeck.invoke_result`. The panel shows it as
     * pressed straight away, but never as done: only the PC knows that.
     */
    val pendingButtonId: String? = null,

    /** Last button whose invocation failed, shown briefly and discreetly. */
    val failedButtonId: String? = null,

    /**
     * Bumped whenever an icon lands in the cache. Icons live on disk, not in
     * this state, so this is what tells the grid to look again.
     */
    val iconGeneration: Int = 0,
) {
    val pages get() = layout.pages

    /** Pages the host published. */
    val pageCount: Int get() = layout.pages.size

    /**
     * Pages the home carousel adds after the dashboard.
     *
     * A host that negotiated the feature but has nothing configured still gets
     * one page - the one explaining where to configure shortcuts. A host that
     * never announced the feature adds none, so an older PanelHost leaves the
     * panel exactly as it was.
     */
    val carouselPageCount: Int
        get() = carouselPageCount(hasLoopMediaPage = false)

    /**
     * With a media page the carousel is never empty past the dashboard, so the
     * "configure shortcuts" placeholder is dropped: Painel -> Mídia.
     */
    fun carouselPageCount(hasLoopMediaPage: Boolean): Int =
        if (available && pageCount == 0 && !hasLoopMediaPage) 1 else pageCount

    /**
     * Reset for a fresh handshake. The layout is dropped rather than kept: the
     * host republishes it right away, and drawing a stale one would let a press
     * carry a revision the PC has long forgotten.
     */
    fun forNewConnection(available: Boolean): StreamDeckUiState = copy(
        available = available,
        layout = StreamDeckLayout.EMPTY,
        pendingButtonId = null,
        failedButtonId = null,
    )

    fun forDisconnectedHost(): StreamDeckUiState = forNewConnection(available = false)
}
