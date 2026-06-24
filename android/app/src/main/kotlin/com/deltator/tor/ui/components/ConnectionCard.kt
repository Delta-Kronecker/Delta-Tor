package com.deltator.tor.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.core.TorState
import com.deltator.tor.ui.theme.*

@Composable
fun SlotCard(
    label: String,
    source: String,
    category: String?,
    transport: String?,
    ip: String?,
    noBridge: Boolean,
    socksPort: Int,
    httpPort: Int,
    state: TorState,
    bootstrapPercent: Int,
    statusText: String,
    healthText: String,
    healthOk: Boolean,
    enabled: Boolean,
    onToggle: () -> Unit,
    onSetProxy: () -> Unit,
    onRetry: () -> Unit,
    onHealth: () -> Unit,
    onLog: () -> Unit,
    onDelete: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .padding(4.dp),
        colors = CardDefaults.cardColors(containerColor = PANEL),
        shape = RoundedCornerShape(4.dp)
    ) {
        Row(Modifier.fillMaxWidth()) {
            Box(
                modifier = Modifier
                    .width(4.dp)
                    .height(IntrinsicSize.Max)
                    .fillMaxHeight()
                    .background(ACC)
            )

            Column(
                modifier = Modifier
                    .weight(1f)
                    .padding(10.dp)
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Checkbox(
                        checked = enabled,
                        onCheckedChange = { onToggle() },
                        colors = CheckboxDefaults.colors(checkedColor = GRN)
                    )

                    Text(
                        text = label,
                        color = FG,
                        fontSize = 14.sp,
                        modifier = Modifier
                            .weight(1f)
                            .padding(start = 4.dp)
                    )

                    Text(
                        text = when (state) {
                            TorState.CONNECTED -> "Connected"
                            TorState.CONNECTING -> "Connecting..."
                            TorState.FAILED -> "Failed"
                            TorState.STOPPED -> "Stopped"
                            else -> "Idle"
                        },
                        color = when (state) {
                            TorState.CONNECTED -> GRN
                            TorState.FAILED -> RED
                            else -> FG2
                        },
                        fontSize = 12.sp
                    )
                }

                Text(
                    text = "$source  \u00b7  ${category ?: "\u2014"}  \u00b7  ${transport ?: "auto"}  \u00b7  ${ip ?: "auto"}",
                    color = FG2,
                    fontSize = 10.sp
                )

                Text(
                    text = "SOCKS $socksPort  \u00b7  HTTP $httpPort",
                    color = CYAN,
                    fontSize = 11.sp,
                    fontFamily = FontFamily.Monospace
                )

                Spacer(modifier = Modifier.height(6.dp))

                LinearProgressIndicator(
                    progress = { bootstrapPercent / 100f },
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(6.dp),
                    color = if (state == TorState.CONNECTED) GRN else ACC,
                    trackColor = BTN
                )

                Text(
                    text = "$bootstrapPercent%",
                    color = FG2,
                    fontSize = 12.sp,
                    modifier = Modifier.align(Alignment.End)
                )

                Text(
                    text = healthText,
                    color = if (healthOk) GRN else RED,
                    fontSize = 12.sp
                )

                Spacer(modifier = Modifier.height(6.dp))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(4.dp)
                ) {
                    CompactButton("Set Proxy", onSetProxy)
                    CompactButton("Retry", onRetry)
                    CompactButton("Health", onHealth)
                    CompactButton("Log", onLog)
                    CompactButton("\u2716", onDelete, RED)
                }
            }
        }
    }
}

@Composable
fun CompactButton(text: String, onClick: () -> Unit, color: androidx.compose.ui.graphics.Color = BTN2) {
    Text(
        text = text,
        color = FG,
        fontSize = 11.sp,
        modifier = Modifier
            .clip(RoundedCornerShape(2.dp))
            .background(color)
            .clickable { onClick() }
            .padding(horizontal = 8.dp, vertical = 4.dp)
    )
}
