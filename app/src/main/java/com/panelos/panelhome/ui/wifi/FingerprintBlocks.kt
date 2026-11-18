package com.panelos.panelhome.ui.wifi

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The 256-bit fingerprint as eight blocks of eight, in two rows of four, uppercase and monospace, with the blocks
 * alternating colour. The line break is fixed (never "wherever the width ends") so it is the same on the PC and the tablet.
 * Never shortened and not copyable: the comparison is visual and only counts when it covers every block.
 */
@Composable
internal fun FingerprintBlocks(hex: String, modifier: Modifier = Modifier, fontSize: TextUnit = 22.sp) {
    val rows = WifiPresentation.fingerprintRows(hex)
    Column(
        modifier = modifier.semantics { contentDescription = rows.flatten().joinToString(", ") { block -> block.toList().joinToString(" ") } },
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        rows.forEachIndexed { rowIndex, row ->
            Row(horizontalArrangement = Arrangement.spacedBy(20.dp)) {
                row.forEachIndexed { column, block ->
                    val index = rowIndex * 4 + column
                    Text(
                        text = block,
                        fontFamily = FontFamily.Monospace,
                        fontWeight = FontWeight.SemiBold,
                        fontSize = fontSize,
                        color = if (index % 2 == 0) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.secondary,
                    )
                }
            }
        }
    }
}