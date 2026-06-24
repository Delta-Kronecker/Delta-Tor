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
import com.deltator.tor.ui.theme.*
import com.deltator.tor.viewmodel.SettingsViewModel

@Composable
fun SettingsScreen(
    viewModel: SettingsViewModel,
    onBack: () -> Unit
) {
    val settings = viewModel.settings

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
            Text("Settings", color = ACC, fontSize = 18.sp)
        }

        Column(
            modifier = Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(14.dp)
        ) {
            // Auto-Connect
            SectionHeader("Auto-Connect")
            IntSetting("Timeout per config (sec)", "auto_connect_timeout", settings)
            BoolSetting("Auto-enable proxy on connect", "auto_proxy_on_connect", settings)

            // Bridges
            SectionHeader("Bridges")
            IntSetting("Bridges written to torrc", "bridges_in_torrc", settings)
            BoolSetting("Shuffle bridge order", "shuffle_bridges", settings)

            // SNI
            SectionHeader("SNI Settings")
            BoolSetting("Enable SNI override", "sni_enabled", settings)
            StringSetting("SNI hostname", "sni_host", settings)

            // Privacy / DNS
            SectionHeader("Privacy / DNS")
            BoolSetting("DNS over Tor (DNSPort 9053)", "dns_over_tor", settings)

            // Circuit Building
            SectionHeader("Circuit Building")
            IntSetting("MaxCircuitDirtiness (sec)", "max_circuit_dirtiness", settings)
            IntSetting("NewCircuitPeriod (sec)", "new_circuit_period", settings)
            IntSetting("NumEntryGuards", "num_entry_guards", settings)

            // Keep-Alive
            SectionHeader("Keep-Alive")
            BoolSetting("Keep-Alive enabled", "keep_alive_enabled", settings)
            IntSetting("Keep-Alive interval (sec)", "keep_alive_interval", settings)

            // Watchdog
            SectionHeader("Watchdog")
            BoolSetting("Watchdog enabled", "watchdog_enabled", settings)
            IntSetting("Check interval (sec)", "watchdog_interval", settings)

            // Exit Nodes
            SectionHeader("Exit Nodes")
            BoolSetting("Enable Exit Nodes filter", "exit_nodes_enabled", settings)
            StringSetting("Countries (torrc format)", "exit_nodes_countries", settings)
            BoolSetting("StrictNodes", "strict_exit_nodes", settings)

            // Experimental
            SectionHeader("Experimental (Advanced torrc)", PRP)
            Text("All OFF by default. Wrong settings can break connectivity.",
                color = YLW, fontSize = 10.sp, modifier = Modifier.padding(bottom = 8.dp))

            BoolSetting("ConnectionPadding", "exp_connection_padding", settings)
            BoolSetting("ReducedConnectionPadding", "exp_reduced_connection_padding", settings)
            IntSetting("CircuitStreamTimeout (sec)", "exp_circuit_stream_timeout", settings)
            IntSetting("SocksTimeout (sec)", "exp_socks_timeout", settings)
            BoolSetting("IsolateDestAddr", "exp_isolate_dest_addr", settings)
            BoolSetting("IsolateDestPort", "exp_isolate_dest_port", settings)
            BoolSetting("SafeLogging", "exp_safe_logging", settings)
            BoolSetting("AvoidDiskWrites", "exp_avoid_disk_writes", settings)
            BoolSetting("HardwareAccel", "exp_hardware_accel", settings)
            BoolSetting("ClientDNSRejectInternalAddresses", "exp_client_dns_reject_internal", settings)
            BoolSetting("FascistFirewall", "exp_fascist_firewall", settings)
            StringSetting("FirewallPorts", "exp_firewall_ports", settings)
            StringSetting("ReachableAddresses", "exp_reachable_addresses", settings)
            IntSetting("NumCPUs", "exp_num_cpus", settings)
            StringSetting("ExcludeNodes", "exp_exclude_nodes", settings)
            StringSetting("ExcludeExitNodes", "exp_exclude_exit_nodes", settings)
            BoolSetting("UseEntryGuardsAsDirGuards", "exp_use_entry_guards_as_dir_guards", settings)
            IntSetting("PathBiasCircThreshold", "exp_path_bias_circ_threshold", settings)
            StringSetting("Reject exit ports", "exp_no_exit_stream_ports", settings)

            Spacer(Modifier.height(30.dp))
        }
    }
}

@Composable
private fun SectionHeader(title: String, color: androidx.compose.ui.graphics.Color = ACC) {
    Text(
        text = title,
        color = color,
        fontSize = 13.sp,
        modifier = Modifier
            .fillMaxWidth()
            .background(BTN)
            .padding(horizontal = 10.dp, vertical = 8.dp)
            .padding(top = 10.dp)
    )
}

@Composable
private fun BoolSetting(label: String, key: String, settings: com.deltator.tor.core.SettingsManager) {
    var value by remember { mutableStateOf(settings.getBoolean(key)) }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp, horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Checkbox(
            checked = value,
            onCheckedChange = {
                value = it
                settings.setBoolean(key, it)
            },
            colors = CheckboxDefaults.colors(checkedColor = ACC)
        )
        Text(label, color = FG, fontSize = 13.sp)
    }
}

@Composable
private fun IntSetting(label: String, key: String, settings: com.deltator.tor.core.SettingsManager) {
    var value by remember { mutableStateOf(settings.getInt(key).toString()) }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 3.dp, horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, color = FG, fontSize = 13.sp, modifier = Modifier.weight(1f))
        OutlinedTextField(
            value = value,
            onValueChange = {
                value = it
                it.toIntOrNull()?.let { v -> settings.setInt(key, v) }
            },
            modifier = Modifier.width(100.dp),
            textStyle = LocalTextStyle.current.copy(color = FG, fontSize = 13.sp),
            singleLine = true
        )
    }
}

@Composable
private fun StringSetting(label: String, key: String, settings: com.deltator.tor.core.SettingsManager) {
    var value by remember { mutableStateOf(settings.getString(key)) }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 3.dp, horizontal = 14.dp)
    ) {
        Text(label, color = FG2, fontSize = 12.sp)
        OutlinedTextField(
            value = value,
            onValueChange = {
                value = it
                settings.setString(key, it)
            },
            modifier = Modifier.fillMaxWidth(),
            textStyle = LocalTextStyle.current.copy(color = FG, fontSize = 13.sp),
            singleLine = true
        )
    }
}


