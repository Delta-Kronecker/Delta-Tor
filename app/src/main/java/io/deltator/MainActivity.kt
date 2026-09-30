package io.deltator

import android.Manifest
import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.Easing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.animateFloatAsState
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
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.deltator.tunnel.BridgeStore
import io.deltator.tunnel.ParallelTorManager
import io.deltator.tunnel.ExitCapacityIndex
import io.deltator.tunnel.ExitNodes
import io.deltator.tunnel.TorrcSettings
import io.deltator.ui.DeltaTor
import io.deltator.ui.DeltaTorTheme
import io.deltator.util.AppLog
import io.deltator.util.LogEntry
import io.deltator.util.LogSession
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

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
        ExitNodes.loadDirectory(this)
        setContent {
            DeltaTorTheme {
                DeltaTorScreen(
                    onPrimary = {
                        val s = AppState.state.value
                        when {
                            // The cores are being killed right now; a start here
                            // would race the teardown for the ports.
                            s.stopping -> Unit
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
        if (AppState.state.value.stopping) return
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

private enum class Screen { Main, Log }

@Composable
fun DeltaTorScreen(
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    onUpdateBridges: () -> Unit
) {
    var screen by remember { mutableStateOf(Screen.Main) }

    // Stable navigation callbacks. Written inline they are a new lambda on every
    // recomposition, which makes them unequal params and re-runs the whole screen
    // body each time AppState ticks (the speed counter does, once a second).
    val goMain = remember { { screen = Screen.Main } }
    val goLog = remember { { screen = Screen.Log } }

    Crossfade(targetState = screen, label = "screen") { s ->
        when (s) {
            Screen.Main -> MainScreen(
                onPrimary = onPrimary,
                onStopVpn = onStopVpn,
                onDisconnect = onDisconnect,
                onUpdateBridges = onUpdateBridges,
                onOpenLog = goLog
            )
            Screen.Log -> LogScreen(onBack = goMain)
        }
    }
}

@Composable
private fun MainScreen(
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    onUpdateBridges: () -> Unit,
    onOpenLog: () -> Unit
) {
    val state by AppState.state.collectAsStateWithLifecycle()
    val release by AppState.releaseState.collectAsStateWithLifecycle()
    val sc = stateColor(state)
    val context = LocalContext.current

    val connecting = state.connecting
    val connected = state.connected
    val torRunning = state.torRunning
    val scAnimated by animateColorAsState(sc, tween(450), label = "stateColor")

    val drawerState = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    val openDrawer: () -> Unit = remember { { scope.launch { drawerState.open() }; Unit } }
    val closeDrawer: () -> Unit = remember { { scope.launch { drawerState.close() }; Unit } }
    BackHandler(enabled = drawerState.isOpen) { closeDrawer() }

    ModalNavigationDrawer(
        drawerState = drawerState,
        drawerContent = {
            ModalDrawerSheet(
                drawerContainerColor = Color(0xFF14171F),
                modifier = Modifier.fillMaxSize()
            ) {
                ControlDrawer(
                    onClose = closeDrawer,
                    onUpdateBridges = onUpdateBridges,
                    onOpenLog = { closeDrawer(); onOpenLog() }
                )
            }
        }
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .windowInsetsPadding(WindowInsets.systemBars)
        ) {
            AirBackground(glow = scAnimated, connecting = connecting)

            Header(
                statusColor = scAnimated,
                statusLabel = statusLabel(state),
                onOpenDrawer = openDrawer,
                modifier = Modifier.align(Alignment.TopCenter)
            )

            if (release.newer && release.latestVersion.isNotBlank()) {
                UpdateBanner(
                    version = release.latestVersion,
                    onOpen = { ReleaseChecker.openInBrowser(context, release.latestUrl) },
                    modifier = Modifier
                        .align(Alignment.TopCenter)
                        .offset(y = 98.dp)
                )
            }

            StateBlock(
                connecting = connecting,
                torRunning = torRunning,
                connected = connected,
                reconnecting = state.reconnecting,
                stopping = state.stopping,
                transport = state.transport,
                peak = peakPct(state.connecting, state.torRunning, state.transports),
                sc = scAnimated,
                hasError = state.error != null,
                modifier = Modifier.align(Alignment.Center).offset(y = (-112).dp)
            )

            RingButton(
                modifier = Modifier.align(Alignment.Center),
                ringColor = scAnimated,
                glowColor = if (connecting || connected) scAnimated else null,
                glyphColor = glyphColor(connecting, connected, state.stopping),
                progress = ringProgressOf(state),
                connecting = connecting,
                enabled = !state.stopping,
                onClick = onPrimary
            )

            Text(
                labelText(state),
                modifier = Modifier
                    .align(Alignment.Center)
                    .offset(y = 106.dp)
                    .fillMaxWidth()
                    .padding(horizontal = 32.dp),
                style = MaterialTheme.typography.bodyLarge.copy(letterSpacing = 1.4.sp),
                color = if (state.error != null) DeltaTor.Red else DeltaTor.Muted,
                textAlign = TextAlign.Center
            )

            BottomPanel(
                state = state,
                onPrimary = onPrimary,
                onStopVpn = onStopVpn,
                onDisconnect = onDisconnect,
                modifier = Modifier.align(Alignment.BottomCenter)
            )
        }
    }
}

// ---- location drawer --------------------------------------------------------

@Composable
private fun MenuIcon(modifier: Modifier = Modifier, color: Color = DeltaTor.Text) {
    Canvas(modifier) {
        val stroke = 1.6.dp.toPx()
        val w = size.width
        val h = size.height
        for (i in 0..2) {
            val y = h * (0.22f + i * 0.28f)
            drawLine(color, Offset(w * 0.12f, y), Offset(w * 0.88f, y), stroke, StrokeCap.Round)
        }
    }
}

@Composable
private fun CloseIcon(modifier: Modifier = Modifier, color: Color = DeltaTor.Text) {
    Canvas(modifier) {
        val stroke = 1.6.dp.toPx()
        val p = size.width * 0.24f
        val q = size.width - p
        drawLine(color, Offset(p, p), Offset(q, q), stroke, StrokeCap.Round)
        drawLine(color, Offset(q, p), Offset(p, q), stroke, StrokeCap.Round)
    }
}

@Composable
private fun WarnIcon(modifier: Modifier = Modifier, color: Color = DeltaTor.Amber) {
    Canvas(modifier.size(16.dp)) {
        val w = size.width
        val h = size.height
        val p = Path()
        p.moveTo(w / 2f, h * 0.06f)
        p.lineTo(w * 0.96f, h * 0.92f)
        p.lineTo(w * 0.04f, h * 0.92f)
        p.close()
        drawPath(p, color.copy(alpha = 0.20f))
        drawPath(p, color, style = Stroke(1.3.dp.toPx(), join = StrokeJoin.Round))
        drawLine(
            color, Offset(w / 2f, h * 0.38f), Offset(w / 2f, h * 0.64f),
            1.5.dp.toPx(), StrokeCap.Round
        )
        drawCircle(color, radius = 1.2.dp.toPx(), center = Offset(w / 2f, h * 0.78f))
    }
}

private const val REPO_URL = "https://github.com/Delta-Kronecker/Delta-Tor"

@Composable
private fun ControlDrawer(
    onClose: () -> Unit,
    onUpdateBridges: () -> Unit,
    onOpenLog: () -> Unit
) {
    val uriHandler = LocalUriHandler.current
    val bridges by AppState.bridgeState.collectAsStateWithLifecycle()
    val selectedCodes by ExitNodes.codes.collectAsStateWithLifecycle()
    val exitNames by ExitNodes.names.collectAsStateWithLifecycle()
    val countries by ExitNodes.directory.collectAsStateWithLifecycle()
    val capacity by ExitCapacityIndex.byCountry.collectAsStateWithLifecycle()

    var showCountries by remember { mutableStateOf(false) }
    var showAllCountries by remember { mutableStateOf(false) }
    var showAdvanced by remember { mutableStateOf(false) }
    val advancedForm = remember { AdvancedForm() }
    LaunchedEffect(advancedForm.saved) {
        if (advancedForm.saved) {
            delay(1500)
            advancedForm.saved = false
        }
    }

    val selection = when {
        selectedCodes.isEmpty() -> "Any location \u00b7 default"
        selectedCodes.size == 1 -> {
            val only = selectedCodes.first()
            "${flagEmoji(only)}  ${exitNames[only].orEmpty()}".trim()
        }
        else -> "${selectedCodes.size} countries selected"
    }

    Column(
        Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.systemBars)
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(start = 20.dp, end = 16.dp, top = 18.dp, bottom = 16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text(
                    "CONTROLS",
                    style = MaterialTheme.typography.titleLarge.copy(
                        letterSpacing = 2.4.sp,
                        fontSize = 19.sp,
                        fontWeight = FontWeight.Bold
                    ),
                    color = DeltaTor.Text
                )
                Spacer(Modifier.height(3.dp))
                Text(
                    "Everything here applies on the next connect",
                    style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                    color = DeltaTor.Muted
                )
            }
            Spacer(Modifier.width(12.dp))
            Box(
                Modifier
                    .size(36.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(DeltaTor.Surface, RoundedCornerShape(12.dp))
                    .border(1.dp, DeltaTor.BorderLight, RoundedCornerShape(12.dp))
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null
                    ) { onClose() },
                contentAlignment = Alignment.Center
            ) {
                CloseIcon(Modifier.size(13.dp), DeltaTor.Text)
            }
        }
        DividerLine()

        LazyColumn(
            modifier = Modifier.weight(1f),
            contentPadding = PaddingValues(bottom = 20.dp)
        ) {
            item(key = "loc-head") {
                DrawerSection(
                    title = "LOCATION",
                    summary = selection,
                    expanded = showCountries,
                    onClick = { showCountries = !showCountries }
                )
            }
            if (showCountries) {
                item(key = "loc-warn") {
                    SettingsCard {
                        Column(
                            Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(DeltaTor.Amber.copy(alpha = 0.10f))
                                .padding(horizontal = 12.dp, vertical = 11.dp)
                        ) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                WarnIcon(color = DeltaTor.Amber)
                                Spacer(Modifier.width(8.dp))
                                Text(
                                    "USE ONLY WHEN NEEDED",
                                    style = MaterialTheme.typography.labelSmall.copy(
                                        letterSpacing = 1.2.sp,
                                        fontWeight = FontWeight.Bold
                                    ),
                                    color = DeltaTor.Amber,
                                    modifier = Modifier.weight(1f)
                                )
                                if (selectedCodes.isNotEmpty()) {
                                    Text(
                                        "CLEAR",
                                        style = MaterialTheme.typography.labelSmall.copy(
                                            letterSpacing = 1.2.sp,
                                            fontWeight = FontWeight.Bold
                                        ),
                                        color = DeltaTor.Red,
                                        modifier = Modifier
                                            .clip(RoundedCornerShape(50))
                                            .clickable { ExitNodes.clear() }
                                            .padding(horizontal = 10.dp, vertical = 3.dp)
                                    )
                                }
                            }
                            Spacer(Modifier.height(6.dp))
                            Text(
                                "Picking a country sends your traffic through a relay there. " +
                                    "It can lower your speed and make the connection less stable.",
                                style = MaterialTheme.typography.bodySmall.copy(
                                    letterSpacing = 0.1.sp,
                                    lineHeight = 16.sp
                                ),
                                color = DeltaTor.Muted
                            )
                        }
                    }
                }
                item(key = "any") {
                    DrawerRow(last = countries.isEmpty()) {
                        CountryRow(
                            emoji = "\uD83C\uDF10",
                            name = "Any location \u00b7 default",
                            code = "--",
                            selected = selectedCodes.isEmpty(),
                            onClick = { ExitNodes.clear() }
                        )
                    }
                }
                if (countries.isEmpty()) {
                    item(key = "loading") {
                        Text(
                            "Reading country list \u2026",
                            style = MaterialTheme.typography.bodySmall,
                            color = DeltaTor.Muted.copy(alpha = 0.75f),
                            modifier = Modifier.padding(start = 26.dp, end = 20.dp, top = 12.dp, bottom = 12.dp)
                        )
                    }
                } else {
                    // `directory` already leads with the countries that have
                    // exits, ordered by how much exit bandwidth they hold, so
                    // the split is just where that prefix ends.
                    val known = capacity.isNotEmpty()
                    val withExits = remember(countries, capacity) {
                        if (known) countries.takeWhile { capacity.containsKey(it.code) } else emptyList()
                    }
                    val without = remember(countries, capacity) {
                        if (known) countries.drop(withExits.size) else countries
                    }
                    if (known && withExits.isNotEmpty()) {
                        item(key = "grp-exits") {
                            DrawerGroupHeader(
                                "COUNTRIES WITH RUNNING EXITS",
                                "${withExits.size} of ${countries.size}"
                            )
                        }
                    } else if (!known) {
                        // Nothing is hidden before the relay data arrives, and
                        // nothing claims to be dead either: it just is not known
                        // yet. The list refines itself once the answer lands.
                        item(key = "grp-unknown") {
                            DrawerGroupHeader("COUNTRIES", "EXIT DATA NOT LOADED", muted = true)
                        }
                    }
                    itemsIndexed(withExits, key = { _, c -> "cc-" + c.code }) { i, c ->
                        DrawerRow(last = without.isEmpty() && i == withExits.lastIndex) {
                            CountryRow(
                                emoji = remember(c.code) { flagEmoji(c.code) },
                                name = c.name,
                                code = c.code,
                                selected = c.code in selectedCodes,
                                exits = capacity[c.code]?.exits ?: 0,
                                share = capacity[c.code]?.weight ?: 0f,
                                onClick = { ExitNodes.toggle(c.code, c.name) }
                            )
                        }
                    }
                    if (known && without.isNotEmpty()) {
                        item(key = "grp-rest") {
                            // A country picked before the relay data arrived is in
                            // this group and nowhere else, so the group opens
                            // itself rather than hiding the selection.
                            val holdsSelection = without.any { it.code in selectedCodes }
                            DrawerGroupHeader(
                                "NO RUNNING EXITS \u00b7 PICKING ONE DOES NOTHING",
                                if (showAllCountries) "HIDE" else "SHOW ALL",
                                muted = true,
                                onClick = {
                                    if (holdsSelection) ExitNodes.clear()
                                    showAllCountries = !showAllCountries
                                }
                            )
                        }
                        if (showAllCountries || without.any { it.code in selectedCodes }) {
                            itemsIndexed(without, key = { _, c -> "cc-" + c.code }) { i, c ->
                                DrawerRow(last = i == without.lastIndex) {
                                    CountryRow(
                                        emoji = remember(c.code) { flagEmoji(c.code) },
                                        name = c.name,
                                        code = c.code,
                                        selected = c.code in selectedCodes,
                                        onClick = { ExitNodes.toggle(c.code, c.name) }
                                    )
                                }
                            }
                        }
                    }
                }
            }
            item(key = "div-adv") { DividerLine() }
            item(key = "adv-head") {
                DrawerSection(
                    title = "ADVANCED",
                    summary = "Transport, bridges, torrc and the log",
                    expanded = showAdvanced,
                    onClick = { showAdvanced = !showAdvanced }
                )
            }
            if (showAdvanced) {
                AdvancedItems(
                    form = advancedForm,
                    bridges = bridges,
                    onUpdateBridges = onUpdateBridges,
                    onOpenLog = onOpenLog
                )
            }
        }

        GradientPill(
            modifier = Modifier
                .fillMaxWidth()
                .padding(start = 20.dp, end = 20.dp, top = 4.dp, bottom = 16.dp),
            label = "GITHUB",
            filled = true,
            onClick = { runCatching { uriHandler.openUri(REPO_URL) } }
        )
    }
}

/**
 * An infinite animated value that only exists while [active].
 *
 * `rememberInfiniteTransition` keeps asking the frame clock for frames from the
 * moment it is created, whether or not anything reads its value. So a ring that
 * spins while connecting, left created in a state that is not connecting, does
 * not idle: it redraws itself sixty times a second to animate something no
 * branch of the draw ever looks at. A VPN that is connected and left open is
 * exactly that situation.
 *
 * Handed back as a plain state so callers read it the same way in both cases,
 * and resting at [resting] instead of jumping to a value the loop would have
 * passed through.
 */
@Composable
private fun rememberActiveLoop(
    active: Boolean,
    label: String,
    from: Float,
    to: Float,
    durationMillis: Int,
    easing: Easing,
    restart: Boolean = false,
    resting: Float = from
): State<Float> =
    if (active) {
        rememberInfiniteTransition(label = label).animateFloat(
            from, to,
            infiniteRepeatable(
                tween(durationMillis, easing = easing),
                if (restart) RepeatMode.Restart else RepeatMode.Reverse
            ),
            label = label
        )
    } else {
        remember(label, resting) { mutableStateOf(resting) }
    }

/**
 * An on/off control drawn out of the same parts as the chips and the checkbox,
 * so it does not bring a Material switch into a drawer that draws everything
 * else itself.
 */
@Composable
private fun ToggleSwitch(checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    val shift by animateDpAsState(
        targetValue = if (checked) 22.dp else 4.dp,
        animationSpec = tween(160),
        label = "knob"
    )
    Box(
        modifier = Modifier
            .size(width = 46.dp, height = 26.dp)
            .clip(RoundedCornerShape(50))
            .background(
                if (checked) DeltaTor.Accent else DeltaTor.SurfaceAlt,
                RoundedCornerShape(50)
            )
            .border(
                1.dp,
                if (checked) DeltaTor.AccentLight.copy(alpha = 0.4f) else DeltaTor.BorderLight,
                RoundedCornerShape(50)
            )
            .clickable { onCheckedChange(!checked) }
    ) {
        Box(
            Modifier
                .offset(x = shift)
                .padding(top = 3.dp)
                .size(18.dp)
                .clip(RoundedCornerShape(50))
                .background(if (checked) Color.White else DeltaTor.Muted)
        )
    }
}

@Composable
private fun DrawerSection(
    title: String,
    summary: String,
    expanded: Boolean? = null,
    onClick: (() -> Unit)? = null
) {
    val turn by animateFloatAsState(
        targetValue = if (expanded == true) 180f else 0f,
        animationSpec = tween(200),
        label = "chevron"
    )
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier)
            .padding(start = 20.dp, end = 20.dp, top = 18.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                title,
                style = MaterialTheme.typography.titleSmall.copy(
                    letterSpacing = 2.sp,
                    fontSize = 15.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Text
            )
            Spacer(Modifier.height(4.dp))
            Text(
                summary,
                style = MaterialTheme.typography.bodyMedium.copy(letterSpacing = 0.2.sp),
                color = DeltaTor.Muted,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
        }
        if (onClick != null) {
            Spacer(Modifier.width(12.dp))
            Text(
                if (expanded == true) "HIDE" else "SHOW",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = if (expanded == true) DeltaTor.AccentLight else DeltaTor.Muted
            )
            Spacer(Modifier.width(8.dp))
            ChevronIcon(Modifier.size(12.dp), DeltaTor.AccentLight, turn)
        }
    }
}

@Composable
private fun ChevronIcon(modifier: Modifier = Modifier, color: Color = DeltaTor.Text, turn: Float = 0f) {
    Canvas(modifier.graphicsLayer(rotationZ = turn)) {
        val stroke = 1.6.dp.toPx()
        val p = Path()
        p.moveTo(size.width * 0.16f, size.height * 0.36f)
        p.lineTo(size.width * 0.5f, size.height * 0.70f)
        p.lineTo(size.width * 0.84f, size.height * 0.36f)
        drawPath(p, color, style = Stroke(stroke, cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}

// ---- state helpers ----------------------------------------------------------

private fun stateColor(state: AppState.VpnState): Color = when {
    state.stopping -> DeltaTor.Amber
    state.connecting -> DeltaTor.Amber
    state.reconnecting -> DeltaTor.AmberLight
    state.connected -> DeltaTor.Green
    state.torRunning -> DeltaTor.Amber
    else -> DeltaTor.Muted
}

private fun statusLabel(state: AppState.VpnState): String = when {
    state.stopping -> "STOPPING"
    state.connecting -> "CONNECTING"
    state.connected -> "CONNECTED"
    state.torRunning -> "READY"
    else -> "OFFLINE"
}

private fun glyphColor(connecting: Boolean, connected: Boolean, stopping: Boolean): Color = when {
    stopping -> DeltaTor.Amber
    connecting -> DeltaTor.Amber
    connected -> DeltaTor.Green
    else -> DeltaTor.Text
}

private fun labelText(state: AppState.VpnState): String = when {
    state.error != null -> state.error
    state.stopping -> "STOPPING"
    state.connecting -> "CANCEL"
    state.connected -> "DISCONNECT"
    state.torRunning -> "START VPN"
    else -> "CONNECT"
}

private fun wordFor(
    connecting: Boolean,
    torRunning: Boolean,
    connected: Boolean,
    reconnecting: Boolean,
    stopping: Boolean,
    hasError: Boolean
): String = when {
    hasError -> "ERROR"
    stopping -> "STOPPING"
    connecting -> "CONNECTING"
    reconnecting -> "LINK LOST"
    connected -> "CONNECTED"
    torRunning -> "READY"
    else -> "OFFLINE"
}

private fun sublineFor(
    connecting: Boolean,
    torRunning: Boolean,
    connected: Boolean,
    reconnecting: Boolean,
    stopping: Boolean,
    transport: String,
    peak: Int,
    hasError: Boolean
): String = when {
    hasError -> "BOOTSTRAP FAILED"
    stopping -> "KILLING EVERY TOR CORE"
    connecting -> "TUNNEL BOOTSTRAPPING \u00b7 $peak%"
    reconnecting -> "RESTORING THE TUNNEL"
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
private fun AirBackground(glow: Color, connecting: Boolean) {
    // Read outside the draw lambda on purpose. It used to be read inside, which
    // is what made this the most expensive thing on the screen: a full-screen
    // Canvas that redrew three large radial gradients every frame, forever,
    // including all the time spent connected to something and not connecting.
    // The ring and the status line already carry the sense of something running.
    val pulse by rememberActiveLoop(
        active = connecting,
        label = "bgpulse",
        from = 0.5f,
        to = 1f,
        durationMillis = 2600,
        easing = FastOutSlowInEasing
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
private fun Header(
    statusColor: Color,
    statusLabel: String,
    onOpenDrawer: () -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 14.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(34.dp)
                    .clip(RoundedCornerShape(11.dp))
                    .background(DeltaTor.Surface, RoundedCornerShape(11.dp))
                    .border(1.dp, DeltaTor.BorderLight, RoundedCornerShape(11.dp))
                    .clickable(
                        interactionSource = remember { MutableInteractionSource() },
                        indication = null
                    ) { onOpenDrawer() },
                contentAlignment = Alignment.Center
            ) {
                MenuIcon(Modifier.size(15.dp), DeltaTor.AccentLight)
            }
            Spacer(Modifier.width(11.dp))
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
    reconnecting: Boolean,
    stopping: Boolean,
    transport: String,
    peak: Int,
    sc: Color,
    hasError: Boolean,
    modifier: Modifier = Modifier
) {
    val word = wordFor(connecting, torRunning, connected, reconnecting, stopping, hasError)
    val sub = sublineFor(connecting, torRunning, connected, reconnecting, stopping, transport, peak, hasError)
    val subColor by animateColorAsState(
        when {
            stopping || reconnecting -> DeltaTor.AmberLight
            connected -> DeltaTor.GreenLight
            else -> DeltaTor.Muted
        },
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
    enabled: Boolean = true,
    onClick: () -> Unit
) {
    // The beam only ever appears while connecting and the scale only breathes
    // while connecting, so neither loop is worth running the rest of the time.
    val rotation by rememberActiveLoop(
        active = connecting,
        label = "ringSpin",
        from = 0f,
        to = 360f,
        durationMillis = 9000,
        easing = LinearEasing,
        restart = true
    )
    val breathe by rememberActiveLoop(
        active = connecting,
        label = "ringBreath",
        from = 1f,
        to = 1.035f,
        durationMillis = 2400,
        easing = FastOutSlowInEasing
    )
    // The halo stays lit while connected, so it keeps breathing, but a
    // connection at rest sits at the middle of the range rather than moving
    // between the two ends forever.
    val pulse by rememberActiveLoop(
        active = glowColor != null,
        label = "ringPulse",
        from = 0.55f,
        to = 1f,
        durationMillis = 2200,
        easing = FastOutSlowInEasing,
        resting = 0.78f
    )
    val scale = if (connecting) breathe else 1f

    Box(
        modifier = modifier
            .size(152.dp)
            .graphicsLayer { scaleX = scale; scaleY = scale }
            .clickable(
                enabled = enabled,
                interactionSource = remember { MutableInteractionSource() },
                indication = null
            ) { onClick() },
        contentAlignment = Alignment.Center
    ) {
        Canvas(Modifier.fillMaxSize()) {
            val c = center
            val r = size.minDimension / 2f
            val halo = 130.dp.toPx()

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
    onPrimary: () -> Unit,
    onStopVpn: () -> Unit,
    onDisconnect: () -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(start = 20.dp, end = 20.dp, top = 4.dp, bottom = 12.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        when {
            // Nothing may be started while the cores are dying, so the row of
            // actions is replaced by the phase itself.
            state.stopping -> GradientPill(
                modifier = Modifier.fillMaxWidth(),
                label = "STOPPING \u00b7 KILLING EVERY TOR CORE",
                filled = false,
                enabled = false,
                onClick = {}
            )
            state.connected -> Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                GradientPill(
                    modifier = Modifier.weight(1f),
                    label = "STOP VPN",
                    filled = false,
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
                    filled = false,
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
            Spacer(Modifier.height(8.dp))
        }
        // Live speed on the home screen itself, not only in the notification.
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StatCard(
                label = "SPEED DOWN",
                value = if (state.connected) "${formatBytes(state.rxSpeed.toLong())}/s" else "--",
                accent = DeltaTor.Green,
                up = false,
                modifier = Modifier.weight(1f)
            )
            StatCard(
                label = "SPEED UP",
                value = if (state.connected) "${formatBytes(state.txSpeed.toLong())}/s" else "--",
                accent = DeltaTor.Accent,
                up = true,
                modifier = Modifier.weight(1f)
            )
        }

        Spacer(Modifier.height(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StatCard(
                label = "DOWNLOADED",
                value = formatBytes(state.rxBytes),
                accent = DeltaTor.Green,
                up = false,
                modifier = Modifier.weight(1f)
            )
            StatCard(
                label = "UPLOADED",
                value = formatBytes(state.txBytes),
                accent = DeltaTor.Accent,
                up = true,
                modifier = Modifier.weight(1f)
            )
        }

        Spacer(Modifier.height(8.dp))

        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            InfoPill(
                label = "UP TIME",
                value = if (state.connected) formatDuration(System.currentTimeMillis() - state.connectedAtMillis) else "--",
                modifier = Modifier.weight(1f)
            )
            InfoPill(
                label = "EXIT",
                value = when {
                    state.exitCode.isNotBlank() ->
                        "${flagEmoji(state.exitCode)} ${state.exitName}".trim()
                    state.connected -> "Locating \u2026"
                    else -> "--"
                },
                modifier = Modifier.weight(1f)
            )
        }

        Spacer(Modifier.height(9.dp))
    }
}

@Composable
private fun GradientPill(
    modifier: Modifier = Modifier,
    label: String,
    filled: Boolean,
    enabled: Boolean = true,
    labelSize: TextUnit = 14.sp,
    onClick: () -> Unit
) {
    val shape = RoundedCornerShape(20.dp)
    val bg = if (filled) {
        Brush.horizontalGradient(listOf(DeltaTor.AccentSoft, DeltaTor.Accent))
    } else {
        Brush.horizontalGradient(listOf(DeltaTor.Surface, DeltaTor.SurfaceAlt))
    }
    val borderC = if (filled) DeltaTor.AccentLight.copy(alpha = 0.4f) else DeltaTor.BorderLight
    Box(
        modifier = modifier
            .height(40.dp)
            .clip(shape)
            .background(bg, shape)
            .border(1.dp, borderC, shape)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelLarge.copy(
                fontSize = labelSize,
                letterSpacing = 1.2.sp,
                fontWeight = FontWeight.Bold
            ),
            color = if (filled) Color.White else DeltaTor.Text,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

@Composable
private fun StatCard(label: String, value: String, accent: Color, up: Boolean, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(14.dp)
    Column(
        modifier = modifier
            .clip(shape)
            .background(
                Brush.verticalGradient(listOf(DeltaTor.SurfaceAlt, DeltaTor.Surface)),
                shape
            )
            .border(1.dp, DeltaTor.Border, shape)
            .padding(horizontal = 12.dp, vertical = 8.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            ArrowIcon(accent = accent, up = up, iconSize = 15.dp)
            Spacer(Modifier.width(7.dp))
            Text(
                label,
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Muted
            )
        }
        Spacer(Modifier.height(4.dp))
        Text(
            value,
            style = MaterialTheme.typography.titleLarge.copy(
                letterSpacing = 0.4.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.Text,
            maxLines = 1
        )
    }
}

@Composable
private fun InfoPill(label: String, value: String, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(12.dp)
    Column(
        modifier = modifier
            .clip(shape)
            .background(DeltaTor.Surface, shape)
            .border(1.dp, DeltaTor.BorderLight, shape)
            .padding(horizontal = 12.dp, vertical = 7.dp)
    ) {
        Text(
            label,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.2.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.Muted
        )
        Spacer(Modifier.height(3.dp))
        Text(
            value,
            style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold),
            color = DeltaTor.Text,
            maxLines = 1
        )
    }
}

@Composable
private fun ArrowIcon(accent: Color, up: Boolean, iconSize: Dp = 20.dp) {
    Canvas(Modifier.size(iconSize)) {
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
private fun BridgeCard(
    bridges: AppState.BridgeState,
    sc: Color,
    onUpdate: () -> Unit,
    modifier: Modifier = Modifier
) {
    val shape = RoundedCornerShape(18.dp)
    Column(
        modifier = modifier
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

private fun formatDuration(ms: Long): String {
    val total = (ms / 1000).coerceAtLeast(0)
    val h = total / 3600
    val m = (total % 3600) / 60
    val s = total % 60
    fun pad(n: Long) = n.toString().padStart(2, '0')
    return if (h > 0) "$h:${pad(m)}:${pad(s)}" else "${pad(m)}:${pad(s)}"
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

// ---- settings & log screens -------------------------------------------------

@Composable
private fun BackArrowIcon(modifier: Modifier = Modifier, color: Color = DeltaTor.Text) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = 2.dp.toPx()
        val p = Path()
        p.moveTo(w * 0.64f, h * 0.16f)
        p.lineTo(w * 0.30f, h * 0.5f)
        p.lineTo(w * 0.64f, h * 0.84f)
        drawPath(p, color, style = Stroke(stroke, cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}

@Composable
private fun ScreenTopBar(
    title: String,
    onBack: () -> Unit,
    action: (@Composable () -> Unit)? = null
) {
    Column {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 20.dp, vertical = 16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Box(
                modifier = Modifier
                    .size(38.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(DeltaTor.Surface, RoundedCornerShape(12.dp))
                    .border(1.dp, DeltaTor.BorderLight, RoundedCornerShape(12.dp))
                    .clickable { onBack() },
                contentAlignment = Alignment.Center
            ) {
                BackArrowIcon(Modifier.size(18.dp))
            }
            Spacer(Modifier.width(14.dp))
            Text(
                title,
                style = MaterialTheme.typography.titleMedium.copy(
                    letterSpacing = 1.6.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Text
            )
            Spacer(Modifier.weight(1f))
            action?.invoke()
        }
        DividerLine()
    }
}

@Composable
private fun UpdateBanner(
    version: String,
    onOpen: () -> Unit,
    modifier: Modifier = Modifier
) {
    val shape = RoundedCornerShape(16.dp)
    Column(
        modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp)
            .clip(shape)
            .background(
                Brush.horizontalGradient(listOf(Color(0xFF2A2140), DeltaTor.Surface)),
                shape
            )
            .border(1.dp, DeltaTor.Accent.copy(alpha = 0.45f), shape)
            .clickable { onOpen() }
            .padding(horizontal = 14.dp, vertical = 12.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(9.dp)
                    .clip(RoundedCornerShape(50))
                    .background(DeltaTor.Green)
            )
            Spacer(Modifier.width(8.dp))
            Text(
                "NEW RELEASE v$version",
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.3.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.GreenLight
            )
        }
        Spacer(Modifier.height(5.dp))
        Text(
            "An update is available \u00b7 tap anywhere to open on GitHub",
            style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
            color = DeltaTor.Muted
        )
    }
}

@Composable
private fun DividerLine() {
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

@Composable
private fun DrawerRow(last: Boolean = false, content: @Composable () -> Unit) {
    Column(
        Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp)
    ) {
        content()
        if (!last) {
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(1.dp)
                    .background(DeltaTor.BorderLight.copy(alpha = 0.6f))
            )
        }
    }
}

@Composable
private fun SettingsCardHeader(
    title: String,
    subtitle: String,
    trailing: (@Composable () -> Unit)? = null
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = 20.dp, end = 20.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                title,
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.6.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Muted
            )
            Spacer(Modifier.height(2.dp))
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                color = DeltaTor.Muted.copy(alpha = 0.75f)
            )
        }
        trailing?.invoke()
    }
}


@Composable
private fun DrawerGroupHeader(
    title: String,
    trailing: String,
    muted: Boolean = false,
    onClick: (() -> Unit)? = null
) {
    val color = if (muted) DeltaTor.Muted.copy(alpha = 0.6f) else DeltaTor.Muted
    val action = onClick
    val clickable = if (action != null) Modifier.clickable { action() } else Modifier
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .then(clickable)
            .padding(start = 26.dp, end = 20.dp, top = 14.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            title,
            style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 1.2.sp),
            color = color,
            modifier = Modifier.weight(1f)
        )
        if (action != null) {
            Text(
                trailing,
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.2.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.AccentLight
            )
        } else {
            Text(
                trailing,
                style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 1.2.sp),
                color = color.copy(alpha = 0.7f)
            )
        }
    }
}

@Composable
private fun CountryRow(
    emoji: String,
    name: String,
    code: String,
    selected: Boolean,
    exits: Int = 0,
    share: Float = 0f,
    onClick: () -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(if (selected) DeltaTor.Accent.copy(alpha = 0.12f) else Color.Transparent)
            .clickable { onClick() }
            .padding(start = 8.dp, end = 8.dp, top = 10.dp, bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(emoji, fontSize = 19.sp)
        Spacer(Modifier.width(12.dp))
        Text(
            name,
            style = MaterialTheme.typography.bodyMedium.copy(
                fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal
            ),
            color = if (selected) DeltaTor.Text else DeltaTor.Muted,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f)
        )
        if (exits > 0) {
            // The share is the honest number and the count is the memorable
            // one, so both are here: a country can have many exits that are all
            // slow, or few that are quick.
            Spacer(Modifier.width(10.dp))
            Text(
                "${(share * 100).format1()}%",
                style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 0.4.sp),
                color = if (selected) DeltaTor.AccentLight else DeltaTor.Muted.copy(alpha = 0.85f)
            )
            Spacer(Modifier.width(8.dp))
            Text(
                "$exits",
                style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 0.4.sp),
                color = DeltaTor.Muted.copy(alpha = 0.55f)
            )
        }
        Spacer(Modifier.width(10.dp))
        Text(
            code,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.sp,
                color = DeltaTor.Muted.copy(alpha = 0.8f)
            )
        )
        Spacer(Modifier.width(10.dp))
        Box(
            Modifier
                .size(16.dp)
                .clip(RoundedCornerShape(50))
                .background(if (selected) DeltaTor.AccentLight else Color.Transparent)
                .border(
                    1.dp,
                    if (selected) DeltaTor.AccentLight else DeltaTor.Border,
                    RoundedCornerShape(50)
                ),
            contentAlignment = Alignment.Center
        ) {
            if (selected) {
                CheckIcon(Modifier.size(10.dp))
            }
        }
    }
}

@Composable
private fun CheckIcon(modifier: Modifier = Modifier, color: Color = Color(0xFF14171F)) {
    Canvas(modifier) {
        val stroke = 1.7.dp.toPx()
        val p = Path()
        p.moveTo(size.width * 0.14f, size.height * 0.52f)
        p.lineTo(size.width * 0.40f, size.height * 0.78f)
        p.lineTo(size.width * 0.86f, size.height * 0.22f)
        drawPath(p, color, style = Stroke(stroke, cap = StrokeCap.Round, join = StrokeJoin.Round))
    }
}

/** "9.0", "0.3" \u00b7 one decimal, never "9", so columns line up. */
private fun Float.format1(): String {
    val v = (this * 10f).toInt()
    return "${v / 10}.${v % 10}"
}

private fun flagEmoji(code: String): String {
    if (code.length != 2) return "\uD83C\uDF10"
    val sb = StringBuilder()
    for (c in code.uppercase()) {
        sb.appendCodePoint(0x1F1E6 + (c - 'A'))
    }
    return sb.toString()
}

@Composable
private fun SettingsCard(content: @Composable ColumnScope.() -> Unit) {    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp)
            .clip(RoundedCornerShape(18.dp))
            .background(
                Brush.verticalGradient(listOf(Color(0xFF20242F), DeltaTor.Surface)),
                RoundedCornerShape(18.dp)
            )
            .border(1.dp, DeltaTor.Border, RoundedCornerShape(18.dp))
            .padding(horizontal = 16.dp, vertical = 8.dp),
        content = content
    )
}

/**
 * The advanced section's editable state.
 *
 * Hoisted out of the body on purpose. The body is one lazy item per card now,
 * and a `remember` written inside an item does not survive that item scrolling
 * off the screen and back: a ticked auto-racer or a half-typed torrc would be
 * gone by the time you scrolled up to it again.
 */
private class AdvancedForm {
    var templateText by mutableStateOf(TorrcSettings.template())
    var saved by mutableStateOf(false)
    var transportMode by mutableStateOf(Config.transportMode)
    var customBridges by mutableStateOf(Config.customBridges)
    var autoTransports by mutableStateOf(Config.autoTransports)
    var loggingOn by mutableStateOf(Config.loggingEnabled)
}

@Composable
private fun AdvancedFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedBorderColor = DeltaTor.AccentLight,
    unfocusedBorderColor = DeltaTor.BorderLight,
    focusedContainerColor = DeltaTor.Surface,
    unfocusedContainerColor = DeltaTor.Surface,
    cursorColor = DeltaTor.AccentLight,
    focusedTextColor = DeltaTor.Text,
    unfocusedTextColor = DeltaTor.Text,
    focusedPlaceholderColor = DeltaTor.Muted,
    unfocusedPlaceholderColor = DeltaTor.Muted
)

/**
 * The advanced section, as one lazy item per card.
 *
 * It was a single tall column inside a single item, so opening the section built
 * all of it in one frame: two dropdowns, two text fields and a bridge card. The
 * torrc field alone holds thirty lines, and the first text field composed
 * anywhere in the process makes Android stand up its entire text input stack,
 * which is what made opening this one feel like it stalled. Split per card, only
 * what is on screen gets built, and the text fields are not built at all until
 * they are scrolled to.
 */
private fun LazyListScope.AdvancedItems(
    form: AdvancedForm,
    bridges: AppState.BridgeState,
    onUpdateBridges: () -> Unit,
    onOpenLog: () -> Unit
) {
    item(key = "adv-transport") {
        Column(Modifier.fillMaxWidth()) {
            SettingsCardHeader("TRANSPORT", "Which way Tor connects \u00b7 applied on next connect")
            SettingsCard {
                val modes = listOf(
                    ParallelTorManager.TRANSPORT_AUTO to "Auto \u00b7 race all",
                    ParallelTorManager.TRANSPORT_VANILLA to "Vanilla \u00b7 plain bridges",
                    ParallelTorManager.TRANSPORT_OBFS4 to "obfs4 \u00b7 obfuscated",
                    ParallelTorManager.TRANSPORT_WEBTUNNEL to "WebTunnel \u00b7 needs IPv6",
                    ParallelTorManager.TRANSPORT_SNOWFLAKE to "Snowflake \u00b7 the two bundled bridges",
                    ParallelTorManager.TRANSPORT_DIRECT to "Direct \u00b7 no bridge at all",
                    ParallelTorManager.TRANSPORT_CUSTOM to "Custom \u00b7 my own bridge lines"
                )
                SettingsDropdown(
                    label = "CONNECT VIA",
                    value = form.transportMode,
                    options = modes,
                    onSelect = {
                        form.transportMode = it
                        Config.transportMode = it
                    }
                )
                Text(
                    when (form.transportMode) {
                        ParallelTorManager.TRANSPORT_AUTO ->
                            "Races vanilla, obfs4, webtunnel and the previously working bridges at the same time."
                        ParallelTorManager.TRANSPORT_VANILLA -> "Plain bridges, no pluggable transport."
                        ParallelTorManager.TRANSPORT_OBFS4 -> "obfs4 only, via lyrebird."
                        ParallelTorManager.TRANSPORT_WEBTUNNEL -> "webtunnel only, via lyrebird. Needs IPv6."
                        ParallelTorManager.TRANSPORT_SNOWFLAKE ->
                            "Snowflake only, via lyrebird. Uses the bundled two bridges."
                        ParallelTorManager.TRANSPORT_DIRECT ->
                            "No bridges at all \u2014 connects straight to a guard."
                        else -> "Uses only the bridge lines you paste below."
                    },
                    style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                    color = DeltaTor.Muted,
                    modifier = Modifier.padding(top = 8.dp, bottom = 2.dp)
                )
                if (form.transportMode in ParallelTorManager.BRIDGE_SOURCES) {
                    // Single-transport modes are not single-runner modes: the
                    // memory twin races next to them, so say that up front.
                    Text(
                        "Every bridge list also gets a memory twin: the bridges " +
                            "that worked in this transport are pulled from the log and " +
                            "race beside it, e.g. ${form.transportMode}-memory.",
                        style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                        color = DeltaTor.Muted.copy(alpha = 0.85f),
                        modifier = Modifier.padding(top = 6.dp, bottom = 2.dp)
                    )
                }
                }
        }
    }
    if (form.transportMode == ParallelTorManager.TRANSPORT_AUTO) {
        item(key = "adv-racers") {
            Column(Modifier.fillMaxWidth()) {
                SettingsCardHeader("AUTO RACERS", "What auto races \u00b7 at least one")
                SettingsCard {
                    Config.AUTO_TRANSPORT_CHOICES.forEach { choice ->
                        val on = choice in form.autoTransports
                        val only = on && form.autoTransports.size == 1
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .clip(RoundedCornerShape(10.dp))
                                .clickable(enabled = !only) {
                                    form.autoTransports = when {
                                        on -> form.autoTransports - choice
                                        else -> form.autoTransports + choice
                                    }
                                    Config.autoTransports = form.autoTransports
                                }
                                .padding(horizontal = 6.dp, vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            Box(
                                Modifier
                                    .size(18.dp)
                                    .clip(RoundedCornerShape(5.dp))
                                    .background(
                                        if (on) DeltaTor.Accent else Color.Transparent
                                    )
                                    .border(
                                        1.dp,
                                        if (on) DeltaTor.Accent else DeltaTor.BorderLight,
                                        RoundedCornerShape(5.dp)
                                    ),
                                contentAlignment = Alignment.Center
                            ) {
                                if (on) {
                                    Text(
                                        "\u2713",
                                        fontSize = 11.sp,
                                        color = Color.White
                                    )
                                }
                            }
                            Spacer(Modifier.width(12.dp))
                            Text(
                                choice.uppercase(),
                                style = MaterialTheme.typography.labelSmall.copy(
                                    letterSpacing = 1.2.sp,
                                    fontWeight = FontWeight.Bold
                                ),
                                color = if (on) DeltaTor.Text else DeltaTor.Muted,
                                modifier = Modifier.weight(1f)
                            )
                            if (only) {
                                Text(
                                    "only one left",
                                    style = MaterialTheme.typography.labelSmall.copy(
                                        letterSpacing = 0.6.sp
                                    ),
                                    color = DeltaTor.Amber
                                )
                            }
                        }
                    }
                    Text(
                        "Auto starts every ticked transport at once and keeps the first that reaches 100%.",
                        style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                        color = DeltaTor.Muted,
                        modifier = Modifier.padding(top = 6.dp)
                    )
                }
            }
        }
    }
    if (form.transportMode == ParallelTorManager.TRANSPORT_CUSTOM) {
        item(key = "adv-custom") {
            Column(Modifier.fillMaxWidth()) {
                SettingsCardHeader("CUSTOM BRIDGES", "One bridge per line \u00b7 applied on next connect")
                SettingsCard {
                    OutlinedTextField(
                        value = form.customBridges,
                        onValueChange = {
                            form.customBridges = it
                            Config.customBridges = it
                        },
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(vertical = 4.dp),
                        minLines = 4,
                        maxLines = 12,
                        textStyle = TextStyle(
                            fontFamily = FontFamily.Monospace,
                            fontSize = 10.sp,
                            color = DeltaTor.Text
                        ),
                        colors = AdvancedFieldColors(),
                        placeholder = {
                            Text(
                                "snowflake 192.0.2.3:80 FINGERPRINT url=... \nobfs4 1.2.3.4:443 FINGERPRINT cert=...",
                                fontFamily = FontFamily.Monospace,
                                fontSize = 10.sp
                            )
                        }
                    )
                    val customCount = form.customBridges.lines().count {
                        it.isNotBlank() && !it.trim().startsWith("#")
                    }
                    Text(
                        if (customCount == 0) "No bridges yet \u2014 paste at least one line."
                        else "$customCount bridge line(s) form.saved",
                        style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 1.1.sp),
                        color = if (customCount == 0) DeltaTor.Amber else DeltaTor.Green,
                        modifier = Modifier.padding(top = 4.dp)
                    )
                }
            }
        }
    }
    item(key = "adv-torrc") {
        Column(Modifier.fillMaxWidth()) {
            SettingsCardHeader("TORRC TEMPLATE", "The full torrc, editable here") {
                Text(
                    "RESET",
                    style = MaterialTheme.typography.labelSmall.copy(
                        letterSpacing = 1.1.sp,
                        fontWeight = FontWeight.Bold
                    ),
                    color = DeltaTor.AccentLight,
                    modifier = Modifier
                        .clip(RoundedCornerShape(8.dp))
                        .clickable {
                            TorrcSettings.resetTemplate()
                            form.templateText = TorrcSettings.template()
                        }
                        .padding(horizontal = 8.dp, vertical = 4.dp)
                )
            }
            SettingsCard {
                Text(
                    "A ready-made torrc template. Bridges and pluggable transports are appended automatically \u00b7 applied on next connect.",
                    style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                    color = DeltaTor.Muted,
                    modifier = Modifier.padding(vertical = 4.dp)
                )
                OutlinedTextField(
                    value = form.templateText,
                    onValueChange = { form.templateText = it },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(vertical = 4.dp),
                    minLines = 10,
                    maxLines = 18,
                    textStyle = TextStyle(
                        fontFamily = FontFamily.Monospace,
                        fontSize = 10.sp,
                        lineHeight = 13.sp,
                        color = DeltaTor.Text
                    ),
                    colors = AdvancedFieldColors()
                )
                Spacer(Modifier.height(6.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        if (form.saved) "SAVED \u2713" else "One directive per line \u00b7 lines starting with # are ignored",
                        style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                        color = if (form.saved) DeltaTor.GreenLight else DeltaTor.Muted,
                        modifier = Modifier.weight(1f)
                    )
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(12.dp))
                            .background(
                                Brush.horizontalGradient(listOf(DeltaTor.AccentDark, DeltaTor.Accent)),
                                RoundedCornerShape(12.dp)
                            )
                            .clickable {
                                TorrcSettings.setTemplate(form.templateText)
                                form.saved = true
                            }
                            .padding(horizontal = 18.dp, vertical = 8.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            "SAVE",
                            style = MaterialTheme.typography.labelSmall.copy(
                                letterSpacing = 1.2.sp,
                                fontWeight = FontWeight.Bold
                            ),
                            color = Color.White
                        )
                    }
                }
            }
            Spacer(Modifier.height(20.dp))
        }
    }
    item(key = "adv-bridges") {
        Column(Modifier.fillMaxWidth()) {
            SettingsCardHeader("BRIDGES", "Bridge mirror counts \u00b7 live cache")
            Spacer(Modifier.height(6.dp))
            BridgeCard(
                bridges = bridges,
                sc = DeltaTor.Accent,
                onUpdate = onUpdateBridges,
                modifier = Modifier.padding(horizontal = 20.dp)
            )
            Spacer(Modifier.height(20.dp))
        }
    }
    item(key = "adv-log") {
        Column(Modifier.fillMaxWidth()) {
            SettingsCardHeader("CONNECTION LOG", "Exact Tor bootstrap output \u00b7 copy with one tap")
            SettingsCard {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(64.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            "View connection log",
                            style = MaterialTheme.typography.bodyLarge.copy(
                                fontWeight = FontWeight.SemiBold,
                                letterSpacing = 0.3.sp
                            ),
                            color = DeltaTor.Text
                        )
                        Spacer(Modifier.height(3.dp))
                        Text(
                            "Shows the live bootstrap; press COPY to share it.",
                            style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                            color = DeltaTor.Muted
                        )
                    }
                    Box(
                        modifier = Modifier
                            .clip(RoundedCornerShape(12.dp))
                            .background(
                                Brush.horizontalGradient(
                                    listOf(DeltaTor.AccentDark, DeltaTor.Accent)
                                ),
                                RoundedCornerShape(12.dp)
                            )
                            .clickable { onOpenLog() }
                            .padding(horizontal = 16.dp, vertical = 10.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            "VIEW LOG",
                            style = MaterialTheme.typography.labelSmall.copy(
                                letterSpacing = 1.2.sp,
                                fontWeight = FontWeight.Bold
                            ),
                            color = Color.White
                        )
                    }
                }
                DividerLine()
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(top = 10.dp, bottom = 2.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(
                            "Record the log",
                            style = MaterialTheme.typography.bodyLarge.copy(
                                fontWeight = FontWeight.SemiBold,
                                letterSpacing = 0.3.sp
                            ),
                            color = DeltaTor.Text
                        )
                        Spacer(Modifier.height(3.dp))
                        Text(
                            if (form.loggingOn) {
                                "Keeps every Tor line of every connect. Turn it off to stop " +
                                    "recording; the reason for a failure still shows on the screen."
                            } else {
                                "Nothing is being recorded. The bootstrap still runs \u2014 it just " +
                                    "is not kept, so a later log has nothing in it."
                            },
                            style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.2.sp),
                            color = DeltaTor.Muted
                        )
                    }
                    Spacer(Modifier.width(12.dp))
                    ToggleSwitch(
                        checked = form.loggingOn,
                        onCheckedChange = {
                            form.loggingOn = it
                            Config.loggingEnabled = it
                            AppLog.enabled = it
                        }
                    )
                }
            }
            Spacer(Modifier.height(20.dp))
        }
    }
}

private fun levelLabel(level: Char): String = when (level) {
    'E' -> "ERRORS"
    'W' -> "WARNINGS"
    'I' -> "INFO"
    'D' -> "DEBUG"
    else -> "VERBOSE"
}

private const val LOG_SEVERITY_ORDER = "EWIDV"

private sealed interface LogNode {
    data class SessionHeader(val id: Int, val title: String, val count: Int) : LogNode
    data class Header(val level: Char, val count: Int, val owner: Int) : LogNode
    data class Row(val entry: LogEntry) : LogNode
}

/**
 * Chronological log, split into one section per connect attempt and filterable
 * per transport (vanilla / obfs4 / webtunnel) so each raced connection can be
 * read on its own.
 */
private fun groupLog(
    lines: List<LogEntry>,
    sessions: List<LogSession>,
    filter: Char?,
    transport: String?
): List<LogNode> {
    val out = ArrayList<LogNode>()

    // One pass over the buffer into session -> level -> rows. The list is re-grouped
    // on every log flush, so scanning it once per session and once per level (what
    // this did before) turned a few thousand lines into tens of thousands of
    // intermediate lists, several times a second, which is what made the screen
    // stutter while a connection was running.
    val bySession = LinkedHashMap<Int, HashMap<Char, ArrayList<LogEntry>>>()
    for (entry in lines) {
        if (transport != null && entry.transport != transport) continue
        val levels = bySession.getOrPut(entry.session) { HashMap() }
        levels.getOrPut(entry.level) { ArrayList() }.add(entry)
    }
    if (bySession.isEmpty()) return out

    fun addSections(levels: HashMap<Char, ArrayList<LogEntry>>, sid: Int) {
        for (level in LOG_SEVERITY_ORDER) {
            if (filter != null && filter != level) continue
            val sel = levels[level] ?: continue
            out.add(LogNode.Header(level, sel.size, sid))
            sel.forEach { out.add(LogNode.Row(it)) }
        }
    }

    if (bySession.keys.all { it == 0 }) {
        addSections(bySession.getValue(0), 0)
        return out
    }

    // Oldest session first; the list is reverse-rendered so newest ends up on top.
    bySession.forEach { (sid, levels) ->
        if (filter != null && !levels.containsKey(filter)) return@forEach
        val meta = sessions.firstOrNull { it.id == sid }
        val section = levels.values.sumOf { it.size }
        val title = listOfNotNull(
            meta?.label,
            meta?.outcome
        ).joinToString(" \u00b7 ").ifBlank { if (sid == 0) "startup" else "session $sid" }
        out.add(LogNode.SessionHeader(sid, title, section))
        addSections(levels, sid)
    }
    return out
}

/** Per-transport line counts, in one pass instead of one pass per transport. */
private fun transportCountsOf(lines: List<LogEntry>): Map<String, Int> {
    val counts = HashMap<String, Int>()
    for (entry in lines) {
        val t = entry.transport ?: continue
        counts[t] = (counts[t] ?: 0) + 1
    }
    return counts
}

@Composable
private fun SeverityChip(
    label: String,
    selected: Boolean,
    onSelect: () -> Unit
) {
    Text(
        label,
        style = MaterialTheme.typography.labelSmall.copy(
            letterSpacing = 1.1.sp,
            fontWeight = FontWeight.Bold
        ),
        color = if (selected) DeltaTor.Text else DeltaTor.Muted,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(
                if (selected) DeltaTor.SurfaceLight else DeltaTor.Surface,
                RoundedCornerShape(50)
            )
            .border(
                1.dp,
                if (selected) DeltaTor.BorderLight else DeltaTor.Border,
                RoundedCornerShape(50)
            )
            .clickable { onSelect() }
            .padding(horizontal = 13.dp, vertical = 6.dp)
    )
}

@Composable
private fun LogGroupHeader(level: Char, count: Int, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(DeltaTor.Surface, RoundedCornerShape(10.dp))
            .padding(horizontal = 10.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(8.dp)
                .clip(RoundedCornerShape(50))
                .background(DeltaTor.Muted)
        )
        Spacer(Modifier.width(8.dp))
        Text(
            levelLabel(level),
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.6.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.Text
        )
        Spacer(Modifier.width(8.dp))
        Text(
            "\u00b7 $count",
            style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 0.4.sp),
            color = DeltaTor.Muted
        )
    }
}

@Composable
private fun TransportChip(
    label: String,
    selected: Boolean,
    count: Int,
    modifier: Modifier = Modifier,
    onSelect: () -> Unit
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(
                if (selected) DeltaTor.SurfaceLight else DeltaTor.Surface,
                RoundedCornerShape(50)
            )
            .border(
                1.dp,
                if (selected) DeltaTor.BorderLight else DeltaTor.Border,
                RoundedCornerShape(50)
            )
            .clickable { onSelect() }
            .padding(horizontal = 14.dp, vertical = 7.dp),
        contentAlignment = Alignment.Center
    ) {
        Text(
            text = if (count > 0) "$label ($count)" else label,
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.1.sp,
                fontWeight = FontWeight.Bold
            ),
            color = if (selected) DeltaTor.Text else DeltaTor.Muted,
            textAlign = TextAlign.Center
        )
    }
}

@Composable
@OptIn(ExperimentalMaterial3Api::class)
private fun SettingsDropdown(
    label: String,
    value: String,
    options: List<Pair<String, String>>,
    onSelect: (String) -> Unit
) {
    var expanded by remember { mutableStateOf(false) }
    val selectedLabel = options.firstOrNull { it.first == value }?.second ?: value
    Box(Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clip(RoundedCornerShape(12.dp))
                .background(DeltaTor.Surface)
                .border(
                    1.dp,
                    if (expanded) DeltaTor.AccentLight else DeltaTor.BorderLight,
                    RoundedCornerShape(12.dp)
                )
                .clickable { expanded = true }
                .padding(horizontal = 14.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                label,
                style = MaterialTheme.typography.labelSmall.copy(
                    letterSpacing = 1.1.sp,
                    fontWeight = FontWeight.Bold
                ),
                color = DeltaTor.Muted
            )
            Spacer(Modifier.weight(1f))
            Text(
                selectedLabel,
                style = MaterialTheme.typography.bodyMedium.copy(
                    fontWeight = FontWeight.SemiBold,
                    letterSpacing = 0.2.sp
                ),
                color = DeltaTor.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Spacer(Modifier.width(8.dp))
            Text(
                if (expanded) "\u25B2" else "\u25BC",
                fontSize = 9.sp,
                color = DeltaTor.AccentLight
            )
        }
        DropdownMenu(
            expanded = expanded,
            onDismissRequest = { expanded = false }
        ) {
            options.forEach { (optionValue, optionLabel) ->
                val isSelected = optionValue == value
                DropdownMenuItem(
                    text = {
                        Text(
                            optionLabel,
                            style = MaterialTheme.typography.bodyMedium.copy(
                                fontWeight = if (isSelected) FontWeight.SemiBold else FontWeight.Normal,
                                letterSpacing = 0.2.sp
                            ),
                            color = if (isSelected) DeltaTor.AccentLight else DeltaTor.Text
                        )
                    },
                    onClick = {
                        expanded = false
                        onSelect(optionValue)
                    }
                )
            }
        }
    }
}

@Composable
private fun LogSessionHeader(id: Int, title: String, count: Int, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(DeltaTor.Accent.copy(alpha = 0.16f), RoundedCornerShape(10.dp))
            .padding(horizontal = 10.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Box(
            Modifier
                .size(8.dp)
                .clip(RoundedCornerShape(50))
                .background(DeltaTor.AccentLight)
        )
        Spacer(Modifier.width(8.dp))
        Text(
            "CONNECTION #$id",
            style = MaterialTheme.typography.labelSmall.copy(
                letterSpacing = 1.6.sp,
                fontWeight = FontWeight.Bold
            ),
            color = DeltaTor.AccentLight
        )
        if (title.isNotBlank()) {
            Spacer(Modifier.width(8.dp))
            Text(
                title,
                style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 0.4.sp),
                color = DeltaTor.Text,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f, fill = false)
            )
        }
        Spacer(Modifier.width(8.dp))
        Text(
            "\u00b7 $count lines",
            style = MaterialTheme.typography.labelSmall.copy(letterSpacing = 0.4.sp),
            color = DeltaTor.Muted
        )
    }
}

@Composable
private fun LogScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    val lines by AppLog.lines.collectAsStateWithLifecycle()
    val sessions by AppLog.sessions.collectAsStateWithLifecycle()
    var copied by remember { mutableStateOf(false) }
    var filter by remember { mutableStateOf<Char?>(null) }
    var transport by remember { mutableStateOf<String?>(null) }
    val nodes = remember(lines, sessions, filter, transport) { groupLog(lines, sessions, filter, transport) }
    val transportCounts = remember(lines) { transportCountsOf(lines) }

    LaunchedEffect(Unit) {
        AppLog.addObserver()
        while (true) {
            AppLog.flushIfDirty()
            delay(250)
        }
    }
    DisposableEffect(Unit) {
        onDispose { AppLog.removeObserver() }
    }

    LaunchedEffect(copied) {
        if (copied) {
            delay(1500)
            copied = false
        }
    }

    fun copyLog() {
        val text = nodes.filterIsInstance<LogNode.Row>().joinToString("\n") { it.entry.raw }
        if (text.isBlank()) return
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.setPrimaryClip(ClipData.newPlainText("DeltaTor connection log", text))
        copied = true
    }

    Box(
        Modifier
            .fillMaxSize()
            .windowInsetsPadding(WindowInsets.systemBars)
            .background(
                Brush.verticalGradient(listOf(Color(0xFF1B2030), DeltaTor.Bg, Color(0xFF0C0E15)))
            )
    ) {
        Column(Modifier.fillMaxSize()) {
            ScreenTopBar("CONNECTION LOG", onBack) {
                Box(
                    modifier = Modifier
                        .clip(RoundedCornerShape(12.dp))
                        .background(
                            if (copied) {
                                Brush.horizontalGradient(listOf(DeltaTor.GreenDark, DeltaTor.Green))
                            } else {
                                Brush.horizontalGradient(listOf(DeltaTor.AccentDark, DeltaTor.Accent))
                            },
                            RoundedCornerShape(12.dp)
                        )
                        .clickable { copyLog() }
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Text(
                        if (copied) "COPIED" else "COPY",
                        style = MaterialTheme.typography.labelSmall.copy(
                            letterSpacing = 1.2.sp,
                            fontWeight = FontWeight.Bold
                        ),
                        color = Color.White
                    )
                }
            }

            Spacer(Modifier.height(4.dp))

            // A log that stops early still reads like a complete one, so say so
            // while recording is off instead of leaving a hole to be explained
            // later.
            if (!AppLog.enabled) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 20.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        Modifier
                            .size(6.dp)
                            .clip(RoundedCornerShape(50))
                            .background(DeltaTor.Muted)
                    )
                    Spacer(Modifier.width(8.dp))
                    Text(
                        if (lines.isEmpty()) {
                            "Recording is off \u00b7 nothing here yet"
                        } else {
                            "Recording is off \u00b7 this log stops here"
                        },
                        style = MaterialTheme.typography.bodySmall.copy(letterSpacing = 0.3.sp),
                        color = DeltaTor.Muted
                    )
                }
            }

            Spacer(Modifier.height(6.dp))

            // Transport picker: read one raced connection at a time.
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                TransportChip(
                    label = "ALL",
                    selected = transport == null,
                    count = 0
                ) { transport = null }
                AppLog.TRANSPORTS.forEach { t ->
                    TransportChip(
                        label = t.uppercase(),
                        selected = transport == t,
                        count = transportCounts[t] ?: 0
                    ) { transport = if (transport == t) null else t }
                }
            }

            Spacer(Modifier.height(6.dp))

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 20.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                SeverityChip("ALL", selected = filter == null, onSelect = { filter = null })
                for (level in LOG_SEVERITY_ORDER) {
                    SeverityChip(
                        levelLabel(level),
                        selected = filter == level,
                        onSelect = { filter = level }
                    )
                }
            }

            Spacer(Modifier.height(6.dp))

            if (nodes.isEmpty()) {
                Box(
                    Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    val activeTransport = transport
                    Text(
                        when {
                            !AppLog.enabled ->
                                "Logging is off. Turn it on under ADVANCED \u00b7 CONNECTION LOG."
                            lines.isEmpty() -> "No log lines captured yet. Start a connection."
                            activeTransport != null -> "No ${activeTransport.uppercase()} lines captured yet."
                            else -> "No entries for this level."
                        },
                        style = MaterialTheme.typography.bodyMedium.copy(letterSpacing = 0.4.sp),
                        color = DeltaTor.Muted
                    )
                }
            } else {
                val reversed = nodes.asReversed()
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f)
                        .padding(horizontal = 20.dp),
                    reverseLayout = true,
                    state = rememberLazyListState()
                ) {
                    reversed.forEach { node ->
                        when (node) {
                            is LogNode.SessionHeader -> item(key = "s-${node.id}") {
                                LogSessionHeader(
                                    id = node.id,
                                    title = node.title,
                                    count = node.count,
                                    modifier = Modifier.padding(top = 12.dp, bottom = 4.dp)
                                )
                            }
                            is LogNode.Header -> item(key = "h-${node.owner}-${node.level}") {
                                LogGroupHeader(
                                    level = node.level,
                                    count = node.count,
                                    modifier = Modifier.padding(top = 10.dp, bottom = 4.dp)
                                )
                            }
                            is LogNode.Row -> item(key = node.entry.id) {
                                Text(
                                    node.entry.raw,
                                    style = TextStyle(
                                        fontFamily = FontFamily.Monospace,
                                        fontSize = 10.sp,
                                        lineHeight = 13.sp,
                                        color = DeltaTor.Muted
                                    ),
                                    modifier = Modifier.padding(bottom = 3.dp)
                                )
                            }
                        }
                    }
                    item { Spacer(Modifier.height(18.dp)) }
                }
            }
        }
    }
}