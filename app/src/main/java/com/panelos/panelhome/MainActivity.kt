package com.panelos.panelhome

import android.Manifest
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.content.pm.PackageManager
import android.graphics.Color
import android.hardware.usb.UsbManager
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.panelos.panelhome.power.PowerBehaviorViewModel
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelViewModel
import com.panelos.panelhome.session.PanelSessionService
import com.panelos.panelhome.data.wifi.WifiPermissions
import com.panelos.panelhome.ui.settings.WifiSettingsModel
import com.panelos.panelhome.ui.wifi.WifiPairActivity
import com.panelos.panelhome.ui.wifi.WifiViewModel
import androidx.activity.result.contract.ActivityResultContracts
import com.panelos.panelhome.ui.PanelApp
import com.panelos.panelhome.ui.enterImmersiveMode
import com.panelos.panelhome.ui.theme.PanelHomeTheme
import androidx.compose.runtime.getValue

class MainActivity : ComponentActivity() {
    private val panelViewModel: PanelViewModel by viewModels()
    private val powerBehaviorViewModel: PowerBehaviorViewModel by viewModels()
    private val wifiViewModel: WifiViewModel by viewModels()
    private var boundWifi: com.panelos.panelhome.session.WifiController? = null
    private val localNetworkPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        // Granted or not, ask the controller to look again: it reports the reason itself if still missing.
        wifiViewModel.retry()
    }
    private var boundSession: PanelSession? = null
    private var serviceBound = false

    private val sessionConnection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, service: IBinder) {
            val binder = service as PanelSessionService.LocalBinder
            val session = binder.session
            boundSession = session
            panelViewModel.attachSession(session)
            boundWifi = binder.wifi
            wifiViewModel.attach(boundWifi)
        }

        override fun onServiceDisconnected(name: ComponentName) {
            wifiViewModel.detach(boundWifi); boundWifi = null
            panelViewModel.detachSession(boundSession)
            boundSession = null
            serviceBound = false
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        startSessionService(intent)
        requestNotificationPermissionIfNeeded()
        powerBehaviorViewModel.attach(this)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(Color.TRANSPARENT),
        )
        window.enterImmersiveMode()
        setContent {
            val powerSettings by powerBehaviorViewModel.settings.collectAsStateWithLifecycle()
            val deviceAdminActive by
                powerBehaviorViewModel.deviceAdminActive.collectAsStateWithLifecycle()
            val wifiState by wifiViewModel.state.collectAsStateWithLifecycle()
            PanelHomeTheme {
                PanelApp(
                    wifi = wifiState?.let { state ->
                        WifiSettingsModel(
                            state = state,
                            onEnabledChange = wifiViewModel::setEnabled,
                            onPair = { startActivity(Intent(this, WifiPairActivity::class.java)) },
                            onForget = wifiViewModel::forget,
                            onRetry = wifiViewModel::retry,
                            onGrantPermission = { localNetworkPermission.launch(WifiPermissions.LOCAL_NETWORK) },
                        )
                    },
                    viewModel = panelViewModel,
                    powerSettings = powerSettings,
                    deviceAdminActive = deviceAdminActive,
                    onWakeOnPowerChanged = powerBehaviorViewModel::setWakeOnPower,
                    onKeepScreenOnWhilePoweredChanged =
                        powerBehaviorViewModel::setKeepScreenOnWhilePowered,
                    onSleepAfterPowerLossChanged = powerBehaviorViewModel::setSleepAfterPowerLoss,
                    onRequestDeviceAdmin = { powerBehaviorViewModel.requestDeviceAdmin(this) },
                    // The session service looks for the PC again; the same call that started it.
                    onRetryConnection = { startSessionService(Intent()) },
                )
            }
        }
    }

    override fun onStart() {
        super.onStart()
        serviceBound = bindService(
            Intent(this, PanelSessionService::class.java),
            sessionConnection,
            Context.BIND_AUTO_CREATE,
        )
    }

    override fun onStop() {
        panelViewModel.setUiTouchActive(false)
        if (serviceBound) {
            wifiViewModel.detach(boundWifi); boundWifi = null
            panelViewModel.detachSession(boundSession)
            unbindService(sessionConnection)
            boundSession = null
            serviceBound = false
        }
        super.onStop()
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        val explicitlyChangesSession =
            intent.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED ||
                intent.hasExtra(PanelSessionService.TRANSPORT_EXTRA) ||
                intent.getBooleanExtra(PanelSessionService.TRANSPORT_BENCHMARK_EXTRA, false)
        if (explicitlyChangesSession) {
            setIntent(intent)
            startSessionService(intent)
        }
    }

    override fun onResume() {
        super.onResume()
        window.enterImmersiveMode()
        powerBehaviorViewModel.refreshDeviceAdminState()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // The system brings the bars back after a dialog or the keyboard; hide them again.
        if (hasFocus) window.enterImmersiveMode()
    }

    override fun onDestroy() {
        powerBehaviorViewModel.detach(this)
        super.onDestroy()
    }

    internal fun panelViewModelForTesting(): PanelViewModel = panelViewModel

    internal fun isSessionBoundForTesting(): Boolean = serviceBound && boundSession != null

    private fun startSessionService(source: Intent) {
        val serviceIntent = Intent(this, PanelSessionService::class.java).apply {
            action = source.action
            source.extras?.let(::putExtras)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(serviceIntent)
        } else {
            startService(serviceIntent)
        }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1001)
        }
    }
}
