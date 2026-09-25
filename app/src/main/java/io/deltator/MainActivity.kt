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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
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
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.deltator.tunnel.ParallelTorManager
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

// ---------------------------------------------------------------------------
// The DeltaTor look (mirrors scripts/TorJetUi.cs MainForm): a slim gradient
// titlebar, big state text with a drop shadow, and a centered glowing power
// ring with the connect label — dark, borderless, professional.
// ---------------------------------------------------------------------------

@Composable
fun DeltaTorScreen(
    onConnect: () -> Unit,
    onDisconnect: () -> Unit
) {
    val state by AppState.state.collectAsStateWithLifecycle()
    val ctx = LocalContext.current
    val version = remember {
        runCatching {
            ctx.packageManager.getPackageInfo(ctx.packageName, 0).versionName
        }.getOrNull() ?: ""
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(DeltaTor.Bg)
            .windowInsetsPadding(WindowInsets.systemBars)
    ) {
        TitleBar(
            dotColor = stateColor(state),
            version = version
        )
        MainPage(
            state = state,
            onPrimary = { if (state.connecting || state.connected) onDisconnect() else onConnect() }
        )
    }
}

private fun stateColor(state: AppState.VpnState): Color = when {
    state.connecting -> DeltaTor.Amber
    state.connected -> DeltaTor.Green
    else -> DeltaTor.Muted
}

// ---- titlebar ---------------------------------------------------------------

@Composable
private fun TitleBar(dotColor: Color, version: String) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .height(44.dp)
            .background(Brush.verticalGradient(listOf(DeltaTor.SurfaceAlt, DeltaTor.Surface)))
            .drawBehind {
                drawLine(
                    DeltaTor.Border,
                    Offset(0f, size.height - 1.dp.toPx()),
                    Offset(size.width, size.height - 1.dp.toPx()),
                    1.dp.toPx()
                )
            }
            .padding(horizontal = 14.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth()) {
            StatusDot(dotColor)
            Spacer(Modifier.width(8.dp))
            Text(
                "DeltaTor",
                style = MaterialTheme.typography.titleLarge,
                color = DeltaTor.Text
            )
            Spacer(Modifier.weight(1f))
            Text(
                "v$version",
                style = MaterialTheme.typography.bodySmall,
                color = DeltaTor.Muted
            )
        }
    }
}

@Composable
private fun StatusDot(color: Color) {
    Box(
        modifier = Modifier
            .size(20.dp)
            .drawBehind {
                drawCircle(
                    brush = Brush.radialGradient(
                        listOf(color.copy(alpha = 0.5f), color.copy(alpha = 0f)),
                        center = center,
                        radius = size.minDimension / 2f
                    ),
                    radius = size.minDimension / 2f,
                    center = center
                )
            },
        contentAlignment = Alignment.Center
    ) {
        Box(
            Modifier
                .size(8.dp)
                .background(color.copy(alpha = 0.92f), CircleShape)
        )
    }
}

// ---- main page --------------------------------------------------------------

@Composable
private fun MainPage(
    state: AppState.VpnState,
    onPrimary: () -> Unit
) {
    val connecting = state.connecting
    val connected = state.connected
    val sc = stateColor(state)

    val big = when {
        connecting -> "RACING"
        connected -> "CONNECTED"
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
    val raceText = if (connecting) "RACE $peak%" else ""

    val ringProgress = if (connecting && peak > 0) peak / 100f else null
    val ringColor = when {
        connecting -> DeltaTor.Accent
        connected -> DeltaTor.Green
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
        connected -> "DISCONNECT"
        connecting -> "CANCEL"
        else -> "CONNECT"
    }
    val labelColor = if (state.error != null) DeltaTor.Red else DeltaTor.Muted

    Box(
        modifier = Modifier
            .fillMaxSize()
            .padding(horizontal = 24.dp),
        contentAlignment = Alignment.Center
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(
                text = if (connecting) raceText else big,
                style = TextStyle(
                    fontSize = 28.sp,
                    fontWeight = FontWeight.Bold,
                    color = sc,
                    textAlign = TextAlign.Center,
                    shadow = Shadow(Color.Black.copy(alpha = 0.3f), Offset(0f, 2f), 0f)
                )
            )
            Spacer(Modifier.height(20.dp))

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
            if (connected) {
                Spacer(Modifier.height(8.dp))
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
            }
            if (connecting) {
                Spacer(Modifier.height(24.dp))
                RaceRows(state)
            }
        }
    }
}

@Composable
private fun RaceRows(state: AppState.VpnState) {
    Column(Modifier.fillMaxWidth()) {
        listOf(
            Pair("Vanilla", ParallelTorManager.TRANSPORT_VANILLA),
            Pair("Obfs4", ParallelTorManager.TRANSPORT_OBFS4),
            Pair("WebTunnel", ParallelTorManager.TRANSPORT_WEBTUNNEL)
        ).forEach { (label, name) ->
            val value = state.transports[name]
            val failed = value == -1
            val started = value != null && value >= 0
            PillSurface(height = 40.dp) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(40.dp)
                        .padding(horizontal = 16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        label,
                        style = MaterialTheme.typography.titleMedium,
                        color = if (failed) MaterialTheme.colorScheme.error else DeltaTor.Text
                    )
                    Spacer(Modifier.weight(1f))
                    if (started) {
                        LinearProgressIndicator(
                            progress = { (value) / 100f },
                            modifier = Modifier
                                .width(100.dp)
                                .height(6.dp)
                                .clip(RoundedCornerShape(3.dp)),
                            color = DeltaTor.Accent,
                            trackColor = DeltaTor.SurfaceLight,
                            strokeCap = StrokeCap.Round
                        )
                        Spacer(Modifier.width(10.dp))
                        Text(
                            "$value%",
                            style = MaterialTheme.typography.labelSmall,
                            color = DeltaTor.Muted,
                            textAlign = TextAlign.End,
                            modifier = Modifier.width(38.dp)
                        )
                    } else if (failed) {
                        Text(
                            "FAIL",
                            style = MaterialTheme.typography.labelSmall,
                            color = MaterialTheme.colorScheme.error
                        )
                    } else {
                        Text(
                            "starting\u2026",
                            style = MaterialTheme.typography.labelSmall,
                            color = DeltaTor.Muted
                        )
                    }
                }
            }
            Spacer(Modifier.height(8.dp))
        }
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
    Canvas(Modifier.size(104.dp)) {
        val stroke = 5.dp.toPx()
        val glowRadius = size.minDimension / 2f + 14.dp.toPx()

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
        val g = 22.dp.toPx()
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
            start = Offset(cx, 16.dp.toPx()),
            end = Offset(cx, 46.dp.toPx()),
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