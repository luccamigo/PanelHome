package com.panelos.panelhome.ui.wifi

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.panelos.panelhome.session.WifiController
import com.panelos.panelhome.session.WifiUiState
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/** Activity-scoped projection of the Service-owned WifiController; unbinding never changes the Wi-Fi decision. */
internal class WifiViewModel : ViewModel() {
    private val _state = MutableStateFlow<WifiUiState?>(null)
    val state: StateFlow<WifiUiState?> = _state.asStateFlow()
    private var controller: WifiController? = null
    private var job: Job? = null

    fun attach(next: WifiController?) {
        if (controller === next && job?.isActive == true) return
        job?.cancel()
        controller = next
        _state.value = next?.state?.value
        job = next?.let { viewModelScope.launch { it.state.collect { value -> _state.value = value } } }
    }

    fun detach(previous: WifiController?) {
        if (controller !== previous) return
        job?.cancel(); job = null; controller = null; _state.value = null
    }

    fun setEnabled(enabled: Boolean) { controller?.setEnabled(enabled) }
    fun forget() { controller?.forget() }
    fun retry() { controller?.retry() }
}