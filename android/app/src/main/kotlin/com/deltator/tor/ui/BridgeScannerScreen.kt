package com.deltator.tor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.graphics.Color
import com.deltator.tor.ui.theme.*
import com.deltator.tor.viewmodel.BridgeScannerViewModel

@Composable
fun BridgeScannerScreen(
    viewModel: BridgeScannerViewModel,
    onBack: () -> Unit
) {
    val isScanning by viewModel.isScanning.collectAsState()
    val progress by viewModel.progress.collectAsState()
    val progressText by viewModel.progressText.collectAsState()
    val results by viewModel.results.collectAsState()
    val summary by viewModel.summary.collectAsState()

    var category by remember { mutableStateOf("Tested & Active") }
    var transport by remember { mutableStateOf("obfs4") }
    var ip by remember { mutableStateOf("IPv4") }
    var workers by remember { mutableStateOf("20") }
    var timeout by remember { mutableStateOf("5") }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BG)
    ) {
        // Header
        Box(Modifier.fillMaxWidth().background(ACC).height(3.dp))
        Row(
            Modifier.fillMaxWidth().background(PANEL).padding(14.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("\u25C0 Back", color = ACC, fontSize = 14.sp,
                modifier = Modifier.clickable(onClick = onBack).padding(end = 16.dp))
            Text("Bridge Scanner", color = ACC, fontSize = 18.sp)
        }

        // Controls
        Card(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column(Modifier.padding(12.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    DropdownCompact("Category:", category,
                        listOf("Tested & Active", "Fresh (72h)", "Full Archive")) { category = it }
                    DropdownCompact("Transport:", transport,
                        listOf("obfs4", "webtunnel", "vanilla")) { transport = it }
                }

                Spacer(Modifier.height(8.dp))

                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    DropdownCompact("IP:", ip, listOf("IPv4", "IPv6")) { ip = it }

                    OutlinedTextField(
                        value = workers,
                        onValueChange = { workers = it.filter { c -> c.isDigit() } },
                        label = { Text("Workers") },
                        modifier = Modifier.weight(1f),
                        singleLine = true
                    )

                    OutlinedTextField(
                        value = timeout,
                        onValueChange = { timeout = it.filter { c -> c.isDigit() } },
                        label = { Text("Timeout(s)") },
                        modifier = Modifier.weight(1f),
                        singleLine = true
                    )
                }
            }
        }

        // Progress
        Text(progressText, color = FG2, fontSize = 11.sp, modifier = Modifier.padding(horizontal = 12.dp))
        LinearProgressIndicator(
            progress = { progress / 100f },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp).height(6.dp),
            color = ACC,
            trackColor = BTN
        )

        Spacer(Modifier.height(8.dp))

        // Results
        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 12.dp)
        ) {
            results.forEach { result ->
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 2.dp)
                        .background(
                            if (result.reachable) Color(0xFF0E2A1A) else Color(0xFF2A0E0E),
                            MaterialTheme.shapes.small
                        )
                        .padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = result.transport,
                        color = FG2,
                        fontSize = 11.sp,
                        modifier = Modifier.width(60.dp)
                    )
                    Text(
                        text = result.host,
                        color = FG,
                        fontSize = 11.sp,
                        modifier = Modifier.weight(1f)
                    )
                    Text(
                        text = "${result.port}",
                        color = FG2,
                        fontSize = 11.sp,
                        modifier = Modifier.width(50.dp)
                    )
                    Text(
                        text = result.latency?.let { "$it ms" } ?: "—",
                        color = FG2,
                        fontSize = 11.sp,
                        modifier = Modifier.width(60.dp)
                    )
                    Text(
                        text = if (result.reachable) "\u2714" else "\u2718",
                        color = if (result.reachable) GRN else RED,
                        fontSize = 14.sp
                    )
                }
            }
        }

        // Summary
        Text(summary, color = GRN, fontSize = 12.sp, modifier = Modifier.padding(12.dp))

        // Action buttons
        Row(
            Modifier.fillMaxWidth().padding(12.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            Button(
                onClick = {
                    viewModel.startScan(category, transport, ip,
                        workers.toIntOrNull() ?: 20,
                        timeout.toIntOrNull() ?: 5)
                },
                enabled = !isScanning,
                modifier = Modifier.weight(1f),
                colors = ButtonDefaults.buttonColors(containerColor = ACC)
            ) { Text("Start Scan") }

            Button(
                onClick = { viewModel.stopScan() },
                enabled = isScanning,
                modifier = Modifier.weight(1f),
                colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = RED)
            ) { Text("Stop") }
        }
    }
}

@Composable
private fun DropdownCompact(label: String, value: String, options: List<String>, onSelected: (String) -> Unit) {
    var expanded by remember { mutableStateOf(false) }
    Column(Modifier.weight(1f)) {
        Text(label, color = FG2, fontSize = 11.sp)
        OutlinedButton(onClick = { expanded = true }, modifier = Modifier.fillMaxWidth()) {
            Text(value, color = FG, fontSize = 12.sp)
        }
        DropdownMenu(expanded, onDismissRequest = { expanded = false }) {
            options.forEach { o ->
                DropdownMenuItem(text = { Text(o) }, onClick = { onSelected(o); expanded = false })
            }
        }
    }
}


