package io.deltator

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.deltator.tunnel.ParallelTorManager
import io.deltator.ui.DeltaTorTheme

class MainActivity : ComponentActivity() {

    private val vpnPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            if (result.resultCode == Activity.RESULT_OK) {
                startConnect()
            }
        }

    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { _ ->
            // Permission is best-effort; the service still runs
        }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestNotificationPermissionIfNeeded()
        setContent {
            DeltaTorTheme {
                DeltaTorScreen(
                    onConnect = { requestVpnPermissionAndConnect() },
                    onDisconnect = { sendAction(TorVpnService.ACTION_DISCONNECT) }
                )
            }
        }
    }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }

    private fun requestVpnPermissionAndConnect() {
        if (AppState.vpnStarted) {
            sendAction(TorVpnService.ACTION_DISCONNECT)
            return
        }
        val vpnIntent = VpnService.prepare(this)
        if (vpnIntent != null) {
            vpnPermissionLauncher.launch(vpnIntent)
        } else {
            startConnect()
        }
    }

    private fun startConnect() {
        val intent = Intent(this, TorVpnService::class.java).apply {
            action = TorVpnService.ACTION_CONNECT
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun sendAction(action: String) {
        val intent = Intent(this, TorVpnService::class.java).apply { this.action = action }
        ContextCompat.startForegroundService(this, intent)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeltaTorScreen(
    onConnect: () -> Unit,
    onDisconnect: () -> Unit
) {
    val state by AppState.state.collectAsStateWithLifecycle()

    Scaffold(containerColor = MaterialTheme.colorScheme.background) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .padding(horizontal = 20.dp)
                .verticalScroll(rememberScrollState()),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Spacer(Modifier.height(32.dp))
            Text(
                "DeltaTor",
                style = MaterialTheme.typography.headlineMedium,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.primary
            )
            Text(
                "Tor VPN \u2022 DNS tunneling",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.height(24.dp))

            StatusCard(
                state = state,
                onDisconnect = onDisconnect
            )
            Spacer(Modifier.height(24.dp))

            if (!AppState.vpnStarted) {
                Spacer(Modifier.height(16.dp))
                Button(
                    onClick = onConnect,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(52.dp)
                ) {
                    Text("Connect to Tor", style = MaterialTheme.typography.titleMedium)
                }
            }
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
fun StatusCard(state: AppState.VpnState, onDisconnect: () -> Unit) {
    val statusText = when {
        state.connected -> "Connected \u00b7 Tor Network (${state.transport})"
        state.connecting -> "Connecting \u2014 racing vanilla / obfs4 / webtunnel \u2026"
        else -> "Disconnected"
    }
    val statusColor = when {
        state.connected -> Color(0xFF2E7D32)
        state.connecting -> MaterialTheme.colorScheme.primary
        else -> MaterialTheme.colorScheme.onSurfaceVariant
    }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(20.dp),
            horizontalAlignment = Alignment.Start
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                val dotColor = when {
                    state.connected -> Color(0xFF2E7D32)
                    state.connecting -> MaterialTheme.colorScheme.primary
                    else -> MaterialTheme.colorScheme.outline
                }
                Surface(color = dotColor, shape = CircleShape, modifier = Modifier.padding(2.dp)) {
                    Text(" ", modifier = Modifier.width(16.dp))
                }
                Spacer(Modifier.width(8.dp))
                Text(
                    statusText,
                    style = MaterialTheme.typography.titleMedium,
                    color = statusColor
                )
            }

            if (state.connecting) {
                Spacer(Modifier.height(12.dp))
                val transports = listOf(
                    ParallelTorManager.TRANSPORT_VANILLA,
                    ParallelTorManager.TRANSPORT_OBFS4,
                    ParallelTorManager.TRANSPORT_WEBTUNNEL
                )
                transports.forEach { name ->
                    val value = state.transports[name]
                    val failed = value == -1
                    val started = value != null && !failed
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            name,
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (failed) MaterialTheme.colorScheme.error
                            else MaterialTheme.colorScheme.onSurface,
                            modifier = Modifier.width(90.dp)
                        )
                        if (started) {
                            LinearProgressIndicator(
                                progress = { (value ?: 0) / 100f },
                                modifier = Modifier.weight(1f)
                            )
                            Spacer(Modifier.width(8.dp))
                            Text(
                                "$value%",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        } else if (failed) {
                            Text(
                                "FAIL",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.error
                            )
                        } else {
                            Text(
                                "starting\u2026",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                    Spacer(Modifier.height(4.dp))
                }
                Spacer(Modifier.height(4.dp))
            }

            if (state.connected) {
                Spacer(Modifier.height(8.dp))
                Text(
                    "Traffic \u2191 ${formatBytes(state.txBytes)}  \u2193 ${formatBytes(state.rxBytes)}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(12.dp))
                OutlinedButton(onClick = onDisconnect, modifier = Modifier.fillMaxWidth()) {
                    Text("Disconnect")
                }
            }

            if (state.error != null) {
                Spacer(Modifier.height(12.dp))
                Text(
                    state.error,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            }
        }
    }
}

private fun formatBytes(bytes: Long): String {
    if (bytes < 1024) return "$bytes B"
    val units = arrayOf("KB", "MB", "GB", "TB")
    var v = bytes.toFloat()
    var idx = -1
    while (v >= 1024 && idx < units.size - 1) {
        v /= 1024f
        idx++
    }
    return String.format("%.1f %s", v, units[idx])
}