package com.panelos.panelhome.ui.wifi

import android.content.res.Configuration
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.unit.dp
import com.panelos.panelhome.R
import com.panelos.panelhome.data.wifi.PairingStage
import com.panelos.panelhome.session.WifiPairingUi
import com.panelos.panelhome.session.WifiUiState
import kotlinx.coroutines.delay

private enum class PairInput { SCAN, CODE }

private const val NO_READ_HINT_MILLIS = 10_000L
private const val FOREIGN_QR_NOTICE_MILLIS = 3_000L
private const val DONE_CLOSE_MILLIS = 2_000L

/**
 * Pair with a PC: read its QR code with the camera, or type the code it shows. It scrolls and keeps the keyboard from
 * covering the actions; in landscape the camera takes the left 55% and the steps and buttons the right 45%. While the PC waits
 * for its operator the screen shows this tablet's full key fingerprint so the two screens can be compared before anyone
 * approves. The QR text and the typed code live only in this composition and in the controller call; they are never saved or
 * logged. A QR from another system is a short notice, not an error screen, and cancelling is not a failure.
 */
@Composable
internal fun WifiPairScreen(
    state: WifiUiState?,
    cameraGranted: Boolean,
    cameraBlocked: Boolean,
    onRequestCamera: () -> Unit,
    onOpenAppSettings: () -> Unit,
    onQr: (ByteArray) -> Unit,
    onCode: (String) -> Unit,
    onCancelPairing: () -> Unit,
    onDismissResult: () -> Unit,
    onClose: () -> Unit,
    localNetworkDenied: Boolean,
    onGrantLocalNetwork: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val landscape = LocalConfiguration.current.orientation == Configuration.ORIENTATION_LANDSCAPE
    var input by remember { mutableStateOf(PairInput.SCAN) }
    var cameraUnavailable by remember { mutableStateOf(false) }
    val pairing = state?.pairing ?: WifiPairingUi.Idle
    LaunchedEffect(pairing) {
        if (pairing is WifiPairingUi.Done) { delay(DONE_CLOSE_MILLIS); onClose() }
        if (pairing is WifiPairingUi.ForeignQr) { delay(FOREIGN_QR_NOTICE_MILLIS); onDismissResult() }
    }
    Column(modifier = modifier.fillMaxSize().systemBarsPadding().imePadding().padding(horizontal = 24.dp, vertical = 16.dp)) {
        // Fechar is always visible at the top right, not only at the end of the page.
        Row(modifier = Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.wifi_pair_title), style = MaterialTheme.typography.headlineLarge, modifier = Modifier.weight(1f))
            OutlinedButton(onClick = onClose, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_close)) }
        }
        Spacer(Modifier.height(16.dp))
        Box(modifier = Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.TopCenter) {
            Column(
                modifier = Modifier.widthIn(max = if (landscape) 1100.dp else 820.dp).fillMaxWidth().verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(16.dp),
            ) {
                when (pairing) {
                    is WifiPairingUi.Working -> WorkingPanel(pairing.stage, pairing.fingerprint, landscape, onCancelPairing)
                    WifiPairingUi.Done -> Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                        CheckMark(color = MaterialTheme.colorScheme.primary, size = 36.dp)
                        Text(stringResource(R.string.wifi_pair_done), style = MaterialTheme.typography.titleLarge)
                    }
                    is WifiPairingUi.Failed -> {
                        val content = WifiPresentation.pairingFailure(pairing.reason)
                        if (content != null) FailurePanel(content, onRetry = onDismissResult, onClose = onClose)
                    }
                    else -> InputPanel(
                        landscape = landscape, input = input, cameraUnavailable = cameraUnavailable, cameraGranted = cameraGranted,
                        cameraBlocked = cameraBlocked, pairing = pairing, localNetworkDenied = localNetworkDenied,
                        onInput = { input = it }, onCameraUnavailable = { cameraUnavailable = true },
                        onRequestCamera = onRequestCamera, onOpenAppSettings = onOpenAppSettings, onQr = onQr, onCode = onCode,
                        onGrantLocalNetwork = onGrantLocalNetwork,
                    )
                }
            }
        }
    }
}

@Composable
private fun InputPanel(
    landscape: Boolean, input: PairInput, cameraUnavailable: Boolean, cameraGranted: Boolean, cameraBlocked: Boolean,
    pairing: WifiPairingUi, localNetworkDenied: Boolean, onInput: (PairInput) -> Unit, onCameraUnavailable: () -> Unit,
    onRequestCamera: () -> Unit, onOpenAppSettings: () -> Unit, onQr: (ByteArray) -> Unit, onCode: (String) -> Unit,
    onGrantLocalNetwork: () -> Unit,
) {
    val scanning = input == PairInput.SCAN && !cameraUnavailable
    var noRead by remember { mutableStateOf(false) }
    LaunchedEffect(scanning, cameraGranted) {
        noRead = false
        if (scanning && cameraGranted) { delay(NO_READ_HINT_MILLIS); noRead = true }
    }
    if (localNetworkDenied) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.wifi_pair_local_network), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.error, modifier = Modifier.weight(1f))
            Button(onClick = onGrantLocalNetwork, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_grant_permission)) }
        }
    }
    if (!scanning) {
        if (cameraUnavailable) Text(stringResource(R.string.wifi_pair_no_camera), style = MaterialTheme.typography.bodyMedium)
        CodeEntry(invalid = pairing == WifiPairingUi.InvalidInput, onCode = onCode)
        if (!cameraUnavailable) {
            OutlinedButton(onClick = { onInput(PairInput.SCAN) }, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_scan_qr)) }
        }
        return
    }
    val actions: @Composable () -> Unit = {
        Text(stringResource(R.string.wifi_pair_instruction), style = MaterialTheme.typography.bodyLarge)
        if (cameraGranted && noRead && pairing != WifiPairingUi.ForeignQr) {
            Text(stringResource(R.string.wifi_pair_scan_hint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (!cameraGranted && cameraBlocked) {
            Text(stringResource(R.string.wifi_pair_camera_blocked), style = MaterialTheme.typography.titleMedium)
            Button(onClick = onOpenAppSettings, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_open_settings)) }
        } else if (!cameraGranted) {
            Text(stringResource(R.string.wifi_pair_camera_rationale), style = MaterialTheme.typography.bodyMedium)
            Button(onClick = onRequestCamera, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_allow_camera)) }
        }
        OutlinedButton(onClick = { onInput(PairInput.CODE) }, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_type_code)) }
    }
    val camera: @Composable (Modifier) -> Unit = { cameraModifier ->
        Box(modifier = cameraModifier) {
            QrScannerPreview(onQr = onQr, onCameraUnavailable = onCameraUnavailable, modifier = Modifier.fillMaxSize())
            // Aiming frame: a square of about 62% of the shorter side.
            Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Box(modifier = Modifier.fillMaxHeight(0.62f).aspectRatio(1f).border(BorderStroke(2.dp, MaterialTheme.colorScheme.primary), RoundedCornerShape(12.dp)))
            }
            if (pairing == WifiPairingUi.ForeignQr) {
                Surface(
                    color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.align(Alignment.BottomCenter).padding(12.dp).semantics { liveRegion = LiveRegionMode.Polite },
                ) {
                    Text(stringResource(R.string.wifi_pair_foreign_qr), style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp))
                }
            }
        }
    }
    if (landscape && cameraGranted) {
        Row(horizontalArrangement = Arrangement.spacedBy(24.dp)) {
            camera(Modifier.weight(0.55f).heightIn(min = 360.dp).height(400.dp))
            Column(modifier = Modifier.weight(0.45f), verticalArrangement = Arrangement.spacedBy(16.dp)) { actions() }
        }
    } else {
        if (cameraGranted) camera(Modifier.fillMaxWidth().height(320.dp))
        actions()
    }
}

/** The four steps, the title of the current one and, while the PC waits for its operator, this tablet's fingerprint. */
@Composable
private fun WorkingPanel(stage: PairingStage, fingerprint: String?, landscape: Boolean, onCancel: () -> Unit) {
    val waiting = stage == PairingStage.WAITING_FOR_APPROVAL
    var remaining by remember { mutableIntStateOf(WifiPresentation.APPROVAL_SECONDS) }
    LaunchedEffect(waiting) {
        if (waiting) {
            remaining = WifiPresentation.APPROVAL_SECONDS
            while (remaining > 0) { delay(1_000); remaining-- }
        }
    }
    val steps: @Composable () -> Unit = {
        Text(stringResource(WifiPresentation.stageText(stage)), style = MaterialTheme.typography.titleLarge, modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite })
        StepList(current = WifiPresentation.stepIndex(stage))
        if (waiting) {
            Text(stringResource(R.string.wifi_pair_waiting_body, WifiPresentation.countdown(remaining)), style = MaterialTheme.typography.bodyLarge)
        }
        OutlinedButton(onClick = onCancel, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_cancel)) }
    }
    val print: @Composable () -> Unit = {
        if (fingerprint != null) {
            Text(stringResource(R.string.wifi_pair_fingerprint), style = MaterialTheme.typography.titleMedium)
            FingerprintBlocks(fingerprint)
            Text(stringResource(R.string.wifi_pair_fingerprint_hint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    if (landscape && fingerprint != null) {
        Row(horizontalArrangement = Arrangement.spacedBy(32.dp)) {
            Column(modifier = Modifier.weight(0.45f), verticalArrangement = Arrangement.spacedBy(16.dp)) { steps() }
            Column(modifier = Modifier.weight(0.55f), verticalArrangement = Arrangement.spacedBy(12.dp)) { print() }
        }
    } else {
        steps(); print()
    }
}

@Composable
private fun StepList(current: Int) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        WifiPresentation.stepLabels.forEachIndexed { index, label ->
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(modifier = Modifier.size(28.dp), contentAlignment = Alignment.Center) {
                    when {
                        index < current -> CheckMark(color = MaterialTheme.colorScheme.primary, size = 24.dp)
                        index == current -> CircularProgressIndicator(modifier = Modifier.size(22.dp), strokeWidth = 3.dp)
                        else -> Box(modifier = Modifier.size(10.dp).border(BorderStroke(2.dp, MaterialTheme.colorScheme.outline), RoundedCornerShape(50)))
                    }
                }
                Text(
                    stringResource(label), style = MaterialTheme.typography.titleMedium,
                    color = if (index > current) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                )
            }
        }
    }
}

@Composable
private fun FailurePanel(content: WifiPresentation.PairFailureContent, onRetry: () -> Unit, onClose: () -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics { liveRegion = LiveRegionMode.Assertive }) {
        WarningMark(color = MaterialTheme.colorScheme.error, size = 32.dp)
        Text(stringResource(content.title), style = MaterialTheme.typography.headlineSmall, color = MaterialTheme.colorScheme.error)
    }
    Text(stringResource(content.body), style = MaterialTheme.typography.bodyLarge)
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        if (content.canRetry) Button(onClick = onRetry, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_retry)) }
        OutlinedButton(onClick = onClose, modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_close)) }
    }
}

@Composable
private fun CodeEntry(invalid: Boolean, onCode: (String) -> Unit) {
    // remember, not rememberSaveable: the code is a one-use secret and must not be written into saved instance state.
    var clean by remember { mutableStateOf("") }
    val badCharacters = WifiCodeInput.invalidCharacters(clean)
    val shown = WifiCodeInput.format(clean)
    Text(stringResource(R.string.wifi_pair_code_instruction), style = MaterialTheme.typography.bodyLarge)
    OutlinedTextField(
        value = TextFieldValue(shown, selection = TextRange(shown.length)),
        onValueChange = { clean = WifiCodeInput.clean(it.text) },
        label = { Text(stringResource(R.string.wifi_pair_code_hint)) },
        isError = badCharacters.isNotEmpty(),
        supportingText = {
            Row(modifier = Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(if (badCharacters.isEmpty()) "" else stringResource(R.string.wifi_pair_code_invalid_chars, badCharacters.joinToString(" ")), modifier = Modifier.weight(1f))
                Text(stringResource(R.string.wifi_pair_code_counter, clean.length, WifiCodeInput.LENGTH))
            }
        },
        textStyle = MaterialTheme.typography.titleMedium.copy(fontFamily = FontFamily.Monospace),
        singleLine = false,
        minLines = 2,
        keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Characters, autoCorrectEnabled = false, keyboardType = KeyboardType.Ascii),
        modifier = Modifier.fillMaxWidth(),
    )
    if (invalid) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            WarningMark(color = MaterialTheme.colorScheme.error, size = 24.dp)
            Text(stringResource(R.string.wifi_pair_invalid), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
        }
    }
    Button(onClick = { onCode(clean) }, enabled = WifiCodeInput.isComplete(clean), modifier = Modifier.heightIn(min = 56.dp)) { Text(stringResource(R.string.wifi_pair_code_submit)) }
}