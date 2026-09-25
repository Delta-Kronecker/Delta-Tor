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
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.deltator.tunnel.BridgeStore
import io.deltator.ui.DeltaTor
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
// DeltaTor — a bold, glassy VPN interface: layered ambient glow, an animated
// power ring that always floats dead-center, live stat cards and a bridge
// store panel. Dark, borderless, owner-drawn and fully professional.
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
    val sc = stateColor(state)

    val connecting = state.connecting
    val connected = state.connected
    val torRunning = state.torRunning
    val scAnimated by animateColorAsState(sc, tween(450), label = "stateColor")

    Box(
        modifier = Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.systemBars)
    ) {
        AirBackground(glow = scAnimated)

        Header(
            statusColor = scAnimated,
            statusLabel = statusLabel(connecting, connected, torRunning),
            modifier = Modifier.align(Alignment.TopCenter)
        )

        StateBlock(
            connecting = connecting,
            torRunning = torRunning,
            connected = connected,
            transport = state.transport,
            peak = peakPct(state.connecting, state.torRunning, state.transports),
            sc = scAnimated,
            hasError = state.error != null,
            modifier = Modifier.align(Alignment.Center).offset(y = (-128).dp)
        )

        RingButton(
            modifier = Modifier.align(Alignment.Center),
            ringColor = scAnimated,
            glowColor = if (connecting || connected) scAnimated else null,
            glyphColor = glyphColor(connecting, connected),
            progress = ringProgressOf(state),
            connecting = connecting,
            onClick = onPrimary
        )

        Text(
            labelText(state),
            modifier = Modifier
                .align(Alignment.Center)
                .offset(y = 122.dp)
                .fillMaxWidth()
                .padding(horizontal = 32.dp),
            style = MaterialTheme.typography.bodyLarge.copy(letterSpacing = 1.4.sp),
            color = if (state.error != null) DeltaTor.Red else DeltaTor.Muted,
            textAlign = TextAlign.Center
        )

        BottomPanel(
            state = state,
            sc = scAnimated,
            bridges = bridges,
            onPrimary = onPrimary,
            onStopVpn = onStopVpn,
            onDisconnect = onDisconnect,
            onUpdateBridges = onUpdateBridges,
            modifier = Modifier.align(Alignment.BottomCenter)
        )
    }
}

// ---- state helpers ----------------------------------------------------------

private fun stateColor(state: AppState.VpnState): Color = when {
    state.connecting -> DeltaTor.Amber
    state.connected -> DeltaTor.Green
    state.torRunning -> DeltaTor.Amber
    else -> DeltaTor.Muted
}

private fun statusLabel(connecting: Boolean, connected: Boolean, torRunning: Boolean): String = when {
    connecting -> "CONNECTING"
    connected -> "CONNECTED"
    torRunning -> "READY"
    else -> "OFFLINE"
}

private fun glyphColor(connecting: Boolean, connected: Boolean): Color = when {
    connecting -> DeltaTor.Amber
    connected -> DeltaTor.Green
    else -> DeltaTor.Text
}

private fun labelText(state: AppState.VpnState): String = when {
    state.error != null -> state.error
    state.connecting -> "CANCEL"
    state.connected -> "DISCONNECT"
    state.torRunning -> "START VPN"
    else -> "CONNECT"
}

private fun wordFor(connecting: Boolean, torRunning: Boolean, connected: Boolean, hasError: Boolean): String = when {
    hasError -> "ERROR"
    connecting && torRunning -> "RECONNECTING"
    connecting -> "RACING"
    connected -> "CONNECTED"
    torRunning -> "READY"
    else -> "OFFLINE"
}

private fun sublineFor(
    connecting: Boolean,
    torRunning: Boolean,
    connected: Boolean,
    transport: String,
    peak: Int,
    hasError: Boolean
): String = when {
    hasError -> "BOOTSTRAP FAILED"
    connecting && torRunning -> "REINITIALIZING TUNNEL"
    connecting -> "TUNNEL BOOTSTRAPPING \u00b7 $peak%"
    connected -> "${transport.uppercase()} \u00b7 GATEWAY ACTIVE"
    torRunning -> "TOR RUNNING \u00b7 VPN PAUSED"
    else -> "YOUR PRIVATE GATEWAY"
}

@Composable
private fun peakPct(connecting: Boolean, torRunning: Boolean, transports: Map<String, Int>): Int {
    var peak by remember { mutableStateOf(0) }
    var wasConnecting by remember { mutableStateOf(false) }
    LaunchedEffect(connecting) {
        if (connecting && !wasConnecting) peak = 0
        if (!connecting) peak = 0
        wasConnecting = connecting
    }
    if (connecting) {
        peak = maxOf(peak, transports.values.filter { it >= 0 }.maxOrNull() ?: 0)
    }
    return peak
}

@Composable
private fun ringProgressOf(state: AppState.VpnState): Float? {
    val peak = peakPct(state.connecting, state.torRunning, state.transports)
    return if (state.connecting && !state.torRunning && peak > 0) peak / 100f else null
}

// ---- background -------------------------------------------------------------

@Composable
private fun AirBackground(glow: Color) {
    val pulse by rememberInfiniteTransition(label = "bgpulse")
        .animateFloat(
            0.5f, 1f,
            infiniteRepeatable(tween(2600, easing = FastOutSlowInEasing), RepeatMode.Reverse),
            label = "a"
        )
    Canvas(Modifier.fillMaxSize()) {
        drawRect(
            Brush.verticalGradient(
                listOf(Color(0xFF1B2030), DeltaTor.Bg, Color(0xFF0C0E15))
            )
        )
        // soft key light in the top-left corner
        drawCircle(
            brush = Brush.radialGradient(
                listOf(Color.White.copy(alpha = 0.05f), Color.Transparent),
                center = Offset(size.width * 0.16f, size.height * 0.1f),
                radius = size.width * 0.55f
            ),
            radius = size.width * 0.55f,
            center = Offset(size.width * 0.16f, size.height * 0.1f)
        )
        // state-colored halo exactly behind the ring
        val c = Offset(size.width / 2f, size.height * 0.5f)
        val halo = size.width * 0.58f
        drawCircle(
            brush = Brush.radialGradient(
                listOf(glow.copy(alpha = 0.16f * pulse), Color.Transparent),
                center = c,
                radius = halo
            ),
            radius = halo,
            center = c
        )
        // faint accent bloom at the bottom edge
        val cb = Offset(size.width / 2f, size.height * 0.98f)
        val rb = size.width * 0.7f
        drawCircle(
            brush = Brush.radialGradient(
                listOf(DeltaTor.Accent.copy(alpha = 0.07f), Color.Transparent),
                center = cb,
                radius = rb
            ),
            radius = rb,
            center = cb
        )
    }
}

// ---- header ----------------------------------------------------------------

@Composable
private fun Header(statusColor: Color, statusLabel: String, modifier: Modifier = Modifier) {
    Column(
        modifier
            .fillMaxWidth()
            .padding(horizontal = 22.dp, vertical = 18.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Logo()
            Spacer(Modifier.width(12.dp))
            Text(
                "DELTA",
                style = MaterialTheme.typography.titleLarge.copy(
                    letterSpacing = 2.6.sp,
                    fontSize = 16.sp
                ),
                color = DeltaTor.Text
            )
            Text(
                "TOR",
                style = MaterialTheme.typography.titleLarge.copy(
                    letterSpacing = 2.6.sp,
                    fontSize = 16.sp
                ),
                color = DeltaTor.AccentLight
            )
            Spacer(Modifier.weight(1f))
            StatusChip(color = statusColor, label = statusLabel)
        }
        Spacer(Modifier.height(14.dp))
        Box(
            Modifier
                .fillMaxWidth()
                .height(1.dp)
                .background(
                    Brush.horizontalGradient(
                        listOf(Color.Transparent, DeltaTor.Border, Color.Transparent)
                    )
                )
        )
    }
}

@Composable
private fun Logo() {
    Box(
        modifier = Modifier
            .size(40.dp)
            .clip(RoundedCornerShape(13.dp))
            .background(Brush.linearGradient(listOf(DeltaTor.AccentDark, DeltaTor.Accent)))
            .border(1.dp, DeltaTor.AccentLight.copy(alpha = 0.35f), RoundedCornerShape(13.dp)),
        contentAlignment = Alignment.Center
    ) {
        Canvas(Modifier.size(20.dp)) {
            val stroke = 2.dp.toPx()
            val r = size.minDimension / 2f - stroke
            drawArc(
                Brush.linearGradient(listOf(Color.White, DeltaTor.AccentLight)),
                startAngle = 310f,
                sweepAngle = 280f,
                useCenter = false,
                topLeft = Offset(center.x - r, center.y - r),
                size = Size(r * 2, r * 2),
                style = Stroke(stroke, cap = StrokeCap.Round)
            )
            drawLine(
                Color.White,
                Offset(center.x, center.y - r - 2.dp.toPx()),
                Offset(center.x, center.y),
                stroke,
                StrokeCap.Round
            )
        }
    }
}

@Composable
private fun StatusChip(color: Color, label: String) {
    Row(
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(DeltaTor.Surface, RoundedCornerShape(50))
            .border(1.dp, DeltaTor.BorderLight, RoundedCornerShape(50))
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(9.dp)
                .clip(RoundedCornerShape(50))
                .background(color)
        )
        Spacer(Modifier.width(8.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.1.sp,
                fontWeight = FontWeight.Bold
            ),
            color = color
        )
    }
}

// ---- big state text ---------------------------------------------------------

@Composable
private fun StateBlock(
    connecting: Boolean,
    torRunning: Boolean,
    connected: Boolean,
    transport: String,
    peak: Int,
    sc: Color,
    hasError: Boolean,
    modifier: Modifier = Modifier
) {
    val word = wordFor(connecting, torRunning, connected, hasError)
    val sub = sublineFor(connecting, torRunning, connected, transport, peak, hasError)
    val subColor by animateColorAsState(
        if (connected) DeltaTor.GreenLight else DeltaTor.Muted,
        tween(450),
        label = "sub"
    )

    Column(
        modifier = modifier,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        AnimatedContent(
            targetState = word,
            transitionSpec = { fadeIn(tween(230)) togetherWith fadeOut(tween(120)) },
            label = "bigWord"
        ) { w ->
            Text(
                w,
                style = TextStyle(
                    fontWeight = FontWeight.Bold,
                    fontSize = 30.sp,
                    letterSpacing = 2.5.sp,
                    color = sc,
                    textAlign = TextAlign.Center,
                    shadow = Shadow(Color.Black.copy(alpha = 0.45f), Offset(0f, 4f), 10f)
                )
            )
        }
        Spacer(Modifier.height(8.dp))
        Text(
            sub,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.4.sp,
                fontWeight = FontWeight.Bold
            ),
            color = subColor
        )
    }
}

// ---- power ring -------------------------------------------------------------

@Composable
private fun RingButton(
    modifier: Modifier = Modifier,
    ringColor: Color,
    glowColor: Color?,
    glyphColor: Color,
    progress: Float?,
    connecting: Boolean,
    onClick: () -> Unit
) {
    val rotation by rememberInfiniteTransition(label = "ringSpin")
        .animateFloat(
            0f, 360f,
            infiniteRepeatable(tween(9000, easing = LinearEasing), RepeatMode.Restart),
            label = "r"
        )
    val pulse by rememberInfiniteTransition(label = "ringPulse")
        .animateFloat(
            0.55f, 1f,
            infiniteRepeatable(tween(2200, easing = FastOutSlowInEasing), RepeatMode.Reverse),
            label = "p"
        )
    val breathe by rememberInfiniteTransition(label = "ringBreath")
        .animateFloat(
            1f, 1.035f,
            infiniteRepeatable(tween(2400, easing = FastOutSlowInEasing), RepeatMode.Reverse),
            label = "b"
        )
    val scale = if (connecting) breathe else 1f

    Box(
        modifier = modifier
            .size(176.dp)
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null
            ) { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Canvas(Modifier.fillMaxSize()) {
            val c = center
            val r = size.minDimension / 2f
            val halo = 150.dp.toPx()

            // ambient halo (pulse when active)
            if (glowColor != null) {
                drawCircle(
                    brush = Brush.radialGradient(
                        listOf(glowColor.copy(alpha = 0.30f * pulse), Color.Transparent),
                        center = c,
                        radius = halo
                    ),
                    radius = halo,
                    center = c
                )
            } else {
                drawCircle(
                    brush = Brush.radialGradient(
                        listOf(DeltaTor.BorderLight.copy(alpha = 0.10f), Color.Transparent),
                        center = c,
                        radius = halo
                    ),
                    radius = halo,
                    center = c
                )
            }

            // rotating beam trail behind the track while connecting
            if (connecting) {
                val beam = Brush.sweepGradient(
                    listOf(ringColor.copy(alpha = 0f), ringColor.copy(alpha = 0.30f), ringColor.copy(alpha = 0f)),
                    center = c
                )
                drawArc(
                    brush = beam,
                    startAngle = rotation,
                    sweepAngle = 360f,
                    useCenter = true,
                    style = Stroke(10.dp.toPx(), cap = StrokeCap.Round)
                )
            }

            // thin, flat track ring
            drawArc(
                color = DeltaTor.BorderLight.copy(alpha = 0.35f),
                startAngle = -90f,
                sweepAngle = 360f,
                useCenter = false,
                size = Size(r * 2, r * 2),
                style = Stroke(2.dp.toPx(), cap = StrokeCap.Round)
            )

            // progress arc — one solid state color, always starts at the top
            val prog = (progress ?: 0f).coerceIn(0f, 1f)
            if (prog > 0f) {
                drawArc(
                    color = ringColor,
                    startAngle = -90f,
                    sweepAngle = 360f * prog,
                    useCenter = false,
                    size = Size(r * 2, r * 2),
                    style = Stroke(6.dp.toPx(), cap = StrokeCap.Round)
                )
            }

            // flat matte disc
            val inner = r - 9.dp.toPx()
            drawCircle(
                brush = Brush.radialGradient(
                    listOf(DeltaTor.Surface, Color(0xFF0F1119)),
                    center = c,
                    radius = inner
                ),
                radius = inner,
                center = c
            )
            drawCircle(
                color = DeltaTor.Border.copy(alpha = 0.9f),
                radius = inner,
                center = c,
                style = Stroke(1.dp.toPx())
            )

            // the power glyph — crisp single color
            val rg = inner * 0.46f
            val gp = 9.dp.toPx()
            drawArc(
                color = glyphColor,
                startAngle = 310f,
                sweepAngle = 280f,
                useCenter = false,
                topLeft = Offset(c.x - rg, c.y - rg),
                size = Size(rg * 2, rg * 2),
                style = Stroke(gp, cap = StrokeCap.Round)
            )
            drawLine(
                glyphColor,
                Offset(c.x, c.y - rg),
                Offset(c.x, c.y + rg * 0.5f),
                gp,
                StrokeCap.Round
            )
        }
    }
}

// ---- bottom panel -----------------------------------------------------------

@Composable
private fun BottomPanel(
    state: AppState.VpnState,
    sc: Color,
    bridges: AppState.BridgeState,
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    onUpdateBridges: () -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(start = 20.dp, end = 20.dp, top = 6.dp, bottom = 16.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        when {
            state.connected -> Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                GradientPill(
                    modifier = Modifier.weight(1f),
                    label = "STOP VPN",
                    filled = true,
                    onClick = onStopVpn
                )
                GradientPill(
                    modifier = Modifier.weight(1f),
                    label = "DISCONNECT",
                    filled = false,
                    onClick = onDisconnect
                )
            }
            state.torRunning -> Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                GradientPill(
                    modifier = Modifier.weight(1f),
                    label = "START VPN",
                    filled = true,
                    onClick = onPrimary
                )
                GradientPill(
                    modifier = Modifier.weight(1f),
                    label = "DISCONNECT",
                    filled = false,
                    onClick = onDisconnect
                )
            }
        }

        if (state.connected || state.torRunning) {
            Spacer(Modifier.height(12.dp))
        }

        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            StatCard(
                label = "DOWNLOADED",
                value = formatBytes(state.txBytes),
                accent = DeltaTor.Green,
                up = false,
                modifier = Modifier.weight(1f)
            )
            StatCard(
                label = "UPLOADED",
                value = formatBytes(state.rxBytes),
                accent = DeltaTor.Accent,
                up = true,
                modifier = Modifier.weight(1f)
            )
        }

        Spacer(Modifier.height(12.dp))

        BridgeCard(bridges = bridges, sc = sc, onUpdate = onUpdateBridges)
    }
}

@Composable
private fun GradientPill(
    modifier: Modifier = Modifier,
    label: String,
    filled: Boolean,
    onClick: () -> Unit
) {
    val shape = RoundedCornerShape(24.dp)
    val bg = if (filled) {
        Brush.horizontalGradient(listOf(DeltaTor.AccentSoft, DeltaTor.Accent))
    } else {
        Brush.horizontalGradient(listOf(DeltaTor.Surface, DeltaTor.SurfaceAlt))
    }
    val borderC = if (filled) DeltaTor.AccentLight.copy(alpha = 0.4f) else DeltaTor.BorderLight
    Box(
        modifier = modifier
            .height(48.dp)
            .clip(shape)
            .background(bg, shape)
            .border(1.dp, borderC, shape)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Text(
            label,
            style = MaterialTheme.typography.titleMedium.copy(
                letterSpacing = 1.2.sp,
                fontWeight = FontWeight.Bold
            ),
            color = if (filled) Color.White else DeltaTor.Text
        )
    }
}

@Composable
private fun StatCard(label: String, value: String, accent: Color, up: Boolean, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        modifier = modifier
            .clip(shape)
            .background(
                Brush.verticalGradient(listOf(DeltaTor.SurfaceAlt, DeltaTor.Surface)),
                shape
            )
            .border(1.dp, DeltaTor.Border, shape)
            .padding(horizontal = 14.dp, vertical = 12.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            ArrowIcon(accent = accent, up = up)
            Spacer(Modifier.width(8.dp))
            Text(
                label,
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Muted
            )
        }
        Spacer(Modifier.height(8.dp))
        Text(
            value,
            style = MaterialTheme.typography.headlineMedium.copy(
                letterSpacing = 0.4.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.Text,
            maxLines = 1
        )
    }
}

@Composable
private fun ArrowIcon(accent: Color, up: Boolean) {
    Canvas(Modifier.size(20.dp)) {
        val stroke = 2.dp.toPx()
        val w = size.width
        val h = size.height
        val tipY = if (up) h * 0.30f else h * 0.70f
        val baseY = if (up) h * 0.68f else h * 0.30f
        val p = Path()
        p.moveTo(w * 0.15f, if (up) h * 0.62f else h * 0.38f)
        p.lineTo(w / 2f, tipY)
        p.lineTo(w * 0.85f, if (up) h * 0.62f else h * 0.38f)
        drawPath(
            p,
            color = accent,
            style = Stroke(width = stroke, cap = StrokeCap.Round, join = StrokeJoin.Round)
        )
        drawLine(
            accent,
            Offset(w / 2f, baseY),
            Offset(w / 2f, if (up) h * 0.78f else h * 0.22f),
            stroke,
            StrokeCap.Round
        )
    }
}

@Composable
private fun BridgeCard(bridges: AppState.BridgeState, sc: Color, onUpdate: () -> Unit) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(shape)
            .background(
                Brush.verticalGradient(listOf(Color(0xFF20242F), DeltaTor.Surface)),
                shape
            )
            .border(1.dp, DeltaTor.Border, shape)
            .padding(horizontal = 16.dp, vertical = 14.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                "BRIDGE STORE",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.6.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Muted
            )
            Spacer(Modifier.weight(1f))
            if (bridges.updating) {
                Spinner(DeltaTor.Amber)
                Spacer(Modifier.width(8.dp))
                Text(
                    "UPDATING \u2026",
                    style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 1.1.sp),
                    color = DeltaTor.Amber
                )
            } else {
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(12.dp))
                        .background(
                            Brush.horizontalGradient(listOf(DeltaTor.AccentDark, DeltaTor.Accent)),
                            RoundedCornerShape(12.dp)
                        )
                        .clickable { onUpdate() }
                        .padding(horizontal = 14.dp, vertical = 6.dp)
                ) {
                    Text(
                        "UPDATE",
                        style = MaterialTheme.typography.labelSmall.copy(
                            letterSpacing = 1.2.sp,
                            fontWeight = FontWeight.Bold
                        ),
                        color = DeltaTor.AccentLight
                    )
                }
            }
        }

        Spacer(Modifier.height(12.dp))

        Row(modifier = Modifier.fillMaxWidth()) {
            StatCell("VANILLA", bridges.vanilla, DeltaTor.Accent, Modifier.weight(1f))
            CellDivider()
            StatCell("OBFS4", bridges.obfs4, DeltaTor.Green, Modifier.weight(1f))
            CellDivider()
            StatCell("WEBTUNNEL", bridges.webtunnel, DeltaTor.Amber, Modifier.weight(1f))
        }

        Spacer(Modifier.height(12.dp))

        val err = bridges.error != null
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(7.dp)
                    .clip(RoundedCornerShape(50))
                    .background(if (err) DeltaTor.Red else sc)
            )
            Spacer(Modifier.width(8.dp))
            Text(
                if (err) "Update failed \u00b7 ${bridges.error}"
                else "Updated ${relativeTime(bridges.lastUpdateMillis)} \u00b7 auto every 24h",
                style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.3.sp),
                color = if (err) DeltaTor.Red else DeltaTor.Muted,
                maxLines = 2
            )
        }
    }
}

@Composable
private fun StatCell(label: String, count: Int, dot: Color, modifier: Modifier = Modifier) {
    Column(
        modifier = modifier,
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(7.dp)
                    .clip(RoundedCornerShape(50))
                    .background(dot)
            )
            Spacer(Modifier.width(7.dp))
            Text(
                "$count",
                style = MaterialTheme.typography.titleLarge.copy(
                    fontSize = 16.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Text
            )
        }
        Spacer(Modifier.height(3.dp))
        Text(
            label,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.Muted
        )
    }
}

@Composable
private fun CellDivider() {
    Box(
        Modifier
            .width(1.dp)
            .height(30.dp)
            .background(DeltaTor.Border)
    )
}

@Composable
private fun Spinner(color: Color) {
    val rotation by rememberInfiniteTransition(label = "spinnerSpin")
        .animateFloat(
            0f, 360f,
            infiniteRepeatable(tween(900, easing = LinearEasing), RepeatMode.Restart),
            label = "s"
        )
    Canvas(
        Modifier
            .size(14.dp)
            .graphicsLayer { rotationZ = rotation }
    ) {
        drawArc(
            color,
            startAngle = 0f,
            sweepAngle = 260f,
            useCenter = false,
            topLeft = Offset(2.dp.toPx(), 2.dp.toPx()),
            size = Size(size.width - 4.dp.toPx(), size.height - 4.dp.toPx()),
            style = Stroke(2.dp.toPx(), cap = StrokeCap.Round)
        )
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