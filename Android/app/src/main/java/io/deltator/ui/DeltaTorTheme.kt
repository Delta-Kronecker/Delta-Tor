package io.deltator.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * The exact DeltaTor (win) theme — dark, borderless, fully owner-drawn look.
 * Colors are copied 1:1 from scripts/TorJetUi.cs Theme (TorJet Core).
 */
object DeltaTor {
    val Bg = Color(0xFF12141C)
    val Surface = Color(0xFF1A1D26)
    val SurfaceAlt = Color(0xFF232732)
    val SurfaceLight = Color(0xFF2C313E)
    val Border = Color(0xFF2D3241)
    val BorderLight = Color(0xFF3C4252)
    val Text = Color(0xFFF5F7FC)
    val Muted = Color(0xFF78829B)
    val Accent = Color(0xFF8A5CF6)
    val AccentLight = Color(0xFFB79CFF)
    val AccentSoft = Color(0xFF6040BE)
    val AccentDark = Color(0xFF4830A0)
    val Green = Color(0xFF34D399)
    val GreenLight = Color(0xFF7DF3C0)
    val GreenDark = Color(0xFF269E76)
    val Red = Color(0xFFEF5C70)
    val Amber = Color(0xFFF5B23C)
    val AmberLight = Color(0xFFFFD58A)
}

// WinForms sizes (pt) — Title 11 / Big 18 / H2 9.5 / Body 9.25 / Small 8 / Caption 7.25.
private val DeltaTorTypography = Typography(
    headlineLarge = TextStyle(fontSize = 28.sp, fontWeight = FontWeight.Bold),   // Big 18pt
    headlineMedium = TextStyle(fontSize = 17.sp, fontWeight = FontWeight.Bold),  // Title 11pt
    titleLarge = TextStyle(fontSize = 17.sp, fontWeight = FontWeight.Bold),      // Title 11pt
    titleMedium = TextStyle(fontSize = 15.sp, fontWeight = FontWeight.Bold),     // H2 9.5pt
    bodyLarge = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Normal),     // Body 9.25pt
    bodyMedium = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Normal),    // Body 9.25pt
    bodySmall = TextStyle(fontSize = 12.sp, fontWeight = FontWeight.Normal),     // Small 8pt
    labelSmall = TextStyle(fontSize = 10.sp, fontWeight = FontWeight.Bold)       // Caption 7.25pt
)

private val DeltaTorColors = darkColorScheme(
    primary = DeltaTor.Accent,
    onPrimary = DeltaTor.Text,
    secondary = DeltaTor.Green,
    onSecondary = Color(0xFF001611),
    background = DeltaTor.Bg,
    onBackground = DeltaTor.Text,
    surface = DeltaTor.Surface,
    onSurface = DeltaTor.Text,
    surfaceVariant = DeltaTor.SurfaceAlt,
    onSurfaceVariant = DeltaTor.Muted,
    error = DeltaTor.Red,
    onError = Color(0xFFFFFFFF),
    outline = DeltaTor.Border,
    outlineVariant = DeltaTor.BorderLight
)

@Composable
fun DeltaTorTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DeltaTorColors,
        typography = DeltaTorTypography,
        content = content
    )
}