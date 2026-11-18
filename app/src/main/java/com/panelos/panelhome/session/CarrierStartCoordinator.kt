package com.panelos.panelhome.session

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * Decides, once per Service start, whether Wi-Fi or the cable owns the carrier, without ever blocking the main thread on
 * the Keystore and without letting a Wi-Fi problem take the cable down: if restoring Wi-Fi fails or throws, the cable path
 * runs exactly as it did before Wi-Fi existed. Start requests that arrive while the restore is still running are kept and
 * handled, in order, after it.
 */
internal class CarrierStartCoordinator<R>(
    private val scope: CoroutineScope,
    /** Null when Wi-Fi is unavailable (no Keystore). Returns true when the user's Wi-Fi choice took over the carrier. */
    private val restoreWifi: (suspend () -> Boolean)?,
    private val wifiOwnsCarrier: () -> Boolean,
    private val onWifiTookOver: () -> Unit,
    /** The cable/ADB selection. */
    private val selectCable: (R) -> Unit,
    /** Anything every start does regardless of the carrier. */
    private val afterStart: (R) -> Unit,
    private val log: (String) -> Unit = {},
) {
    private var restored = false
    private val waiting = ArrayList<R>()

    /** True from the first start until Wi-Fi has been restored: a cable attach in this window must not start a cable session. */
    var restoring = false
        private set

    fun start(request: R) {
        if (restoring) { waiting += request; return }
        if (restored) { handle(request); return }
        restored = true
        restoring = true
        waiting += request
        scope.launch {
            val tookOver = try { restoreWifi?.invoke() == true } catch (error: Exception) {
                log("Wi-Fi restore failed, using the cable: ${error::class.java.simpleName}"); false
            }
            restoring = false
            if (tookOver) onWifiTookOver()
            val pending = waiting.toList(); waiting.clear()
            pending.forEach(::handle)
        }
    }

    private fun handle(request: R) {
        if (!wifiOwnsCarrier()) selectCable(request)
        afterStart(request)
    }
}