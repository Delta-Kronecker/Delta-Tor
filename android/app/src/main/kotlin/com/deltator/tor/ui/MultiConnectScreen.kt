package com.deltator.tor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.core.SlotDef
import com.deltator.tor.ui.components.SlotCard
import com.deltator.tor.ui.theme.*
import com.deltator.tor.viewmodel.MultiConnectViewModel

@Composable
fun MultiConnectScreen(
    viewModel: MultiConnectViewModel,
    onBack: () -> Unit
) {
    val slots by viewModel.slots.collectAsState()
    val isRunning by viewModel.isRunning.collectAsState()
    val autoProxy by viewModel.autoProxyEnabled.collectAsState()
    var showAddDialog by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BG)
    ) {
        // Toolbar
        Card(
            modifier = Modifier.fillMaxWidth(),
            colors = CardDefaults.cardColors(containerColor = CARD)
        ) {
            Column {
                Box(Modifier.fillMaxWidth().background(ACC).height(2.dp))
                Row(
                    Modifier.fillMaxWidth().padding(8.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Button(
                        onClick = onBack,
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = FG2)
                    ) { Text("\u25C0 Back") }

                    Spacer(Modifier.width(4.dp))

                    Button(
                        onClick = { viewModel.startAll() },
                        colors = ButtonDefaults.buttonColors(containerColor = GRN)
                    ) { Text("\u25B6 Start") }

                    Spacer(Modifier.width(4.dp))

                    Button(
                        onClick = { viewModel.stopAll() },
                        colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = RED)
                    ) { Text("\u23F9 Stop") }

                    Spacer(Modifier.weight(1f))

                    Button(
                        onClick = { viewModel.toggleAutoProxy() },
                        colors = ButtonDefaults.buttonColors(
                            containerColor = if (autoProxy) ACC else BTN2,
                            contentColor = FG
                        )
                    ) {
                        Text("Auto Proxy: ${if (autoProxy) "ON" else "OFF"}")
                    }
                }
            }
        }

        // Slots grid
        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(6.dp)
        ) {
            val chunked = slots.chunked(2)
            chunked.forEach { row ->
                Row(Modifier.fillMaxWidth()) {
                    row.forEach { slot ->
                        val ports = com.deltator.tor.core.Config.slotPorts(slots.indexOf(slot))
                        SlotCard(
                            label = slot.label,
                            source = slot.def.source,
                            category = slot.def.category,
                            transport = slot.def.transport,
                            ip = slot.def.ip,
                            noBridge = slot.def.noBridge,
                            socksPort = ports.first,
                            httpPort = ports.third,
                            state = slot.state,
                            bootstrapPercent = slot.bootstrapPercent,
                            statusText = slot.statusText,
                            healthText = slot.healthText,
                            healthOk = slot.healthOk,
                            enabled = slot.enabled,
                            onToggle = { viewModel.toggleSlot(slot.label) },
                            onSetProxy = { viewModel.setProxyToSlot(slot.label) },
                            onRetry = { viewModel.retrySlot(slot.label) },
                            onHealth = {},
                            onLog = {},
                            onDelete = { viewModel.deleteSlot(slot.label) }
                        )
                    }
                    // Fill empty space
                    if (row.size < 2) {
                        Spacer(Modifier.weight(1f))
                    }
                }
            }
        }

        // Add button
        Button(
            onClick = { showAddDialog = true },
            modifier = Modifier.fillMaxWidth().padding(12.dp).height(48.dp),
            colors = ButtonDefaults.buttonColors(containerColor = BTN, contentColor = CYAN)
        ) {
            Text("+ Add Connection Mode", fontSize = 14.sp)
        }
    }

    if (showAddDialog) {
        AddSlotDialog(
            onDismiss = { showAddDialog = false },
            onAdd = { def ->
                viewModel.addSlot(def)
                showAddDialog = false
            }
        )
    }
}

@Composable
fun AddSlotDialog(
    onDismiss: () -> Unit,
    onAdd: (SlotDef) -> Unit
) {
    var name by remember { mutableStateOf("New Mode") }
    var source by remember { mutableStateOf("Delta-Kronecker") }
    var category by remember { mutableStateOf("Tested & Active") }
    var transport by remember { mutableStateOf("obfs4") }
    var ip by remember { mutableStateOf("IPv4") }

    val sources = listOf("Default (Built-in)", "Delta-Kronecker", "Direct (No Bridge)")
    val categories = listOf("Tested & Active", "Fresh (72h)", "Full Archive")
    val transports = listOf("obfs4", "webtunnel", "vanilla")
    val ips = listOf("IPv4", "IPv6", "Both")

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = BG,
        title = { Text("Add Connection Mode", color = ACC) },
        text = {
            Column {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("Name") },
                    modifier = Modifier.fillMaxWidth()
                )

                Spacer(Modifier.height(8.dp))

                // Source dropdown
                var srcExpanded by remember { mutableStateOf(false) }
                Text("Source:", color = FG2, fontSize = 12.sp)
                OutlinedButton(onClick = { srcExpanded = true }, modifier = Modifier.fillMaxWidth()) {
                    Text(source, color = FG, fontSize = 12.sp)
                }
                DropdownMenu(srcExpanded, onDismissRequest = { srcExpanded = false }) {
                    sources.forEach { s ->
                        DropdownMenuItem(text = { Text(s) }, onClick = { source = s; srcExpanded = false })
                    }
                }

                if (source == "Delta-Kronecker") {
                    Spacer(Modifier.height(8.dp))
                    var catExpanded by remember { mutableStateOf(false) }
                    Text("Category:", color = FG2, fontSize = 12.sp)
                    OutlinedButton(onClick = { catExpanded = true }, modifier = Modifier.fillMaxWidth()) {
                        Text(category, color = FG, fontSize = 12.sp)
                    }
                    DropdownMenu(catExpanded, onDismissRequest = { catExpanded = false }) {
                        categories.forEach { c ->
                            DropdownMenuItem(text = { Text(c) }, onClick = { category = c; catExpanded = false })
                        }
                    }

                    Spacer(Modifier.height(8.dp))
                    var trExpanded by remember { mutableStateOf(false) }
                    Text("Transport:", color = FG2, fontSize = 12.sp)
                    OutlinedButton(onClick = { trExpanded = true }, modifier = Modifier.fillMaxWidth()) {
                        Text(transport, color = FG, fontSize = 12.sp)
                    }
                    DropdownMenu(trExpanded, onDismissRequest = { trExpanded = false }) {
                        transports.forEach { t ->
                            DropdownMenuItem(text = { Text(t) }, onClick = { transport = t; trExpanded = false })
                        }
                    }

                    Spacer(Modifier.height(8.dp))
                    var ipExpanded by remember { mutableStateOf(false) }
                    Text("IP Version:", color = FG2, fontSize = 12.sp)
                    OutlinedButton(onClick = { ipExpanded = true }, modifier = Modifier.fillMaxWidth()) {
                        Text(ip, color = FG, fontSize = 12.sp)
                    }
                    DropdownMenu(ipExpanded, onDismissRequest = { ipExpanded = false }) {
                        ips.forEach { i ->
                            DropdownMenuItem(text = { Text(i) }, onClick = { ip = i; ipExpanded = false })
                        }
                    }
                }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    val def = if (source == "Direct (No Bridge)") {
                        SlotDef(name, source, null, null, null, noBridge = true)
                    } else if (source == "Default (Built-in)") {
                        SlotDef(name, source, null, "snowflake", null, false)
                    } else {
                        SlotDef(name, source, category, transport, ip, false)
                    }
                    onAdd(def)
                },
                colors = ButtonDefaults.buttonColors(containerColor = ACC)
            ) { Text("Add") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("Cancel", color = FG2) }
        }
    )
}
