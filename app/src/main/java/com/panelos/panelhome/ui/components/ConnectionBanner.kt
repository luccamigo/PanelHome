package com.panelos.panelhome.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionStatus

/**
 * The strip at the top of a page when the PC is not there. Connecting has no
 * button (it resolves by itself); disconnected always offers "Tentar de novo"
 * and the way to Configurações, so the user is never left at a dead end.
 */
@Composable
internal fun ConnectionBanner(
    status: ConnectionStatus,
    onRetry: () -> Unit,
    onOpenSettings: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val connecting = status == ConnectionStatus.CONNECTING
    AnimatedStatusBanner(
        visible = status != ConnectionStatus.CONNECTED,
        modifier = modifier,
        tone = if (connecting) BannerTone.Connecting else BannerTone.Disconnected,
        title = stringResource(if (connecting) R.string.banner_connecting_title else R.string.banner_disconnected_title),
        message = stringResource(if (connecting) R.string.banner_connecting_message else R.string.banner_disconnected_message),
        primary = if (connecting) null else BannerAction(stringResource(R.string.action_try_again), onRetry, R.drawable.ic_refresh),
        secondary = if (connecting) null else BannerAction(stringResource(R.string.dashboard_open_settings), onOpenSettings),
    )
}
