package com.panelos.panelhome.data.wifi

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build

/**
 * The two runtime permissions Wi-Fi can need, and nothing else: the camera (only to read the pairing QR, asked on the
 * scan screen) and, on Android 17 and above where this app targets, access to the local network. Neither is asked at
 * startup; a denial leaves the cable working and a readable reason on screen. No location, SSID or Bluetooth permission.
 */
internal object WifiPermissions {
    const val CAMERA = "android.permission.CAMERA"
    const val LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"

    fun localNetworkRequired(apiLevel: Int = Build.VERSION.SDK_INT) = apiLevel >= 37

    fun localNetworkGranted(context: Context, apiLevel: Int = Build.VERSION.SDK_INT): Boolean =
        !localNetworkRequired(apiLevel) || context.checkSelfPermission(LOCAL_NETWORK) == PackageManager.PERMISSION_GRANTED

    fun cameraGranted(context: Context): Boolean = context.checkSelfPermission(CAMERA) == PackageManager.PERMISSION_GRANTED
}