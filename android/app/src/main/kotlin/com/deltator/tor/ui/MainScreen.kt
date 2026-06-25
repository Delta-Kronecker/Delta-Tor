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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.ui.components.*
import com.deltator.tor.ui.theme.*
import com.deltator.tor.viewmodel.MainViewModel

@Composable
fun MainScreen(
    viewModel: MainViewModel,
    onNavigateToMulti: () -> Unit,
    onNavigateToScanner: () -> Unit,
    onNavigateToSettings: () -> Unit
) {
    val selectedSource by viewModel.selectedSource.collectAsState()
    val selectedCategory by viewModel.selectedCategory.collectAsState()
    val selectedTransport by viewModel.selectedTransport.collectAsState()
    val selectedIp by viewModel.selectedIp.collectAsState()
    val noBridge by viewModel.noBridge.collectAsState()
    val connectionProgress by viewModel.connectionProgress.collectAsState()
    val statusText by viewModel.statusText.collectAsState()
    val isConnected by viewModel.isConnected.collectAsState()
    val isProxyEnabled by viewModel.isProxyEnabled.collectAsState()
    val exitIp by viewModel.exitIp.collectAsState()
    val country by viewModel.country.collectAsState()
    val uptime by viewModel.uptime.collectAsState()
    val torStatus by viewModel.torStatus.collectAsState()
    val logs by viewModel.logs.collectAsState()
    val bridgeCount by viewModel.bridgeCount.collectAsState()
    val bridgeUpdated by viewModel.bridgeUpdated.collectAsState()

    val transports = viewModel.getAvailableTransports()
    val categoryOptions = viewModel.categoryOptions
    val ipOptions = viewModel.ipOptions

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BG)
    ) {
        Box(Modifier.fillMaxWidth().background(ACC).height(3.dp))

        // Nav bar
        Row(
            modifier = Modifier.fillMaxWidth().background(PANEL).padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("Delta Tor", color = ACC, fontSize = 18.sp, modifier = Modifier.weight(1f))
            NavButton("Settings") { onNavigateToSettings() }
        }

        StatusBar(statusText, isConnected,
            if (isProxyEnabled) "proxy: active | HTTP: 19052 | SOCKS: 9050" else ""
        )

        // Scrollable content (everything except logs)
        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
        ) {
            // Bridge Config
            Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = PANEL)) {
                Column(Modifier.padding(14.dp)) {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text("Bridge Configuration", color = FG, fontSize = 14.sp, modifier = Modifier.weight(1f))
                        TextButton(onClick = { viewModel.updateBridges() }) { Text("Update Bridges", color = CYAN) }
                    }
                    Spacer(Modifier.height(4.dp))
                    DropdownRow("Source:", selectedSource, viewModel.sourceOptions) { viewModel.setSelectedSource(it) }
                    if (selectedSource != "Default (Built-in)") {
                        DropdownRow("Category:", selectedCategory, categoryOptions) { viewModel.setSelectedCategory(it) }
                    }
                    DropdownRow("Transport:", selectedTransport, transports) { viewModel.setSelectedTransport(it) }
                    if (selectedSource != "Default (Built-in)") {
                        DropdownRow("IP Version:", selectedIp, ipOptions) { viewModel.setSelectedIp(it) }
                    }
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = noBridge, onCheckedChange = { viewModel.setNoBridge(it) }, colors = CheckboxDefaults.colors(checkedColor = ACC))
                        Text("Connect without bridge", color = FG, fontSize = 12.sp)
                    }
                    Row {
                        Text("Available: ", color = FG2, fontSize = 11.sp)
                        Text(bridgeCount, color = FG, fontSize = 11.sp)
                        Spacer(Modifier.width(16.dp))
                        Text("Updated: ", color = FG2, fontSize = 11.sp)
                        Text(bridgeUpdated, color = FG2, fontSize = 11.sp)
                    }
                }
            }

            Spacer(Modifier.height(6.dp))

            // Buttons
            Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = PANEL)) {
                Column(Modifier.padding(14.dp)) {
                    Button(onClick = onNavigateToMulti, modifier = Modifier.fillMaxWidth().height(48.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = MULTI_BG, contentColor = MULTI_FG)) {
                        Text("Multi-Connect \u2014 Recommended", fontSize = 14.sp)
                    }
                    Spacer(Modifier.height(6.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        Button(onClick = { viewModel.startAutoConnect() }, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)) { Text("Auto") }
                        Button(onClick = { viewModel.startConnect() }, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)) { Text("Start") }
                        Button(onClick = { viewModel.stopConnect() }, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = RED)) { Text("Stop") }
                    }
                    Spacer(Modifier.height(4.dp))
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                        Button(onClick = onNavigateToScanner, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)) { Text("Scanner") }
                        Button(onClick = { viewModel.testConnection() }, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)) { Text("Test") }
                        Button(onClick = { viewModel.requestNewCircuit() }, modifier = Modifier.weight(1f).height(44.dp),
                            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)) { Text("New Circuit") }
                    }
                    Spacer(Modifier.height(6.dp))
                    Button(onClick = { viewModel.toggleProxy() }, modifier = Modifier.fillMaxWidth().height(44.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = if (isProxyEnabled) Color(0xFF0E2A1A) else BTN,
                            contentColor = if (isProxyEnabled) GRN else FG2
                        )) {
                        Text("System Proxy: ${if (isProxyEnabled) "ON" else "OFF"}")
                    }
                }
            }

            Spacer(Modifier.height(6.dp))

            // Progress + Stats
            ProgressCard(connectionProgress)
            Spacer(Modifier.height(6.dp))

            Card(modifier = Modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = PANEL)) {
                Column {
                    Box(Modifier.fillMaxWidth().background(ACC).height(2.dp))
                    Column(Modifier.padding(10.dp)) {
                        Row(Modifier.fillMaxWidth()) {
                            StatsRowInline("Exit IP:", exitIp, Modifier.weight(1f))
                            StatsRowInline("Country:", country, Modifier.weight(1f))
                        }
                        Row(Modifier.fillMaxWidth()) {
                            StatsRowInline("Uptime:", uptime, Modifier.weight(1f))
                            StatsRowInline("Status:", torStatus, Modifier.weight(1f))
                        }
                    }
                }
            }
        }

        // Log viewer - fixed at bottom
        Card(
            modifier = Modifier
                .fillMaxWidth()
                .height(200.dp),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column(Modifier.fillMaxSize()) {
                Box(Modifier.fillMaxWidth().background(ACC).height(2.dp))
                Row(
                    Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("Logs (${logs.size})", color = FG, fontSize = 11.sp, modifier = Modifier.weight(1f))
                    TextButton(onClick = { onNavigateToMulti() }) {
                        Text("Full View", color = CYAN, fontSize = 10.sp)
                    }
                    TextButton(onClick = {
                        val allLogs = logs.joinToString("\n")
                        val ctx = viewModel.getApplication<android.app.Application>()
                        val clip = android.content.ClipData.newPlainText("logs", allLogs)
                        val cm = ctx.getSystemService(android.content.Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
                        cm.setPrimaryClip(clip)
                    }) {
                        Text("Copy", color = CYAN, fontSize = 10.sp)
                    }
                }
                Column(
                    modifier = Modifier
                        .fillMaxSize()
                        .background(PANEL)
                        .padding(start = 8.dp, end = 8.dp, bottom = 4.dp)
                        .verticalScroll(rememberScrollState())
                ) {
                    if (logs.isEmpty()) {
                        Text("No logs yet...", color = FG2, fontSize = 10.sp)
                    }
                    logs.takeLast(50).forEach { line ->
                        Text(
                            text = line,
                            color = when {
                                "[err]" in line.lowercase() || "error" in line.lowercase() -> RED
                                "[warn]" in line.lowercase() || "warn " in line.lowercase() -> YLW
                                "[notice]" in line.lowercase() || "bootstrapped" in line.lowercase() -> GRN
                                "[auto]" in line.lowercase() -> CYAN
                                "[debug]" in line.lowercase() || "[extract]" in line.lowercase() -> PRP
                                "[bundle]" in line.lowercase() -> CYAN
                                else -> FG2
                            },
                            fontSize = 10.sp
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun StatsRowInline(label: String, value: String, modifier: Modifier = Modifier) {
    Row(modifier.padding(horizontal = 8.dp, vertical = 4.dp)) {
        Text(label, color = FG2, fontSize = 12.sp)
        Spacer(Modifier.width(4.dp))
        Text(value, color = GRN, fontSize = 12.sp)
    }
}

@Composable
fun NavButton(text: String, onClick: () -> Unit) {
    Text(
        text = text,
        color = FG2,
        fontSize = 12.sp,
        modifier = Modifier.clickable { onClick() }.padding(horizontal = 10.dp, vertical = 6.dp)
    )
}

@Composable
fun DropdownRow(label: String, value: String, options: List<String>, onSelected: (String) -> Unit) {
    Row(modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, color = FG2, fontSize = 13.sp, modifier = Modifier.width(90.dp))
        var expanded by remember { mutableStateOf(false) }
        Box(Modifier.weight(1f)) {
            OutlinedButton(
                onClick = { expanded = true },
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.outlinedButtonColors(containerColor = BTN, contentColor = FG),
                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
            ) { Text(value, fontSize = 13.sp, modifier = Modifier.fillMaxWidth()) }
            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                options.forEach { option ->
                    DropdownMenuItem(text = { Text(option) }, onClick = { onSelected(option); expanded = false })
                }
            }
        }
    }
}
