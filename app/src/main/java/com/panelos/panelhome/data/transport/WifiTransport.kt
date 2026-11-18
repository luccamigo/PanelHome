package com.panelos.panelhome.data.transport

import android.os.Build
import androidx.annotation.RequiresApi
import com.panelos.panelhome.data.wifi.WifiIdentityPort
import com.panelos.panelhome.data.wifi.WifiPairedHost
import com.panelos.panelhome.data.wifi.WifiTls
import java.io.EOFException
import java.io.IOException
import java.net.ConnectException
import java.net.InetSocketAddress
import java.net.NoRouteToHostException
import java.net.Socket
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.security.cert.CertificateException
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket

/** Why the Wi-Fi link is not up. The panel shows this, in words; it is never the PC's text. */
internal enum class WifiFailure {
    UNSUPPORTED_PLATFORM, PERMISSION_DENIED, NOT_PAIRED, UNREACHABLE, TIMEOUT, IDENTITY_MISMATCH, REFUSED_BY_PC, PROTOCOL,
}

internal class WifiConnectException(val failure: WifiFailure, cause: Throwable? = null) : IOException("Wi-Fi connect failed: $failure", cause)

internal interface WifiTransportListener {
    fun onConnecting() {}
    fun onConnected() {}
    /** [paused] means no further attempt is made until the user acts (see [WifiTransport.resume]). */
    fun onFailed(failure: WifiFailure, paused: Boolean) {}
    fun onSessionLost() {}
}

internal object NoWifiTransportListener : WifiTransportListener

/** Opens one authenticated PLMX session to the paired PC. Blocking; throws [WifiConnectException]. */
internal fun interface WifiSessionOpener {
    fun open(host: WifiPairedHost, metrics: TransportMetrics, onClosed: (Throwable?) -> Unit): MultiplexedTransportSession
}

/**
 * The Wi-Fi carrier: one mutual-TLS 1.3 connection to the pinned PC, PLMX v2 on top with the WiFi limits (64 KiB frames,
 * Control and Video only, bounded queues), the same logical channels as the cable. It never discovers anything, never
 * connects anywhere but the paired address, and stops trying when the PC's answer says the pairing is gone.
 */
internal class WifiTransport(
    private val host: WifiPairedHost,
    private val opener: WifiSessionOpener,
    private val listener: WifiTransportListener = NoWifiTransportListener,
) : PanelTransport {
    private val lock = Any()
    @Volatile private var session: MultiplexedTransportSession? = null
    @Volatile private var closed = false
    private var consecutiveRefusals = 0
    @Volatile private var pausedFailure: WifiFailure? = null

    override val name = "wifi"
    override val metrics = TransportMetrics()

    override suspend fun connect(channel: LogicalChannel): PanelByteConnection {
        require(channel == LogicalChannel.CONTROL || channel == LogicalChannel.VIDEO) { "WiFi carries Control and Video only" }
        val active = synchronized(lock) {
            if (closed) throw TransportDisconnectedException("Wi-Fi transport is closed")
            pausedFailure?.let { throw TransportDisconnectedException("Wi-Fi is paused: $it") }
            session?.takeIf { it.isHealthy } ?: openSession()
        }
        return try {
            active.open(channel)
        } catch (error: Exception) {
            throw TransportDisconnectedException("Wi-Fi logical channel failed (${error::class.java.simpleName}: ${error.message})", error)
        }
    }

    private fun openSession(): MultiplexedTransportSession {
        session?.let { runCatching { it.close() } }
        session = null
        listener.onConnecting()
        val opened = try {
            opener.open(host, metrics) { reason -> if (!closed && reason != null) listener.onSessionLost() }
        } catch (error: Throwable) {
            val failure = (error as? WifiConnectException)?.failure ?: WifiFailure.PROTOCOL
            val permanent = when (failure) {
                WifiFailure.IDENTITY_MISMATCH, WifiFailure.PERMISSION_DENIED, WifiFailure.UNSUPPORTED_PLATFORM, WifiFailure.NOT_PAIRED -> true
                // The PC closed on us after TLS: revoked, not paired, or a hiccup. Three in a row is a refusal.
                WifiFailure.REFUSED_BY_PC -> ++consecutiveRefusals >= REFUSALS_BEFORE_PAUSE
                else -> false
            }
            if (failure != WifiFailure.REFUSED_BY_PC) consecutiveRefusals = 0
            if (permanent) pausedFailure = failure
            listener.onFailed(failure, permanent)
            throw TransportDisconnectedException("Wi-Fi connect failed: $failure", error)
        }
        consecutiveRefusals = 0
        if (closed) { opened.close(); throw TransportDisconnectedException("Wi-Fi transport closed while connecting") }
        session = opened
        listener.onConnected()
        return opened
    }

    /** The user asked to try again after a pause (re-pair, or "tentar de novo"). */
    fun resume() { synchronized(lock) { pausedFailure = null; consecutiveRefusals = 0 } }

    override fun close() {
        closed = true
        val active = session
        session = null
        runCatching { active?.close() }
    }

    companion object {
        const val REFUSALS_BEFORE_PAUSE = 3
    }
}

/** The real opener: TCP to the paired address, mutual TLS 1.3 pinned to the PC, then the PLMX handshake. */
internal class TlsWifiSessionOpener(
    private val identity: WifiIdentityPort,
    private val apiLevel: Int = Build.VERSION.SDK_INT,
    private val connectTimeoutMillis: Int = 3_000,
    private val handshakeTimeoutMillis: Int = 8_000,
    /** Test seam only. */
    private val endpoint: (String, Int) -> InetSocketAddress = { ip, port -> InetSocketAddress(ip, port) },
    /** Test seam only: android.os.Process.myTid() does not exist on the host JVM. */
    private val threadId: () -> Int = { android.os.Process.myTid() },
) : WifiSessionOpener {
    @RequiresApi(28)
    private fun openSecure(host: WifiPairedHost, metrics: TransportMetrics, onClosed: (Throwable?) -> Unit): MultiplexedTransportSession {
        val plain = Socket()
        var socket: SSLSocket? = null
        var phase = WifiConnectPhase.TCP
        try {
            plain.tcpNoDelay = true
            plain.connect(endpoint(host.ip, host.sessionPort), connectTimeoutMillis)
            phase = WifiConnectPhase.TLS
            val root = CertificateFactory.getInstance("X.509").generateCertificate(host.rootDer.inputStream()) as X509Certificate
            val entry = identity.entry(host.hostId)
            val context = WifiTls.context(host.hostId, root, entry, apiLevel)
            socket = context.socketFactory.createSocket(plain, host.ip, host.sessionPort, true) as SSLSocket
            WifiTls.configure(socket, host.hostId, pairing = false, apiLevel)
            socket.startHandshake()
            WifiTls.checkNegotiated(socket, pairing = false, apiLevel)
            phase = WifiConnectPhase.PLMX
            val secure = socket
            secure.soTimeout = handshakeTimeoutMillis
            val session = MultiplexedTransportSession(
                physicalInput = secure.inputStream,
                physicalOutput = secure.outputStream,
                closePhysical = { runCatching { secure.close() }; runCatching { plain.close() } },
                metrics = metrics,
                onClosed = onClosed,
                limits = CarrierLimits.WIFI_V1,
                threadId = threadId,
            )
            session.start()
            secure.soTimeout = 0 // the application heartbeat (5 s ping, 15 s timeout) supervises the link from here
            return session
        } catch (error: Throwable) {
            runCatching { socket?.close() }; runCatching { plain.close() }
            throw WifiConnectException(classify(error, phase), error)
        }
    }

    override fun open(host: WifiPairedHost, metrics: TransportMetrics, onClosed: (Throwable?) -> Unit): MultiplexedTransportSession {
        if (apiLevel < 28) throw WifiConnectException(WifiFailure.UNSUPPORTED_PLATFORM)
        return openSecureChecked(host, metrics, onClosed)
    }

    // Lint cannot see that the guard above is the API check; the platform call is only reachable at API 28+.
    @android.annotation.SuppressLint("NewApi")
    private fun openSecureChecked(host: WifiPairedHost, metrics: TransportMetrics, onClosed: (Throwable?) -> Unit) =
        openSecure(host, metrics, onClosed)

    companion object {
        /** Where the failure happened decides what it means; see [classify]. */
        internal fun classify(error: Throwable, phase: WifiConnectPhase): WifiFailure {
            val causes = generateSequence(error) { it.cause }.take(8).toList()
            return when {
                causes.any { it is SecurityException } -> WifiFailure.PERMISSION_DENIED
                causes.any { it is CertificateException } -> WifiFailure.IDENTITY_MISMATCH
                causes.any { it is SocketTimeoutException } ->
                    if (phase == WifiConnectPhase.TCP) WifiFailure.UNREACHABLE else WifiFailure.TIMEOUT
                causes.any { it is ConnectException || it is NoRouteToHostException || it is UnknownHostException } -> WifiFailure.UNREACHABLE
                // TLS 1.3 finishes the client handshake before the PC has judged the client certificate, so a revoked or
                // unknown device sees the close or alert only when it waits for the PLMX HELLO.
                phase == WifiConnectPhase.PLMX && causes.any { it is EOFException || it is SSLException || it is java.net.SocketException } ->
                    WifiFailure.REFUSED_BY_PC
                phase == WifiConnectPhase.TLS && causes.any { it is SSLException } -> WifiFailure.REFUSED_BY_PC
                causes.any { it is SSLException || it is java.net.SocketException || it is EOFException } -> WifiFailure.UNREACHABLE
                else -> WifiFailure.PROTOCOL
            }
        }
    }
}

/** TCP connect, the TLS 1.3 handshake, and the PLMX handshake that follows a successful TLS handshake. */
internal enum class WifiConnectPhase { TCP, TLS, PLMX }