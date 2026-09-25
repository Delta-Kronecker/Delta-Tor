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
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.deltator.tunnel.BridgeStore
import io.deltator.ui.DeltaTorTheme
import io.deltator.ui.DeltaTor

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
                    onPrimary = {
                        val s = AppState.state.value
                        when {
                            s.connecting || s.connected -> sendAction(TorVpnService.ACTION_DISCONNECT)
                            s.torRunning -> sendAction(TorVpnService.ACTION_START_VPN)
                            else -> requestVpnPermissionAndConnect()
                        }
                    },
                    onStopVpn = { sendAction(TorVpnService.ACTION_STOP_VPN) },
                    onDisconnect = { sendAction(TorVpnService.ACTION_DISCONNECT) },
                    onUpdateBridges = { BridgeStore.update(applicationContext) }
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
        if (AppState.state.value.torRunning) {
            sendAction(TorVpnService.ACTION_START_VPN)
            return
        }
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

// ---------------------------------------------------------------------------
// The DeltaTor look (mirrors scripts/TorJetUi.cs MainForm): a centered glowing
// power ring with big state text above and status/actions below — dark,
// borderless, professional.
// ---------------------------------------------------------------------------

@Composable
fun DeltaTorScreen(
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    onUpdateBridges: () -> Unit
) {
    val state by AppState.state.collectAsStateWithLifecycle()
    val bridges by AppState.bridgeState.collectAsStateWithLifecycle()

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(DeltaTor.Bg)
            .windowInsetsPadding(WindowInsets.systemBars)
    ) {
        MainPage(
            state = state,
            bridges = bridges,
            onPrimary = onPrimary,
            onStopVpn = onStopVpn,
            onDisconnect = onDisconnect,
            onUpdateBridges = onUpdateBridges
        )
    }
}

private fun stateColor(state: AppState.VpnState): Color = when {
    state.connecting -> DeltaTor.Amber
    state.connected -> DeltaTor.Green
    state.torRunning -> DeltaTor.Amber
    else -> DeltaTor.Muted
}

// ---- main page --------------------------------------------------------------

@Composable
private fun MainPage(
    state: AppState.VpnState,
    bridges: AppState.BridgeState,
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    onUpdateBridges: () -> Unit
) {
    val connecting = state.connecting
    val connected = state.connected
    val torRunning = state.torRunning
    val sc = stateColor(state)

    val big = when {
        connecting && torRunning -> "RECONNECTING"
        connecting -> "RACING"
        connected -> "CONNECTED"
        torRunning -> "READY"
        else -> "OFFLINE"
    }

    // race percentage, ratcheted upward to the peak, like the win UI
    var peak by remember { mutableStateOf(0) }
    var wasConnecting by remember { mutableStateOf(false) }
    LaunchedEffect(connecting) {
        if (connecting && !wasConnecting) peak = 0
        if (!connecting) peak = 0
        wasConnecting = connecting
    }
    if (connecting) {
        peak = maxOf(peak, state.transports.values.filter { it >= 0 }.maxOrNull() ?: 0)
    }
    val raceText = if (connecting && !torRunning) "RACE $peak%" else ""

    val ringProgress = if (connecting && !torRunning && peak > 0) peak / 100f else null
    val ringColor = when {
        connecting -> DeltaTor.Accent
        connected -> DeltaTor.Green
        torRunning -> DeltaTor.Amber
        else -> DeltaTor.BorderLight
    }
    val ringGlow = when {
        connecting -> DeltaTor.Accent
        connected -> DeltaTor.Green
        else -> null
    }
    val glyphColor = when {
        connecting -> DeltaTor.Amber
        connected -> DeltaTor.Green
        else -> DeltaTor.Text
    }

    val labelText = when {
        state.error != null -> state.error
        connecting -> "CANCEL"
        connected -> "DISCONNECT"
        torRunning -> "START VPN"
        else -> "CONNECT"
    }
    val labelColor = if (state.error != null) DeltaTor.Red else DeltaTor.Muted

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 24.dp)
    ) {
        // top zone: state text; flex so the ring stays at true center
        Box(
            Modifier
                .fillMaxWidth()
                .weight(1f),
            contentAlignment = Alignment.Center
        ) {
            Text(
                text = if (connecting && !torRunning) raceText else big,
                style = TextStyle(
                    fontSize = 28.sp,
                    fontWeight = FontWeight.Bold,
                    color = sc,
                    textAlign = TextAlign.Center,
                    shadow = Shadow(Color.Black.copy(alpha = 0.3f), Offset(0f, 2f), 0f)
                )
            )
        }

        // center zone: the ring stays at the exact center of the screen
        Column(
            modifier = Modifier.fillMaxWidth(),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(
                Modifier.clickable(enabled = true) { onPrimary() },
                contentAlignment = Alignment.Center
            ) {
                PowerRing(
                    ringColor = ringColor,
                    glowColor = ringGlow,
                    glyphColor = glyphColor,
                    progress = ringProgress
                )
            }
            Spacer(Modifier.height(16.dp))
            Text(
                labelText,
                style = MaterialTheme.typography.bodyLarge,
                color = labelColor,
                textAlign = TextAlign.Center
            )
        }

        // bottom zone: status and actions; flex so the ring stays at true center
        Box(
            Modifier
                .fillMaxWidth()
                .weight(1f),
            contentAlignment = Alignment.Center
        ) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                if (connected) {
                    Text(
                        "${state.transport.uppercase()} \u00b7 SOCKS 127.0.0.1:${Config.proxyPort}",
                        style = MaterialTheme.typography.bodySmall,
                        color = DeltaTor.Muted
                    )
                    Spacer(Modifier.height(2.dp))
                    Text(
                        "\u25b2 ${formatBytes(state.txBytes)}   \u25bc ${formatBytes(state.rxBytes)}",
                        style = MaterialTheme.typography.bodySmall,
                        color = DeltaTor.Muted
                    )
                    Spacer(Modifier.height(20.dp))
                    ActionPill(label = "STOP VPN", onClick = onStopVpn)
                } else if (torRunning) {
                    Text(
                        "Tor on SOCKS 127.0.0.1:${Config.proxyPort} \u00b7 VPN stopped",
                        style = MaterialTheme.typography.bodySmall,
                        color = DeltaTor.Muted
                    )
                    Spacer(Modifier.height(20.dp))
                    ActionPill(label = "DISCONNECT", onClick = onDisconnect)
                }
                Spacer(Modifier.height(if (connected || torRunning) 24.dp else 0.dp))
                BridgePanel(bridges = bridges, onUpdate = onUpdateBridges)
            }
        }
    }
}

@Composable
private fun ActionPill(label: String, onClick: () -> Unit) {
    PillSurface(height = 44.dp) {
        Box(
            Modifier
                .fillMaxWidth()
                .height(44.dp)
                .clickable(onClick = onClick),
            contentAlignment = Alignment.Center
        ) {
            Text(
                label,
                style = MaterialTheme.typography.titleMedium,
                color = DeltaTor.Text
            )
        }
    }
}

// ---- bridges ----------------------------------------------------------------

@Composable
private fun BridgePanel(bridges: AppState.BridgeState, onUpdate: () -> Unit) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "BRIDGES",
                style = MaterialTheme.typography.labelSmall,
                color = DeltaTor.Muted
            )
            Spacer(Modifier.weight(1f))
            if (bridges.updating) {
                Text(
                    "UPDATING \u2026",
                    style = MaterialTheme.typography.labelSmall,
                    color = DeltaTor.Amber
                )
            } else {
                Box(
                    Modifier
                        .clip(RoundedCornerShape(12.dp))
                        .border(1.dp, DeltaTor.BorderLight, RoundedCornerShape(12.dp))
                        .clickable { onUpdate() }
                        .padding(horizontal = 12.dp, vertical = 5.dp)
                ) {
                    Text(
                        "UPDATE",
                        style = MaterialTheme.typography.labelSmall,
                        color = DeltaTor.Accent
                    )
                }
            }
        }
        Spacer(Modifier.height(10.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            BridgeChip("VANILLA", bridges.vanilla)
            BridgeChip("OBFS4", bridges.obfs4)
            BridgeChip("WEBTUNNEL", bridges.webtunnel)
        }
        Spacer(Modifier.height(8.dp))
        val footer = if (bridges.error != null) {
            "Update failed \u00b7 ${bridges.error}"
        } else {
            "Updated ${relativeTime(bridges.lastUpdateMillis)}"
        }
        Text(
            footer,
            style = MaterialTheme.typography.bodySmall,
            color = if (bridges.error != null) DeltaTor.Red else DeltaTor.Muted,
            textAlign = TextAlign.Center
        )
    }
}

@Composable
private fun BridgeChip(label: String, count: Int) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(12.dp))
            .background(DeltaTor.Surface, RoundedCornerShape(12.dp))
            .border(1.dp, DeltaTor.Border, RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = DeltaTor.Muted
        )
        Spacer(Modifier.width(8.dp))
        Text(
            "$count",
            style = MaterialTheme.typography.labelSmall,
            color = DeltaTor.Text
        )
    }
}

private fun relativeTime(ms: Long): String {
    if (ms <= 0) return "never"
    val secs = (System.currentTimeMillis() - ms) / 1000
    return when {
        secs < 60 -> "just now"
        secs < 3600 -> "${secs / 60}m ago"
        secs < 86_400 -> "${secs / 3600}h ago"
        else -> "${secs / 86_400}d ago"
    }
}

// ---- ring -------------------------------------------------------------------

@Composable
private fun PowerRing(
    ringColor: Color,
    glowColor: Color?,
    glyphColor: Color,
    progress: Float?
) {
    Canvas(Modifier.size(128.dp)) {
        val stroke = 6.dp.toPx()
        val glowRadius = size.minDimension / 2f + 18.dp.toPx()

        if (glowColor != null) {
            drawCircle(
                brush = Brush.radialGradient(
                    listOf(glowColor.copy(alpha = 0.32f), glowColor.copy(alpha = 0f)),
                    center = center,
                    radius = glowRadius
                ),
                radius = glowRadius,
                center = center
            )
        }

        // soft drop shadow, offset like the win PaintShadow (3,3)
        val off = 3.dp.toPx()
        drawArc(
            color = Color.Black.copy(alpha = 0.16f),
            startAngle = 0f,
            sweepAngle = 360f,
            useCenter = false,
            topLeft = Offset(off, off),
            size = size,
            style = Stroke(stroke, cap = StrokeCap.Round)
        )

        if (progress != null && progress > 0f) {
            drawArc(
                color = DeltaTor.Border,
                startAngle = -90f,
                sweepAngle = 360f,
                useCenter = false,
                size = size,
                style = Stroke(stroke, cap = StrokeCap.Round)
            )
            drawArc(
                color = DeltaTor.Accent,
                startAngle = -90f,
                sweepAngle = 360f * progress.coerceIn(0f, 1f),
                useCenter = false,
                size = size,
                style = Stroke(stroke, cap = StrokeCap.Round)
            )
        } else {
            drawArc(
                brush = Brush.verticalGradient(listOf(ringColor, ringColor.copy(alpha = 0.7f))),
                startAngle = 0f,
                sweepAngle = 360f,
                useCenter = false,
                size = size,
                style = Stroke(stroke, cap = StrokeCap.Round)
            )
        }

        // power glyph (arc -60..240 + stem)
        val g = 26.dp.toPx()
        drawArc(
            color = glyphColor,
            startAngle = -60f,
            sweepAngle = 300f,
            useCenter = false,
            topLeft = Offset(g, g),
            size = Size(size.width - 2 * g, size.height - 2 * g),
            style = Stroke(stroke, cap = StrokeCap.Round)
        )
        val cx = size.width / 2f
        drawLine(
            color = glyphColor,
            start = Offset(cx, 18.dp.toPx()),
            end = Offset(cx, 56.dp.toPx()),
            strokeWidth = stroke,
            cap = StrokeCap.Round
        )
    }
}

// ---- pill row ---------------------------------------------------------------

@Composable
private fun PillSurface(
    height: Dp,
    content: @Composable () -> Unit
) {
    Box(
        Modifier
            .fillMaxWidth()
            .height(height)
            .clip(RoundedCornerShape(height / 2))
            .background(
                Brush.verticalGradient(listOf(DeltaTor.SurfaceAlt, DeltaTor.Surface)),
                RoundedCornerShape(height / 2)
            )
            .border(
                width = 1.dp,
                color = DeltaTor.Border,
                shape = RoundedCornerShape(height / 2)
            )
    ) {
        content()
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