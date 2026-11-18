package com.panelos.panelhome.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.domain.model.ConnectionStatus
import com.panelos.panelhome.domain.protocol.PanelProtocol
import com.panelos.panelhome.power.PowerOffDelay
import com.panelos.panelhome.power.PowerSettings
import com.panelos.panelhome.presentation.MonitoringState
import com.panelos.panelhome.presentation.MonitoringPreferences

@Composable
internal fun SettingsScreen(
    connectionStatus: ConnectionStatus,
    hostName: String?,
    powerSettings: PowerSettings,
    deviceAdminActive: Boolean,
    onWakeOnPowerChanged: (Boolean) -> Unit,
    onKeepScreenOnWhilePoweredChanged: (Boolean) -> Unit,
    onSleepAfterPowerLossChanged: (PowerOffDelay) -> Unit,
    onRequestDeviceAdmin: () -> Unit,
    onBack: () -> Unit,
    modifier: Modifier = Modifier,
    wifi: WifiSettingsModel? = null,
    monitoring: MonitoringState? = null,
    onMonitoringPreferences: (MonitoringPreferences) -> Unit = {},
) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        item {
            Column(
                modifier = Modifier
                    .widthIn(max = 760.dp)
                    .fillMaxWidth()
                    .padding(24.dp),
                verticalArrangement = Arrangement.spacedBy(20.dp),
            ) {
                Text(
                    text = stringResource(R.string.settings_title),
                    style = MaterialTheme.typography.headlineLarge,
                )
                Surface(
                    color = MaterialTheme.colorScheme.surface,
                    shape = RoundedCornerShape(24.dp),
                ) {
                    Column {
                        SettingRow(
                            label = stringResource(R.string.settings_device_name),
                            value = PanelProtocol.DEVICE_NAME,
                        )
                        Separator()
                        SettingRow(
                            label = stringResource(R.string.settings_computer),
                            value = hostName
                                ?: stringResource(R.string.settings_computer_unknown),
                        )
                        Separator()
                        SettingRow(
                            label = stringResource(R.string.settings_status),
                            value = connectionStatusLabel(connectionStatus),
                        )
                        Separator()
                        SettingRow(
                            label = stringResource(R.string.settings_transport),
                            value = stringResource(
                                if (wifi?.state?.usingWifi == true) R.string.wifi_carrier_wifi else R.string.settings_transport_value,
                            ),
                        )
                        Separator()
                        SettingRow(
                            label = stringResource(R.string.settings_protocol),
                            value = stringResource(
                                R.string.settings_protocol_value,
                                PanelProtocol.PROTOCOL_VERSION,
                            ),
                        )
                    }
                }
                if (wifi != null) WifiSettingsSection(wifi)
                if (monitoring != null) {
                    Text("Páginas", style = MaterialTheme.typography.headlineSmall)
                    Surface(color = MaterialTheme.colorScheme.surface, shape = RoundedCornerShape(24.dp)) {
                        Column {
                            SettingSwitchRow("Página Computador", "Gráficos de CPU, memória, GPU e rede.",
                                monitoring.preferences.computerPage,
                                { onMonitoringPreferences(monitoring.preferences.copy(computerPage = it)) })
                            Separator()
                            SettingSwitchRow("Página Processos", "Lista de CPU e RAM, somente para leitura.",
                                monitoring.preferences.processesPage,
                                { onMonitoringPreferences(monitoring.preferences.copy(processesPage = it)) })
                            Separator()
                            SettingSwitchRow("Página Avisos", "Valores e avisos publicados pelos programas deste PC.",
                                monitoring.preferences.noticesPage,
                                { onMonitoringPreferences(monitoring.preferences.copy(noticesPage = it)) })
                            Separator()
                            SettingSwitchRow("Mostrar avisos no modo Desktop", "Informação e sucesso aparecem como uma fita sem capturar toque.",
                                monitoring.preferences.noticesOnDesktop,
                                { onMonitoringPreferences(monitoring.preferences.copy(noticesOnDesktop = it)) })
                            Separator()
                            SettingSwitchRow("Tom ao chegar pedido do Claude Code", "Um tom curto quando o pedido aparece. Desligado por padrão.",
                                monitoring.preferences.permissionSound,
                                { onMonitoringPreferences(monitoring.preferences.copy(permissionSound = it)) })
                        }
                    }
                }
                Text(
                    text = stringResource(R.string.power_settings_section),
                    style = MaterialTheme.typography.headlineSmall,
                )
                Surface(
                    color = MaterialTheme.colorScheme.surface,
                    shape = RoundedCornerShape(24.dp),
                ) {
                    Column {
                        SettingSwitchRow(
                            label = stringResource(R.string.power_wake_on_connect),
                            summary = stringResource(R.string.power_wake_on_connect_summary),
                            checked = powerSettings.wakeOnPower,
                            onCheckedChange = onWakeOnPowerChanged,
                        )
                        Separator()
                        SettingSwitchRow(
                            label = stringResource(R.string.power_keep_screen_on),
                            summary = stringResource(R.string.power_keep_screen_on_summary),
                            checked = powerSettings.keepScreenOnWhilePowered,
                            onCheckedChange = onKeepScreenOnWhilePoweredChanged,
                        )
                        Separator()
                        DelaySettingRow(
                            delay = powerSettings.sleepAfterPowerLoss,
                            onDelayChanged = onSleepAfterPowerLossChanged,
                        )
                        Separator()
                        DeviceAdminRow(
                            active = deviceAdminActive,
                            onRequestDeviceAdmin = onRequestDeviceAdmin,
                        )
                    }
                }
                Button(
                    onClick = onBack,
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 60.dp),
                ) {
                    Text(stringResource(R.string.action_back))
                }
            }
        }
    }
}

@Composable
internal fun SettingSwitchRow(
    label: String,
    summary: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    enabled: Boolean = true,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(
                enabled = enabled,
                role = Role.Switch,
                onClick = { onCheckedChange(!checked) },
            )
            .padding(horizontal = 22.dp, vertical = 16.dp),
        horizontalArrangement = Arrangement.spacedBy(20.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(
            modifier = Modifier.weight(1f),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(text = label, style = MaterialTheme.typography.titleLarge)
            Text(
                text = summary,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Switch(checked = checked, onCheckedChange = null, enabled = enabled)
    }
}

@Composable
private fun DelaySettingRow(
    delay: PowerOffDelay,
    onDelayChanged: (PowerOffDelay) -> Unit,
) {
    var expanded by remember { mutableStateOf(false) }
    Box {
        SettingRow(
            label = stringResource(R.string.power_sleep_delay),
            value = powerDelayLabel(delay),
            modifier = Modifier.clickable { expanded = true },
        )
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false },
        ) {
            PowerOffDelay.entries.forEach { option ->
                DropdownMenuItem(
                    text = { Text(powerDelayLabel(option)) },
                    onClick = {
                        expanded = false
                        onDelayChanged(option)
                    },
                )
            }
        }
    }
}

@Composable
private fun DeviceAdminRow(
    active: Boolean,
    onRequestDeviceAdmin: () -> Unit,
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 22.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(
            text = stringResource(R.string.power_admin_heading),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = stringResource(
                if (active) {
                    R.string.power_admin_allowed
                } else {
                    R.string.power_admin_not_allowed
                },
            ),
            style = MaterialTheme.typography.titleLarge,
        )
        Text(
            text = stringResource(
                if (active) {
                    R.string.power_admin_active_summary
                } else {
                    R.string.power_admin_inactive_summary
                },
            ),
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (!active) {
            Button(onClick = onRequestDeviceAdmin) {
                Text(stringResource(R.string.power_admin_enable))
            }
        }
    }
}

@Composable
private fun powerDelayLabel(delay: PowerOffDelay): String = stringResource(
    when (delay) {
        PowerOffDelay.TEN_SECONDS -> R.string.power_delay_10_seconds
        PowerOffDelay.THIRTY_SECONDS -> R.string.power_delay_30_seconds
        PowerOffDelay.SIXTY_SECONDS -> R.string.power_delay_60_seconds
        PowerOffDelay.TWO_MINUTES -> R.string.power_delay_2_minutes
        PowerOffDelay.FIVE_MINUTES -> R.string.power_delay_5_minutes
        PowerOffDelay.NEVER -> R.string.power_delay_never
    },
)

@Composable
private fun connectionStatusLabel(status: ConnectionStatus): String = stringResource(
    when (status) {
        ConnectionStatus.CONNECTED -> R.string.connection_status_connected
        ConnectionStatus.CONNECTING -> R.string.connection_status_connecting
        ConnectionStatus.DISCONNECTED -> R.string.connection_status_disconnected
    },
)

@Composable
internal fun SettingRow(
    label: String,
    value: String,
    modifier: Modifier = Modifier,
) {
    Column(
        modifier = modifier
            .fillMaxWidth()
            .padding(horizontal = 22.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Text(
            text = value,
            style = MaterialTheme.typography.titleLarge,
        )
    }
}

@Composable
internal fun Separator() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(1.dp)
            .background(MaterialTheme.colorScheme.outline.copy(alpha = 0.5f)),
    )
}
