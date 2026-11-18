package com.panelos.panelhome.domain.model

/**
 * Runtime display characteristics kept centrally for future desktop negotiation.
 * The profile is intentionally transport-agnostic and is not sent automatically.
 */
data class DeviceProfile(
    val displayWidth: Int,
    val displayHeight: Int,
    val refreshRate: Float,
) {
    init {
        require(displayWidth > 0) { "displayWidth must be positive" }
        require(displayHeight > 0) { "displayHeight must be positive" }
        require(refreshRate > 0f && refreshRate.isFinite()) {
            "refreshRate must be positive and finite"
        }
    }
}
