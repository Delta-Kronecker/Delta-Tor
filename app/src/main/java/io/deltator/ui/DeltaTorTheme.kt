package io.deltator.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext

private val DarkColors = darkColorScheme(
    primary = androidx.compose.ui.graphics.Color(0xFF80CBC4),
    secondary = androidx.compose.ui.graphics.Color(0xFFA5D6A7),
    background = androidx.compose.ui.graphics.Color(0xFF101418),
    surface = androidx.compose.ui.graphics.Color(0xFF161C22),
    onSurface = androidx.compose.ui.graphics.Color(0xFFE0E3E7)
)

private val LightColors = lightColorScheme(
    primary = androidx.compose.ui.graphics.Color(0xFF00695C),
    secondary = androidx.compose.ui.graphics.Color(0xFF2E7D32),
    surfaceVariant = androidx.compose.ui.graphics.Color(0xFFE6EDE9),
    onSurface = androidx.compose.ui.graphics.Color(0xFF1A1C1E)
)

@Composable
fun DeltaTorTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val colorScheme = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> {
            val context = LocalContext.current
            if (darkTheme) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
        }
        darkTheme -> DarkColors
        else -> LightColors
    }
    MaterialTheme(
        colorScheme = colorScheme,
        content = content
    )
}