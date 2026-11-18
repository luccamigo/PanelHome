package com.panelos.panelhome.session

import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.hardware.usb.UsbAccessory
import android.hardware.usb.UsbManager
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.util.Log
import com.panelos.panelhome.MainActivity
import com.panelos.panelhome.R
import com.panelos.panelhome.data.connection.TransportPanelConnection
import com.panelos.panelhome.data.streamdeck.FileStreamDeckIconStore
import com.panelos.panelhome.data.transport.AccessoryHandle
import com.panelos.panelhome.data.transport.AdbTcpAuthentication
import com.panelos.panelhome.data.transport.AdbTcpTransport
import com.panelos.panelhome.data.transport.NativeUsbTransport
import com.panelos.panelhome.data.transport.NoWifiTransportListener
import com.panelos.panelhome.data.transport.TlsWifiSessionOpener
import com.panelos.panelhome.data.transport.WifiTransport
import com.panelos.panelhome.data.wifi.AndroidWifiIdentity
import com.panelos.panelhome.data.wifi.WifiPairedHost
import com.panelos.panelhome.data.wifi.WifiPermissions
import com.panelos.panelhome.data.transport.PanelTransport
import com.panelos.panelhome.data.transport.TransportBenchmarkClient
import com.panelos.panelhome.data.transport.TransportSelection
import com.panelos.panelhome.data.transport.UsbFailureKind
import com.panelos.panelhome.data.transport.UsbTransportListener
import com.panelos.panelhome.data.video.TransportVideoConnection
import com.panelos.panelhome.domain.model.ConnectionPhase
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.presentation.PanelSession
import com.panelos.panelhome.presentation.PanelSessionCoordinator
import java.io.File
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Foreground owner of the continuous PanelOS transport and application session.
 *
 * The Service survives everything the USB cable can do. A physical detach closes
 * a session and a descriptor; it never stops this Service, never touches the
 * Activity, and never ends the process. That was not a preference: PanelHome is
 * the tablet's HOME app, so "restart the app to recover the cable" means the
 * home screen disappears, and the recovery it used to buy is instead bought here
 * by releasing the descriptor properly and waiting for a real attach.
 */
class PanelSessionService : Service() {
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val binder = LocalBinder()
    private lateinit var runtime: PanelSessionServiceRuntime
    private lateinit var usbManager: UsbManager
    private lateinit var usbLifecycle: UsbLifecycleController
    private var activeTransport: PanelTransport? = null
    private var wifiIdentity: AndroidWifiIdentity? = null
    private var wifiController: WifiController? = null
    private var notificationJob: Job? = null
    private var benchmarkJob: Job? = null
    private var notificationPhase = ConnectionPhase.DISABLED
    private var notificationContent: SessionNotificationContent? = null
    private var everConnected = false
    private var reportedGeneration = 0L
    private var reportedStatus: ConnectionStatus? = null

    inner class LocalBinder internal constructor() : Binder() {
        val session: PanelSession get() = runtime
        val sessionGeneration: Long get() = runtime.sessionGeneration
        internal val usbState: UsbRecoveryState get() = usbLifecycle.state
        internal val usbMetrics: UsbRecoveryMetrics get() = usbLifecycle.metrics

        /** Null only if the Keystore is unusable; then the panel is cable-only exactly as before. */
        internal val wifi: WifiController? get() = wifiController
    }

    private val usbReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            // Only queueing happens here. `onReceive` runs on the main thread,
            // and everything this used to do inline - closing channels, closing
            // a descriptor, waiting on a writer thread that a cable pull had
            // already stranded - is exactly what froze the panel on detach.
            when (intent.action) {
                // While the user has chosen Wi-Fi, a cable plugged in does not take the session away from it.
                // (While Wi-Fi is still being restored at start, the first start request picks the cable up if Wi-Fi does not own it.)
                UsbManager.ACTION_USB_ACCESSORY_ATTACHED -> if (!wifiOwnsCarrier && !carrierStart.restoring) submitAttach(accessoryFrom(intent))
                UsbManager.ACTION_USB_ACCESSORY_DETACHED -> usbLifecycle.submit(
                    UsbLifecycleEvent.AccessoryDetached(accessoryFrom(intent)?.let(::identityOf)),
                )
                ACTION_USB_PERMISSION -> handleUsbPermissionResult(intent)
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        usbManager = getSystemService(UsbManager::class.java)
        createNotificationChannel()
        notificationContent = SessionNotificationPolicy.content(ConnectionPhase.WAITING_FOR_USB, null, everConnected = false)
        startForeground(NOTIFICATION_ID, buildNotification(notificationContent!!))
        registerUsbReceiver()
        runtime = PanelSessionServiceRuntime(
            scope = serviceScope,
            monitoringClock = android.os.SystemClock::elapsedRealtime,
            saveMonitoringPreferences = com.panelos.panelhome.presentation.MonitoringPreferencesStore(this)::write,
            factory = PanelSessionFactory { config ->
                PanelSessionServiceTestDependencies.sessionFactory?.invoke()?.let { session ->
                    // The seam replaces the transport, so the descriptor this
                    // config carries has no owner left. Releasing it here keeps
                    // the single-open invariant true in instrumented runs too.
                    config.accessory?.close()
                    PanelSessionInstance(session = session, close = {})
                } ?: createSession(config)
            },
            log = { message, error ->
                if (error == null) Log.i(LOG_TAG, "[Service] $message")
                else Log.e(LOG_TAG, "[Service] $message", error)
            },
        )
        usbLifecycle = UsbLifecycleController(scope = serviceScope, port = AndroidUsbLifecyclePort())
        usbLifecycle.start()
        createWifi()
        wifiController?.let { controller -> serviceScope.launch { controller.state.collect { updateNotification(notificationPhase, force = true) } } }
        notificationJob = serviceScope.launch {
            runtime.uiState.collect { state ->
                if (state.connectionStatus == ConnectionStatus.CONNECTED) everConnected = true
                observeApplicationSession(state.connectionStatus)
                updateNotification(state.connectionPhase)
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // Wi-Fi is off until the user pairs and chooses it. Only then does it, instead of the cable, own the carrier.
        carrierStart.start(intent ?: Intent())
        return START_STICKY
    }

    private val carrierStart by lazy {
        CarrierStartCoordinator<Intent>(
            scope = serviceScope,
            restoreWifi = wifiController?.let { controller -> { controller.restore() } },
            wifiOwnsCarrier = { wifiOwnsCarrier },
            onWifiTookOver = { usbLifecycle.submit(UsbLifecycleEvent.CarrierDisabled) },
            selectCable = ::selectCarrier,
            afterStart = ::maybeRunBenchmark,
            log = { Log.w(LOG_TAG, "[Wifi] $it") },
        )
    }

    private val wifiOwnsCarrier: Boolean get() = wifiController?.state?.value?.enabled == true

    /** The cable/ADB selection exactly as it was before Wi-Fi existed. */
    private fun selectCarrier(request: Intent) {
        val transportExtra = request.getStringExtra(TRANSPORT_EXTRA)
        val nativeUsb = TransportSelection.shouldUseNativeUsb(
            request.action,
            transportExtra,
            TransportSelection.hasAccessory(usbManager),
        )
        if (!nativeUsb) {
            startAdbSession(request)
        } else if (request.action == UsbManager.ACTION_USB_ACCESSORY_ATTACHED) {
            submitAttach(accessoryFrom(request))
        } else {
            // An accessory that is already attached produces no broadcast, so a
            // process that starts after the AOA transition can only find it by
            // looking. Skipping this is what left the panel on ADB/TCP after an
            // app update while the host wrote into an endpoint nobody drained.
            val current = usbManager.accessoryList?.firstOrNull()
            usbLifecycle.submit(
                UsbLifecycleEvent.ServiceStarted(
                    accessory = current?.let(::identityOf),
                    permitted = current?.let(usbManager::hasPermission) ?: false,
                ),
            )
        }
    }

    private fun createWifi() {
        val identity = runCatching { AndroidWifiIdentity(this) }.getOrNull() ?: return
        wifiIdentity = identity
        wifiController = WifiController(
            scope = serviceScope,
            identity = identity,
            modeStore = FileWifiModeStore(File(noBackupFilesDir, "panelos-wifi/mode")),
            pairer = AndroidWifiPairer(identity),
            sessionHost = AndroidWifiSessionHost(),
            localNetworkGranted = { WifiPermissions.localNetworkGranted(this) },
            log = { Log.i(LOG_TAG, "[Wifi] $it") },
        )
    }

    private inner class AndroidWifiSessionHost : WifiSessionHost {
        override fun startWifi(host: WifiPairedHost) {
            usbLifecycle.submit(UsbLifecycleEvent.CarrierDisabled)
            serviceScope.launch(Dispatchers.IO) {
                runtime.start(SessionTransportConfig(kind = SessionTransportKind.WIFI, wifiHost = host))
            }
        }

        override fun stopWifi(restoreCable: Boolean) {
            serviceScope.launch(Dispatchers.IO) {
                runtime.stopSession(SessionTransportKind.WIFI)
                if (restoreCable) withContext(Dispatchers.Main.immediate) { selectCarrier(Intent()) }
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onDestroy() {
        runCatching { unregisterReceiver(usbReceiver) }
        notificationJob?.cancel()
        benchmarkJob?.cancel()
        usbLifecycle.shutdown()
        runtime.shutdown()
        activeTransport = null
        serviceScope.cancel()
        super.onDestroy()
    }

    private fun startAdbSession(request: Intent) {
        usbLifecycle.submit(UsbLifecycleEvent.CarrierDisabled)
        val config = SessionTransportConfig(
            kind = SessionTransportKind.ADB_TCP,
            adbAuthenticationToken = AdbTcpAuthentication.parseToken(
                request.getStringExtra(ADB_AUTH_TOKEN_EXTRA),
            ),
        )
        serviceScope.launch(Dispatchers.IO) { runtime.start(config) }
    }

    /**
     * One physical attach reaches this Service twice: once as the broadcast and
     * once as the Activity intent Android starts for the same event. Both are
     * submitted; the lifecycle's debounce is what makes them one open.
     */
    private fun submitAttach(accessory: UsbAccessory?) {
        val target = accessory ?: usbManager.accessoryList?.firstOrNull() ?: return
        usbLifecycle.submit(
            UsbLifecycleEvent.AccessoryAttached(
                accessory = identityOf(target),
                permitted = usbManager.hasPermission(target),
            ),
        )
    }

    /**
     * Turns the coordinator's connection status into lifecycle events, on the
     * edges only.
     *
     * A fresh session publishes DISCONNECTED before it has tried anything, and
     * the retry loop republishes it on every attempt. Forwarding all of those
     * would report a handshake failure for a handshake that has not started -
     * harmless, because the reducer discards it, but it buries the transitions
     * that matter in a log an operator reads while holding a cable.
     */
    private fun observeApplicationSession(status: ConnectionStatus) {
        val generation = runtime.activeNativeGeneration
        if (generation == 0L) return
        if (generation != reportedGeneration) {
            reportedGeneration = generation
            reportedStatus = null
        }
        val previous = reportedStatus
        reportedStatus = status
        when {
            status == ConnectionStatus.CONNECTED && previous != ConnectionStatus.CONNECTED ->
                usbLifecycle.submit(UsbLifecycleEvent.ApplicationHandshakeSucceeded(generation))
            status == ConnectionStatus.DISCONNECTED && previous == ConnectionStatus.CONNECTED ->
                usbLifecycle.submit(
                    UsbLifecycleEvent.ApplicationHandshakeFailed(
                        generation = generation,
                        detail = "panel protocol session ended",
                    ),
                )
        }
    }

    private fun createSession(config: SessionTransportConfig): PanelSessionInstance {
        val transport = when (config.kind) {
            SessionTransportKind.NATIVE_USB -> NativeUsbTransport(
                handle = requireNotNull(config.accessory) {
                    "A native session needs a descriptor the lifecycle already opened"
                },
                generation = config.nativeGeneration,
                listener = lifecycleTransportListener,
            )
            SessionTransportKind.ADB_TCP -> AdbTcpTransport(
                authenticationToken = config.adbAuthenticationToken,
            )
            SessionTransportKind.WIFI -> WifiTransport(
                host = requireNotNull(config.wifiHost) { "A Wi-Fi session needs the paired PC" },
                opener = TlsWifiSessionOpener(requireNotNull(wifiIdentity) { "Wi-Fi identity unavailable" }),
                listener = wifiController?.transportListener ?: NoWifiTransportListener,
            )
        }
        activeTransport = transport
        val monitoringPreferences = com.panelos.panelhome.presentation.MonitoringPreferencesStore(this)
        val coordinator = PanelSessionCoordinator(
            connection = TransportPanelConnection(transport = transport),
            videoConnection = TransportVideoConnection(transport = transport),
            streamDeckIcons = FileStreamDeckIconStore(
                File(filesDir, STREAM_DECK_ICON_DIRECTORY),
            ),
            loopMediaDirectory = File(filesDir, LOOP_MEDIA_DIRECTORY),
            communicationScope = serviceScope,
            ownedTransport = transport,
            monitoringPreferences = monitoringPreferences.read(),
            saveMonitoringPreferences = monitoringPreferences::write,
            monitoringClock = android.os.SystemClock::elapsedRealtime,
            initialMonitoringState = runtime.uiState.value.monitoring,
            initialIntegrationsState = runtime.uiState.value.integrations,
            onPermissionReady = {
                com.panelos.panelhome.power.PermissionAttention.request(this,
                    monitoringPreferences.read().permissionSound)
            },
        )
        return PanelSessionInstance(
            session = coordinator,
            close = {
                coordinator.close()
                if (activeTransport === transport) activeTransport = null
            },
        )
    }

    private fun handleUsbPermissionResult(intent: Intent) {
        val accessory = accessoryFrom(intent) ?: return
        val granted = intent.getBooleanExtra(UsbManager.EXTRA_PERMISSION_GRANTED, false) &&
            usbManager.hasPermission(accessory)
        usbLifecycle.submit(
            UsbLifecycleEvent.PermissionResult(identityOf(accessory), granted),
        )
    }

    @SuppressLint("UnspecifiedRegisterReceiverFlag")
    private fun registerUsbReceiver() {
        val filter = IntentFilter().apply {
            addAction(UsbManager.ACTION_USB_ACCESSORY_ATTACHED)
            addAction(UsbManager.ACTION_USB_ACCESSORY_DETACHED)
            addAction(ACTION_USB_PERMISSION)
        }
        if (Build.VERSION.SDK_INT >= 33) {
            // USB attach/detach originates outside this app. The permission
            // result is still validated against UsbManager before use.
            registerReceiver(usbReceiver, filter, RECEIVER_EXPORTED)
        } else {
            @Suppress("DEPRECATION")
            registerReceiver(usbReceiver, filter)
        }
    }

    @Suppress("DEPRECATION")
    private fun accessoryFrom(intent: Intent): UsbAccessory? =
        if (Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(UsbManager.EXTRA_ACCESSORY, UsbAccessory::class.java)
        } else {
            intent.getParcelableExtra(UsbManager.EXTRA_ACCESSORY)
        }

    /**
     * The serial is read only to be hashed. It is never stored, never logged and
     * never leaves the process: it exists here to tell two accessories apart,
     * not to identify a device.
     */
    private fun identityOf(accessory: UsbAccessory): AccessoryIdentity = AccessoryIdentity.of(
        manufacturer = accessory.manufacturer,
        model = accessory.model,
        version = accessory.version,
        serial = runCatching { accessory.serial }.getOrNull(),
    )

    private fun maybeRunBenchmark(intent: Intent) {
        if (!intent.getBooleanExtra(TRANSPORT_BENCHMARK_EXTRA, false) ||
            benchmarkJob?.isActive == true
        ) {
            return
        }
        benchmarkJob = serviceScope.launch {
            delay(
                intent.getLongExtra(
                    TRANSPORT_BENCHMARK_DELAY_EXTRA,
                    DEFAULT_TRANSPORT_BENCHMARK_DELAY_MILLIS,
                ).coerceAtLeast(0L),
            )
            val transport = activeTransport ?: return@launch
            TransportBenchmarkClient(transport) { message -> Log.i(LOG_TAG, message) }.run()
        }
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel = NotificationChannel(
            NOTIFICATION_CHANNEL_ID,
            getString(R.string.session_notification_channel_name),
            NotificationManager.IMPORTANCE_LOW,
        ).apply {
            description = getString(R.string.session_notification_channel_description)
            setShowBadge(false)
        }
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    /** Rebuilds the notification when what it says changes: the cable phase or the path (Wi-Fi/cable) and Wi-Fi's state. */
    private fun updateNotification(phase: ConnectionPhase, force: Boolean = false) {
        notificationPhase = phase
        val content = SessionNotificationPolicy.content(phase, wifiController?.state?.value, everConnected)
        if (content == notificationContent) return
        notificationContent = content
        getSystemService(NotificationManager::class.java)
            .notify(NOTIFICATION_ID, buildNotification(content))
    }

    private fun buildNotification(content: SessionNotificationContent): Notification {
        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val builder = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, NOTIFICATION_CHANNEL_ID)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
        return builder
            .setSmallIcon(R.drawable.ic_panel_session)
            .setContentTitle(getString(R.string.session_notification_title))
            .setContentText(getString(content.text))
            .setSubText(getString(content.subtext))
            .setContentIntent(contentIntent)
            .setOngoing(true)
            .setCategory(Notification.CATEGORY_SERVICE)
            .setOnlyAlertOnce(true)
            .build()
    }

    /**
     * The only place in the app that may call `UsbManager.openAccessory`, and it
     * is reachable only from the lifecycle's serialized loop.
     */
    private inner class AndroidUsbLifecyclePort : UsbLifecyclePort {
        override suspend fun openAccessory(
            generation: Long,
            accessory: AccessoryIdentity,
        ): UsbOpenResult {
            // Defensive close-before-open. Nothing should be live here, and if
            // something is - an abandoned open that returned late - it owns a
            // descriptor that would otherwise keep every future open refused.
            withContext(Dispatchers.IO) { runtime.stopSession(SessionTransportKind.NATIVE_USB) }
            val abandoned = AtomicBoolean()
            val outcome = CompletableDeferred<UsbOpenResult>()
            serviceScope.launch(Dispatchers.IO) {
                val handle = openDescriptor(accessory, outcome) ?: return@launch
                if (abandoned.get()) {
                    handle.close()
                    return@launch
                }
                runtime.start(
                    config = SessionTransportConfig(
                        kind = SessionTransportKind.NATIVE_USB,
                        accessory = handle,
                        nativeGeneration = generation,
                    ),
                    forceRestart = true,
                )
                outcome.complete(UsbOpenResult.Opened)
            }
            val result = withTimeoutOrNull(UsbLifecycleTimings.OPEN_ACCESSORY_TIMEOUT_MILLIS) {
                outcome.await()
            }
            if (result != null) return result
            abandoned.set(true)
            return UsbOpenResult.Failed(
                kind = UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                detail = "openAccessory did not return within " +
                    UsbLifecycleTimings.OPEN_ACCESSORY_TIMEOUT_MILLIS + "ms",
            )
        }

        private fun openDescriptor(
            accessory: AccessoryIdentity,
            outcome: CompletableDeferred<UsbOpenResult>,
        ): AccessoryHandle? {
            val attached = usbManager.accessoryList.orEmpty()
            val target = attached.firstOrNull { identityOf(it) == accessory }
                ?: attached.firstOrNull()
            if (target == null) {
                outcome.complete(
                    UsbOpenResult.Failed(
                        UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                        "no Android Open Accessory is attached",
                    ),
                )
                return null
            }
            if (!usbManager.hasPermission(target)) {
                outcome.complete(
                    UsbOpenResult.Failed(
                        UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                        "USB accessory permission has not been granted",
                    ),
                )
                return null
            }
            // A null descriptor means some other holder still owns the accessory
            // or the attach is stale: `/dev/usb_accessory` is single-open.
            val descriptor = runCatching { usbManager.openAccessory(target) }.getOrNull()
            if (descriptor == null) {
                outcome.complete(
                    UsbOpenResult.Failed(
                        UsbFailureKind.OPEN_ACCESSORY_FAILURE,
                        "Android refused to open the accessory (already open, or a stale attach)",
                    ),
                )
                return null
            }
            return AccessoryHandle.of(descriptor)
        }

        override suspend fun closeSession(generation: Long, reason: String) {
            // Never on the main thread: this closes logical channels and the
            // descriptor, and both are bounded rather than instantaneous.
            withContext(Dispatchers.IO) { runtime.stopSession(SessionTransportKind.NATIVE_USB) }
        }

        override fun requestPermission(accessory: AccessoryIdentity) {
            val target = usbManager.accessoryList.orEmpty()
                .firstOrNull { identityOf(it) == accessory }
                ?: return
            val permissionIntent = Intent(ACTION_USB_PERMISSION).setPackage(packageName)
            val pendingIntent = PendingIntent.getBroadcast(
                this@PanelSessionService,
                USB_PERMISSION_REQUEST_CODE,
                permissionIntent,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
            )
            usbManager.requestPermission(target, pendingIntent)
        }

        override fun publishPhase(phase: ConnectionPhase) {
            runtime.publishPhase(phase)
            updateNotification(phase)
        }

        override fun log(message: String) {
            Log.i(LOG_TAG, message)
        }
    }

    /**
     * Bridges physical-session reports into the lifecycle queue. Every callback
     * carries the generation it belongs to, so a report that arrives after its
     * session was replaced is discarded by the reducer instead of tearing down
     * the successor.
     */
    private inner class LifecycleTransportListenerImpl : UsbTransportListener {
        override fun onTransportHandshakeSucceeded(generation: Long, sessionPrefix: String) {
            usbLifecycle.submit(
                UsbLifecycleEvent.TransportHandshakeSucceeded(generation, sessionPrefix),
            )
        }

        override fun onTransportHandshakeFailed(
            generation: Long,
            kind: UsbFailureKind,
            detail: String,
        ) {
            usbLifecycle.submit(
                UsbLifecycleEvent.TransportHandshakeFailed(generation, kind, detail),
            )
        }

        override fun onSessionSuperseded(generation: Long, sessionPrefix: String) {
            usbLifecycle.submit(
                UsbLifecycleEvent.HostSessionSuperseded(generation, sessionPrefix),
            )
        }

        override fun onPhysicalConnectionLost(
            generation: Long,
            kind: UsbFailureKind,
            detail: String,
        ) {
            usbLifecycle.submit(
                UsbLifecycleEvent.PhysicalConnectionLost(generation, kind, detail),
            )
        }
    }

    private val lifecycleTransportListener: UsbTransportListener by lazy {
        LifecycleTransportListenerImpl()
    }

    companion object {
        const val TRANSPORT_EXTRA = "transport"
        const val ADB_AUTH_TOKEN_EXTRA = "panelAdbAuthToken"
        const val TRANSPORT_BENCHMARK_EXTRA = "transportBenchmark"
        const val TRANSPORT_BENCHMARK_DELAY_EXTRA = "transportBenchmarkDelayMillis"

        private const val LOG_TAG = "PanelOS"
        private const val STREAM_DECK_ICON_DIRECTORY = "streamdeck-icons"
        private const val LOOP_MEDIA_DIRECTORY = "loop-media"
        private const val NOTIFICATION_CHANNEL_ID = "panel_session"
        private const val NOTIFICATION_ID = 1001
        private const val USB_PERMISSION_REQUEST_CODE = 1002
        private const val ACTION_USB_PERMISSION =
            "com.panelos.panelhome.action.USB_ACCESSORY_PERMISSION"
        private const val DEFAULT_TRANSPORT_BENCHMARK_DELAY_MILLIS = 10_000L
    }
}

/** Process-local seam for androidTest; production always leaves this factory null. */
internal object PanelSessionServiceTestDependencies {
    @Volatile
    var sessionFactory: (() -> PanelSession)? = null

    fun reset() {
        sessionFactory = null
    }
}
