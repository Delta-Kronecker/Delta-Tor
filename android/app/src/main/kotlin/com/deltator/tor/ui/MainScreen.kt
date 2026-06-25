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
            .verticalScroll(rememberScrollState())
    ) {
        // Header
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .background(ACC)
                .height(3.dp)
        )

        // Navigation bar
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(PANEL)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = "Delta Tor",
                color = ACC,
                fontSize = 18.sp,
                modifier = Modifier.weight(1f)
            )
            NavButton("Settings") { onNavigateToSettings() }
        }

        StatusBar(statusText, isConnected,
            if (isProxyEnabled) "proxy: active | HTTP: 19052 | SOCKS: 9050" else ""
        )

        // Bridge Configuration Card
        Card(
            modifier = Modifier.fillMaxWidth().padding(0.dp),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column(Modifier.padding(14.dp)) {
                Row(
                    Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("Bridge Configuration", color = FG, fontSize = 14.sp,
                        modifier = Modifier.weight(1f))
                    TextButton(onClick = { viewModel.updateBridges() }) {
                        Text("Update Bridges", color = CYAN)
                    }
                }

                Spacer(Modifier.height(4.dp))

                // Source
                DropdownRow("Source:", selectedSource, viewModel.sourceOptions) {
                    viewModel.setSelectedSource(it)
                }

                // Category (hidden for built-in)
                if (selectedSource != "Default (Built-in)") {
                    DropdownRow("Category:", selectedCategory, categoryOptions) {
                        viewModel.setSelectedCategory(it)
                    }
                }

                // Transport
                DropdownRow("Transport:", selectedTransport, transports) {
                    viewModel.setSelectedTransport(it)
                }

                // IP Version
                if (selectedSource != "Default (Built-in)") {
                    DropdownRow("IP Version:", selectedIp, ipOptions) {
                        viewModel.setSelectedIp(it)
                }
                }

                // No bridge toggle
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = noBridge,
                        onCheckedChange = { viewModel.setNoBridge(it) },
                        colors = CheckboxDefaults.colors(checkedColor = ACC)
                    )
                    Text("Connect without bridge (direct Tor)", color = FG, fontSize = 12.sp)
                }

                // Bridge info
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

        // Action buttons
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column(Modifier.padding(14.dp)) {
                // Multi-Connect button
                Button(
                    onClick = onNavigateToMulti,
                    modifier = Modifier.fillMaxWidth().height(48.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = MULTI_BG, contentColor = MULTI_FG)
                ) {
                    Text("Multi-Connect \u2014 Recommended", fontSize = 14.sp)
                }

                Spacer(Modifier.height(6.dp))

                // Main buttons row
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    Button(
                        onClick = { viewModel.startAutoConnect() },
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)
                    ) { Text("Auto") }

                    Button(
                        onClick = { viewModel.startConnect() },
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)
                    ) { Text("Start") }

                    Button(
                        onClick = { viewModel.stopConnect() },
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = RED)
                    ) { Text("Stop") }
                }

                Spacer(Modifier.height(4.dp))

                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    Button(
                        onClick = onNavigateToScanner,
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)
                    ) { Text("Bridge Scanner") }

                    Button(
                        onClick = { viewModel.testConnection() },
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)
                    ) { Text("Test Connection") }

                    Button(
                        onClick = { viewModel.requestNewCircuit() },
                        modifier = Modifier.weight(1f).height(44.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG)
                    ) { Text("New Circuit") }
                }

                Spacer(Modifier.height(6.dp))

                // Proxy toggle
                Button(
                    onClick = { viewModel.toggleProxy() },
                    modifier = Modifier.fillMaxWidth().height(44.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = if (isProxyEnabled) Color(0xFF0E2A1A) else BTN,
                        contentColor = if (isProxyEnabled) GRN else FG2
                    )
                ) {
                    Text("System Proxy: ${if (isProxyEnabled) "ON" else "OFF"}")
                }
            }
        }

        Spacer(Modifier.height(6.dp))

        // Progress
        ProgressCard(connectionProgress)

        Spacer(Modifier.height(6.dp))

        // Stats
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column {
                Box(Modifier.fillMaxWidth().background(ACC).height(2.dp))
                Column(Modifier.padding(10.dp)) {
                    Row(Modifier.fillMaxWidth()) {
                        StatsRow("Exit IP:", exitIp, Modifier.weight(1f))
                        StatsRow("Country:", country, Modifier.weight(1f))
                    }
                    Row(Modifier.fillMaxWidth()) {
                        StatsRow("Uptime:", uptime, Modifier.weight(1f))
                        StatsRow("Status:", torStatus, Modifier.weight(1f))
                    }
                }
            }
        }

        Spacer(Modifier.height(6.dp))

        // Log viewer
        Card(
            modifier = Modifier.fillMaxWidth().heightIn(min = 200.dp),
            colors = CardDefaults.cardColors(containerColor = PANEL)
        ) {
            Column {
                Box(Modifier.fillMaxWidth().background(ACC).height(2.dp))
                Text("Tor Logs", color = FG, fontSize = 12.sp,
                    modifier = Modifier.padding(10.dp, 6.dp))

                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .background(PANEL)
                        .padding(10.dp)
                ) {
                    logs.forEach { line ->
                        Text(
                            text = line,
                            color = when {
                                "[err]" in line.lowercase() || "error" in line.lowercase() -> RED
                                "[warn]" in line.lowercase() || "warn " in line.lowercase() -> YLW
                                "[notice]" in line.lowercase() || "bootstrapped" in line.lowercase() -> GRN
                                "[auto]" in line.lowercase() -> CYAN
                                else -> FG2
                            },
                            fontSize = 10.sp
                        )
                    }
                }
            }
        }

        Spacer(Modifier.height(16.dp))
    }
}

@Composable
fun NavButton(text: String, onClick: () -> Unit) {
    Text(
        text = text,
        color = FG2,
        fontSize = 12.sp,
        modifier = Modifier
            .clickable { onClick() }
            .padding(horizontal = 10.dp, vertical = 6.dp)
    )
}

@Composable
fun DropdownRow(label: String, value: String, options: List<String>, onSelected: (String) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, color = FG2, fontSize = 13.sp, modifier = Modifier.width(90.dp))

        var expanded by remember { mutableStateOf(false) }

        Box(Modifier.weight(1f)) {
            OutlinedButton(
                onClick = { expanded = true },
                modifier = Modifier.fillMaxWidth(),
                colors = ButtonDefaults.outlinedButtonColors(containerColor = BTN, contentColor = FG),
                contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp)
            ) {
                Text(value, fontSize = 13.sp, modifier = Modifier.fillMaxWidth())
            }

            DropdownMenu(expanded = expanded, onDismissRequest = { expanded = false }) {
                options.forEach { option ->
                    DropdownMenuItem(
                        text = { Text(option) },
                        onClick = { onSelected(option); expanded = false }
                    )
                }
            }
        }
    }
}

@Composable
private fun StatsRow(label: String, value: String, modifier: Modifier = Modifier) {
    Row(modifier.padding(horizontal = 8.dp, vertical = 4.dp)) {
        Text(label, color = FG2, fontSize = 12.sp)
        Spacer(Modifier.width(4.dp))
        Text(value, color = GRN, fontSize = 12.sp)
    }
}


