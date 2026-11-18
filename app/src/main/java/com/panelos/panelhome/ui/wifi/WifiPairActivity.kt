package com.panelos.panelhome.ui.wifi

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import android.os.IBinder
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.view.WindowCompat
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.panelos.panelhome.data.wifi.WifiPermissions
import com.panelos.panelhome.session.PanelSessionService
import com.panelos.panelhome.session.WifiController
import com.panelos.panelhome.ui.enterImmersiveMode
import com.panelos.panelhome.ui.theme.PanelHomeTheme
import androidx.compose.runtime.remember
import com.panelos.panelhome.session.WifiUiState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Pairing screen. It only collects the QR or the code and hands it to the controller that lives in the Service; it
 * owns no pairing state. FLAG_SECURE keeps the typed code and the scan out of screenshots and the recents thumbnail.
 */
class WifiPairActivity : ComponentActivity() {
    private val controllerFlow = MutableStateFlow<WifiController?>(null)
    private var bound = false
    private var cameraGranted by mutableStateOf(false)
    private var cameraBlocked by mutableStateOf(false)
    private var localNetworkDenied by mutableStateOf(false)
    private var pendingAction: (() -> Unit)? = null

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            controllerFlow.value = (service as PanelSessionService.LocalBinder).wifi
        }
        override fun onServiceDisconnected(name: ComponentName) { controllerFlow.value = null }
    }

    private val cameraPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        cameraGranted = granted
        // Denied and the system will not ask again: only the app settings can change it now.
        cameraBlocked = !granted && !shouldShowRequestPermissionRationale(WifiPermissions.CAMERA)
    }
    private val localNetworkPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        localNetworkDenied = !granted
        val action = pendingAction; pendingAction = null
        if (granted) action?.invoke()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        // Draw edge to edge so the keyboard inset can be applied (imePadding) and the actions stay above it.
        WindowCompat.setDecorFitsSystemWindows(window, false)
        window.enterImmersiveMode()
        cameraGranted = WifiPermissions.cameraGranted(this)
        bound = bindService(Intent(this, PanelSessionService::class.java), connection, Context.BIND_AUTO_CREATE)
        setContent {
            PanelHomeTheme {
                val controller by controllerFlow.collectAsStateWithLifecycle()
                val empty = remember { MutableStateFlow<WifiUiState?>(null) }
                val source: StateFlow<WifiUiState?> = controller?.state ?: empty
                val state by source.collectAsStateWithLifecycle()
                WifiPairScreen(
                    state = state,
                    cameraGranted = cameraGranted,
                    cameraBlocked = cameraBlocked,
                    onRequestCamera = { cameraPermission.launch(WifiPermissions.CAMERA) },
                    onOpenAppSettings = {
                        startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", packageName, null)))
                    },
                    onQr = { bytes -> withLocalNetwork { controller?.pairFromQr(bytes) } },
                    onCode = { text -> withLocalNetwork { controller?.pairFromCode(text) } },
                    onCancelPairing = { controller?.cancelPairing() },
                    onDismissResult = { controller?.dismissPairingResult() },
                    onClose = { controller?.cancelPairingIfWorking(); finish() },
                    localNetworkDenied = localNetworkDenied,
                    onGrantLocalNetwork = { localNetworkPermission.launch(WifiPermissions.LOCAL_NETWORK) },
                )
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) window.enterImmersiveMode()
    }

    override fun onResume() {
        super.onResume()
        window.enterImmersiveMode()
        // The user may have granted the camera in the app settings.
        cameraGranted = WifiPermissions.cameraGranted(this)
        if (cameraGranted) cameraBlocked = false
    }

    /** On Android 17+ the LAN needs a runtime permission; ask first, then run the pairing once it is granted. */
    private fun withLocalNetwork(action: () -> Unit) {
        if (WifiPermissions.localNetworkGranted(this)) { localNetworkDenied = false; action(); return }
        pendingAction = action
        localNetworkPermission.launch(WifiPermissions.LOCAL_NETWORK)
    }

    override fun onDestroy() {
        if (bound) runCatching { unbindService(connection) }
        super.onDestroy()
    }
}