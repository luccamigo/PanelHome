package com.panelos.panelhome.domain.protocol

import com.panelos.panelhome.BuildConfig

/** Diagnostic build metadata. Protocol versions, not build identities, are the contract. */
data class BuildIdentity(
    val identity: String,
    val number: Long,
)

object PanelBuildIdentity {
    val current = BuildIdentity(
        identity = BuildConfig.PANEL_BUILD_IDENTITY,
        number = BuildConfig.PANEL_BUILD_NUMBER,
    )
}
