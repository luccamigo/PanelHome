package com.panelos.panelhome.presentation

import com.panelos.panelhome.data.streamdeck.StreamDeckIconStore
import com.panelos.panelhome.data.streamdeck.StreamDeckLayoutMapper
import com.panelos.panelhome.domain.protocol.PanelCommand
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.domain.protocol.StreamDeckIconMessage
import com.panelos.panelhome.domain.protocol.StreamDeckIconsRequestCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeCommand
import com.panelos.panelhome.domain.protocol.StreamDeckInvokeResultMessage
import com.panelos.panelhome.domain.protocol.StreamDeckLayoutMessage
import com.panelos.panelhome.domain.video.VideoLogger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Owns Stream Deck layouts, revisions, icon requests, and invoke correlation. */
internal class StreamDeckController(
    private val scope: CoroutineScope,
    private val icons: StreamDeckIconStore,
    private val logger: VideoLogger,
    private val enqueueCommand: (PanelCommand) -> Boolean,
    private val onStateChanged: (StreamDeckUiState) -> Unit = {},
) {
    private val _state = MutableStateFlow(StreamDeckUiState())
    val state: StateFlow<StreamDeckUiState> = _state.asStateFlow()
    private var connected = false

    fun onConnected(features: Set<String>) {
        connected = true
        update(
            _state.value.forNewConnection(
                available = PanelProtocol.FEATURE_STREAM_DECK in features,
            ),
        )
    }

    fun onDisconnected() {
        connected = false
        update(_state.value.forDisconnectedHost())
    }

    fun applyLayout(message: StreamDeckLayoutMessage) {
        val layout = StreamDeckLayoutMapper.toLayout(message)
        update(
            _state.value.copy(
                layout = layout,
                pendingButtonId = _state.value.pendingButtonId
                    ?.takeIf { layout.findButton(it) != null },
                failedButtonId = null,
            ),
        )
        logger.debug(
            "[StreamDeck] Layout received revision=${layout.revision} pages=${layout.pages.size}",
        )
        val missing = icons.missingIconIds(layout.iconIds())
        if (missing.isNotEmpty()) {
            logger.debug("[StreamDeck] Missing icons=${missing.size}")
            enqueueCommand(StreamDeckIconsRequestCommand(missing))
        }
    }

    fun applyIcon(message: StreamDeckIconMessage) {
        if (icons.store(message)) {
            update(_state.value.copy(iconGeneration = _state.value.iconGeneration + 1))
        }
    }

    fun invoke(buttonId: String) {
        val current = _state.value
        if (!connected || !current.available || current.pendingButtonId != null) return
        val button = current.layout.findButton(buttonId) ?: return
        if (!button.enabled) return

        update(current.copy(pendingButtonId = buttonId, failedButtonId = null))
        logger.debug("[StreamDeck] Invoke button=$buttonId")
        if (!enqueueCommand(StreamDeckInvokeCommand(buttonId, current.layout.revision))) {
            update(_state.value.copy(pendingButtonId = null, failedButtonId = buttonId))
        }
    }

    fun applyInvokeResult(message: StreamDeckInvokeResultMessage) {
        if (_state.value.pendingButtonId != message.buttonId) return
        update(
            _state.value.copy(
                pendingButtonId = null,
                failedButtonId = message.buttonId.takeIf { !message.success },
            ),
        )
        logger.debug(
            "[StreamDeck] Result " +
                if (message.success) "success" else "failed code=${message.errorCode}",
        )
        if (message.success) return
        scope.launch {
            delay(STREAM_DECK_FAILURE_DISPLAY_MILLIS)
            if (_state.value.failedButtonId == message.buttonId) clearFailure()
        }
    }

    fun clearFailure() {
        if (_state.value.failedButtonId != null) update(_state.value.copy(failedButtonId = null))
    }

    private fun update(value: StreamDeckUiState) {
        _state.value = value
        onStateChanged(value)
    }

    private companion object {
        const val STREAM_DECK_FAILURE_DISPLAY_MILLIS = 2_000L
    }
}
