package com.panelos.panelhome.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.foundation.layout.size
import androidx.compose.material3.ButtonDefaults
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.sp
import com.panelos.panelhome.ui.wifi.CheckMark
import com.panelos.panelhome.ui.wifi.FingerprintBlocks
import com.panelos.panelhome.ui.wifi.WarningMark
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.data.transport.WifiFailure
import com.panelos.panelhome.session.WifiConnectionState
import com.panelos.panelhome.session.WifiUiState
import com.panelos.panelhome.ui.wifi.WifiPresentation

/** The state of the Wi-Fi link plus what the user can do about it. All logic lives in WifiController. */
internal class WifiSettingsModel(
    val state: WifiUiState,
    val onEnabledChange: (Boolean) -> Unit,
    val onPair: () -> Unit,
    val onForget: () -> Unit,
    val onRetry: () -> Unit,
    val onGrantPermission: () -> Unit,
)

@Composable
internal fun WifiSettingsSection(model: WifiSettingsModel, modifier: Modifier = Modifier) {
    val state = model.state
    var confirmForget by remember { mutableStateOf(false) }
    Text(text = stringResource(R.string.wifi_section_title), style = MaterialTheme.typography.headlineSmall, modifier = modifier)
    Surface(color = MaterialTheme.colorScheme.surface, shape = RoundedCornerShape(24.dp)) {
        Column {
            // 1. Which path the panel is on, with a status mark: a check only for a live Wi-Fi link, neutral for the cable.
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 22.dp, vertical = 16.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(stringResource(R.string.wifi_carrier_label), style = MaterialTheme.typography.titleMedium)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (state.usingWifi) CheckMark(color = MaterialTheme.colorScheme.primary, size = 20.dp)
                    Text(stringResource(WifiPresentation.carrierText(state)), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            Separator()
            // 2. The switch; when it is on, the state is its summary.
            SettingSwitchRow(
                label = stringResource(R.string.wifi_switch_label),
                summary = stringResource(
                    when {
                        state.enabled -> WifiPresentation.stateText(state.state)
                        state.paired != null -> R.string.wifi_switch_summary_paired
                        else -> R.string.wifi_switch_summary_unpaired
                    },
                ),
                checked = state.enabled,
                onCheckedChange = model.onEnabledChange,
                enabled = state.supported && state.paired != null,
            )
            // 3. A failure, with the way out inside the same block.
            state.failure?.let { failure ->
                Surface(
                    color = MaterialTheme.colorScheme.errorContainer,
                    shape = RoundedCornerShape(16.dp),
                    modifier = Modifier.fillMaxWidth().padding(horizontal = 22.dp, vertical = 8.dp),
                ) {
                    Column(modifier = Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
                            WarningMark(color = MaterialTheme.colorScheme.onErrorContainer, size = 24.dp)
                            Text(stringResource(WifiPresentation.failureText(failure)), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onErrorContainer)
                        }
                        if (failure == WifiFailure.PERMISSION_DENIED) {
                            Button(onClick = model.onGrantPermission, modifier = Modifier.heightIn(min = 52.dp)) { Text(stringResource(R.string.wifi_grant_permission)) }
                        } else if (state.enabled && (state.state == WifiConnectionState.PAUSED || state.state == WifiConnectionState.RETRYING)) {
                            Button(onClick = model.onRetry, modifier = Modifier.heightIn(min = 52.dp)) { Text(stringResource(R.string.wifi_retry)) }
                        }
                    }
                }
            }
            // 4. The paired PC and its fingerprint, in the same 2 x 4 blocks as the Control Center shows.
            state.paired?.let { paired ->
                Separator()
                Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 22.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(stringResource(R.string.wifi_paired_pc), style = MaterialTheme.typography.titleMedium)
                    Text(stringResource(R.string.wifi_paired_pc_fingerprint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    FingerprintBlocks(paired.hostIdHex, fontSize = 18.sp)
                }
            }
            Separator()
            // 5. Actions: pairing is the main action while there is no PC; forgetting is a quiet, error-coloured one.
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 22.dp, vertical = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                if (state.supported) {
                    if (state.paired == null) {
                        Button(onClick = model.onPair, modifier = Modifier.heightIn(min = 52.dp)) { Text(stringResource(R.string.wifi_pair_button)) }
                    } else {
                        OutlinedButton(onClick = model.onPair, modifier = Modifier.heightIn(min = 52.dp)) { Text(stringResource(R.string.wifi_pair_button)) }
                    }
                }
                if (state.paired != null) {
                    OutlinedButton(
                        onClick = { confirmForget = true },
                        colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
                        modifier = Modifier.heightIn(min = 52.dp),
                    ) { Text(stringResource(R.string.wifi_forget_button)) }
                }
            }
        }
    }
    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text(stringResource(R.string.wifi_forget_title)) },
            text = { Text(stringResource(R.string.wifi_forget_body)) },
            confirmButton = {
                TextButton(onClick = { confirmForget = false; model.onForget() }) {
                    Text(stringResource(R.string.wifi_forget_confirm), color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text(stringResource(R.string.wifi_dialog_cancel)) } },
        )
    }
}