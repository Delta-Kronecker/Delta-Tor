package io.deltator.ui

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import androidx.core.view.WindowCompat
import io.deltator.Config

/**
 * How the app picks between its two looks.
 *
 * [System] follows the device, which is what a fresh install does. The other two
 * pin the app to one look whatever the device is set to.
 */
enum class ThemeMode(val key: String) {
    System("system"),
    Light("light"),
    Dark("dark");

    companion object {
        /** An unknown or missing stored value falls back to following the system. */
        fun fromKey(key: String?): ThemeMode = entries.firstOrNull { it.key == key } ?: System
    }
}

/**
 * Every colour the app draws with, for one look.
 *
 * The names are roles, not hues: [accentLight] is "the accent as text or an icon
 * on a surface", which is a lighter violet in the dark look and a deeper one in
 * the light look. [onBright] is whatever reads on top of [accentLight], [green]
 * or [amber] when they are used as a fill.
 */
@Immutable
class DeltaTorPalette(
    val isDark: Boolean,
    // Canvas: the screen background is a three-stop vertical gradient.
    val bg: Color,
    val bgTop: Color,
    val bgBottom: Color,
    // Cards, sheets, fields.
    val surface: Color,
    val surfaceTop: Color,
    val surfaceBottom: Color,
    val surfaceAlt: Color,
    val surfaceLight: Color,
    val sheet: Color,
    val border: Color,
    val borderLight: Color,
    // Ink.
    val text: Color,
    val muted: Color,
    val onAccent: Color,
    val onBright: Color,
    // Brand violet.
    val accent: Color,
    val accentLight: Color,
    val accentSoft: Color,
    val accentDark: Color,
    val bannerTop: Color,
    // State colours.
    val green: Color,
    val greenLight: Color,
    val greenDark: Color,
    val red: Color,
    val amber: Color,
    val amberLight: Color,
    // Atmosphere.
    val keyLight: Color,
    val textShadow: Color
)

/** Deep ink with a violet cast. The look the app has always had, with a clearer muted grey. */
private val DarkPalette = DeltaTorPalette(
    isDark = true,
    bg = Color(0xFF12141C),
    bgTop = Color(0xFF1B2030),
    bgBottom = Color(0xFF0C0E15),
    surface = Color(0xFF1A1D26),
    surfaceTop = Color(0xFF20242F),
    surfaceBottom = Color(0xFF0F1119),
    surfaceAlt = Color(0xFF232732),
    surfaceLight = Color(0xFF2C313E),
    sheet = Color(0xFF14171F),
    border = Color(0xFF2D3241),
    borderLight = Color(0xFF3C4252),
    text = Color(0xFFF5F7FC),
    muted = Color(0xFF8790AB),
    onAccent = Color(0xFFFFFFFF),
    onBright = Color(0xFF08111A),
    accent = Color(0xFF8A5CF6),
    accentLight = Color(0xFFB79CFF),
    accentSoft = Color(0xFF6040BE),
    accentDark = Color(0xFF4830A0),
    bannerTop = Color(0xFF2A2140),
    green = Color(0xFF34D399),
    greenLight = Color(0xFF7DF3C0),
    greenDark = Color(0xFF269E76),
    red = Color(0xFFEF5C70),
    amber = Color(0xFFF5B23C),
    amberLight = Color(0xFFFFD58A),
    keyLight = Color.White.copy(alpha = 0.05f),
    textShadow = Color.Black.copy(alpha = 0.45f)
)

/**
 * Soft lavender paper with white cards and the same violet, deepened so it stays
 * readable on light. Body text, muted text and accent text all clear 4.5:1 on the
 * background; the state colours clear 3.5:1, which is what a ring or a bold label needs.
 */
private val LightPalette = DeltaTorPalette(
    isDark = false,
    bg = Color(0xFFF4F3FA),
    bgTop = Color(0xFFE8E5F8),
    bgBottom = Color(0xFFFBFAFF),
    surface = Color(0xFFFFFFFF),
    surfaceTop = Color(0xFFFFFFFF),
    surfaceBottom = Color(0xFFECEAF6),
    surfaceAlt = Color(0xFFEFEDF8),
    surfaceLight = Color(0xFFE4E1F2),
    sheet = Color(0xFFFFFFFF),
    border = Color(0xFFE1DEEE),
    borderLight = Color(0xFFCFCBE3),
    text = Color(0xFF171A2E),
    muted = Color(0xFF5B6280),
    onAccent = Color(0xFFFFFFFF),
    onBright = Color(0xFFFFFFFF),
    accent = Color(0xFF6C3DE0),
    accentLight = Color(0xFF5A2FC8),
    accentSoft = Color(0xFF5A32C4),
    accentDark = Color(0xFF4E28B8),
    bannerTop = Color(0xFFEBE5FC),
    green = Color(0xFF0B8F63),
    greenLight = Color(0xFF087552),
    greenDark = Color(0xFF06694A),
    red = Color(0xFFD6304A),
    amber = Color(0xFFA66600),
    amberLight = Color(0xFFBF7800),
    keyLight = Color(0xFF6C3DE0).copy(alpha = 0.08f),
    textShadow = Color.Black.copy(alpha = 0.12f)
)

/**
 * The colours the UI draws with.
 *
 * Every property reads the active [DeltaTorPalette] out of Compose snapshot state,
 * so a composable that reads `DeltaTor.Text` is recomposed when the look changes,
 * and so is a plain function that is only ever called from composition, such as
 * the helpers that pick a state colour. That is what lets the several hundred
 * existing call sites keep their shape while the values underneath them switch.
 *
 * Do not read these from a thread or a callback and keep the result: the value is
 * only live inside composition.
 */
object DeltaTor {
    private var palette by mutableStateOf(DarkPalette)

    /** The stored choice, observable so the settings control and the theme both follow it. */
    private var modeState by mutableStateOf(ThemeMode.fromKey(Config.themeMode))

    /** What the user picked, which may be [ThemeMode.System]. */
    val mode: ThemeMode get() = modeState

    /** Pick a mode and remember it. Takes effect on the next frame. */
    fun setMode(mode: ThemeMode) {
        if (modeState == mode) return
        modeState = mode
        Config.themeMode = mode.key
    }

    /** True when the look in force is the dark one, whatever the mode that chose it. */
    val isDark: Boolean get() = palette.isDark

    /**
     * The look last handed to [applyDark]. A plain field, not state, on purpose:
     * [applyDark] runs inside composition, and if it read [palette] to compare it
     * would subscribe the theme to the very value it is about to write.
     */
    private var applied: Boolean? = null

    /**
     * Make [dark] the look in force. Called by [DeltaTorTheme] once per composition
     * before anything below it draws; repeating the value already held does nothing,
     * so nothing is invalidated in the steady state.
     */
    internal fun applyDark(dark: Boolean) {
        if (applied == dark) return
        applied = dark
        palette = if (dark) DarkPalette else LightPalette
    }

    internal fun current(): DeltaTorPalette = palette

    val Bg: Color get() = palette.bg
    val BgTop: Color get() = palette.bgTop
    val BgBottom: Color get() = palette.bgBottom
    val Surface: Color get() = palette.surface
    val SurfaceTop: Color get() = palette.surfaceTop
    val SurfaceBottom: Color get() = palette.surfaceBottom
    val SurfaceAlt: Color get() = palette.surfaceAlt
    val SurfaceLight: Color get() = palette.surfaceLight
    val Sheet: Color get() = palette.sheet
    val Border: Color get() = palette.border
    val BorderLight: Color get() = palette.borderLight
    val Text: Color get() = palette.text
    val Muted: Color get() = palette.muted
    val OnAccent: Color get() = palette.onAccent
    val OnBright: Color get() = palette.onBright
    val Accent: Color get() = palette.accent
    val AccentLight: Color get() = palette.accentLight
    val AccentSoft: Color get() = palette.accentSoft
    val AccentDark: Color get() = palette.accentDark
    val BannerTop: Color get() = palette.bannerTop
    val Green: Color get() = palette.green
    val GreenLight: Color get() = palette.greenLight
    val GreenDark: Color get() = palette.greenDark
    val Red: Color get() = palette.red
    val Amber: Color get() = palette.amber
    val AmberLight: Color get() = palette.amberLight
    val KeyLight: Color get() = palette.keyLight
    val TextShadow: Color get() = palette.textShadow
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

/**
 * The Material colour scheme for a palette.
 *
 * The app draws most of itself, but the navigation bar, dropdown menus, dialogs and
 * text fields come from Material and read these roles, so they are filled in
 * explicitly rather than left at Material's purple defaults.
 */
private fun materialSchemeFor(p: DeltaTorPalette): ColorScheme =
    if (p.isDark) {
        darkColorScheme(
            primary = p.accent,
            onPrimary = p.onAccent,
            primaryContainer = p.accentDark,
            onPrimaryContainer = p.onAccent,
            secondary = p.green,
            onSecondary = p.onBright,
            secondaryContainer = p.surfaceLight,
            onSecondaryContainer = p.text,
            background = p.bg,
            onBackground = p.text,
            surface = p.surface,
            onSurface = p.text,
            surfaceVariant = p.surfaceAlt,
            onSurfaceVariant = p.muted,
            surfaceContainerLowest = p.bg,
            surfaceContainerLow = p.sheet,
            surfaceContainer = p.surface,
            surfaceContainerHigh = p.surfaceAlt,
            surfaceContainerHighest = p.surfaceLight,
            error = p.red,
            onError = Color.White,
            outline = p.border,
            outlineVariant = p.borderLight
        )
    } else {
        lightColorScheme(
            primary = p.accent,
            onPrimary = p.onAccent,
            primaryContainer = p.bannerTop,
            onPrimaryContainer = p.accentDark,
            secondary = p.green,
            onSecondary = p.onBright,
            secondaryContainer = p.surfaceLight,
            onSecondaryContainer = p.text,
            background = p.bg,
            onBackground = p.text,
            surface = p.surface,
            onSurface = p.text,
            surfaceVariant = p.surfaceAlt,
            onSurfaceVariant = p.muted,
            surfaceContainerLowest = p.bg,
            surfaceContainerLow = p.sheet,
            surfaceContainer = p.surface,
            surfaceContainerHigh = p.surfaceAlt,
            surfaceContainerHighest = p.surfaceLight,
            error = p.red,
            onError = Color.White,
            outline = p.border,
            outlineVariant = p.borderLight
        )
    }

/** The activity behind a compose view, which a plain `as Activity` cast misses under a wrapper. */
private fun Context.findActivity(): Activity? {
    var c: Context? = this
    while (c is ContextWrapper) {
        if (c is Activity) return c
        c = c.baseContext
    }
    return null
}

@Composable
fun DeltaTorTheme(content: @Composable () -> Unit) {
    val dark = when (DeltaTor.mode) {
        ThemeMode.System -> isSystemInDarkTheme()
        ThemeMode.Light -> false
        ThemeMode.Dark -> true
    }
    // Before any content below composes, so the first frame already has the right look.
    DeltaTor.applyDark(dark)

    // The bars are transparent and drawn over the app, so their icons have to be
    // dark on the light look and light on the dark one.
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = view.context.findActivity()?.window ?: return@SideEffect
            val controller = WindowCompat.getInsetsController(window, view)
            controller.isAppearanceLightStatusBars = !dark
            controller.isAppearanceLightNavigationBars = !dark
        }
    }

    MaterialTheme(
        colorScheme = materialSchemeFor(DeltaTor.current()),
        typography = DeltaTorTypography
    ) {
        // The window behind the system bars is whatever the theme XML says, which
        // cannot follow an in-app choice. Painting the whole window here does.
        Box(Modifier.fillMaxSize().background(DeltaTor.Bg)) {
            content()
        }
    }
}
