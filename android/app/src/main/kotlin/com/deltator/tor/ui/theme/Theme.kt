package com.deltator.tor.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable

private val DarkColorScheme = darkColorScheme(
    primary = ACC,
    secondary = ACC2,
    tertiary = GRN,
    background = BG,
    surface = PANEL,
    surfaceVariant = CARD,
    onPrimary = FG,
    onSecondary = FG,
    onBackground = FG,
    onSurface = FG,
    onSurfaceVariant = FG2,
    error = RED,
    outline = BORDER
)

@Composable
fun DeltaTorTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DarkColorScheme,
        typography = DeltaTypography,
        content = content
    )
}
