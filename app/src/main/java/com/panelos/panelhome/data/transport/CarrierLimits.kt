package com.panelos.panelhome.data.transport

internal enum class CarrierLimits(val wifi: Boolean) {
    CABLE(false), WIFI_V1(true);
    fun validate(channel: LogicalChannel, bytes: Int) {
        if (wifi && (bytes !in 0..FRAME_BYTES || channel == LogicalChannel.ASSETS || channel == LogicalChannel.AUDIO))
            throw TransportProtocolException("WiFi carrier limit exceeded")
    }
    companion object {
        const val FRAME_BYTES = 64 * 1024
        const val CHANNEL_BYTES = 2 * 1024 * 1024
        const val SESSION_BYTES = 8 * 1024 * 1024
        const val WORKSPACE_BYTES = FRAME_BYTES + 16 * 1024
        fun <T> inboundQueue(channel: LogicalChannel): java.util.concurrent.BlockingQueue<T> =
            java.util.concurrent.ArrayBlockingQueue(if (channel == LogicalChannel.VIDEO) 32 else 64)
    }
}

internal class CarrierOpenRate(private val nanoTime: () -> Long = System::nanoTime) {
    private val opens = mutableMapOf<LogicalChannel, ArrayDeque<Long>>()
    @Synchronized fun record(channel: LogicalChannel) {
        val times = opens.getOrPut(channel) { ArrayDeque() }; val now = nanoTime()
        while (times.isNotEmpty() && now - times.first() >= 60_000_000_000L) times.removeFirst()
        if (times.size >= 12) throw TransportProtocolException("WiFi channel reopen rate exceeded")
        times.addLast(now)
    }
}

internal class CarrierBudget(private val limits: CarrierLimits) {
    private val bytes = LongArray(4)
    private val items = IntArray(4)
    var liveBytes = if (limits.wifi) CarrierLimits.WORKSPACE_BYTES.toLong() else 0L
        private set
    @Synchronized fun reserve(outbound: Boolean, channel: LogicalChannel, length: Int): AutoCloseable? {
        limits.validate(channel, length)
        if (!limits.wifi) return null
        val index = (if (outbound) 2 else 0) + (if (channel == LogicalChannel.VIDEO) 1 else 0)
        if (items[index] >= (if (channel == LogicalChannel.VIDEO) 32 else 64) ||
            length > CarrierLimits.CHANNEL_BYTES - bytes[index] || length > CarrierLimits.SESSION_BYTES - liveBytes)
            throw TransportProtocolException("WiFi carrier budget exceeded")
        items[index]++; bytes[index] += length; liveBytes += length
        return object : AutoCloseable {
            private var released = false
            override fun close() = synchronized(this@CarrierBudget) {
                if (!released) { released = true; items[index]--; bytes[index] -= length; liveBytes -= length }
            }
        }
    }
}
