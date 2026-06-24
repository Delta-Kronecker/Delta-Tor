package com.deltator.tor

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.lifecycle.viewmodel.compose.viewModel
import com.deltator.tor.ui.*
import com.deltator.tor.ui.theme.DeltaTorTheme
import com.deltator.tor.viewmodel.*

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            DeltaTorTheme {
                DeltaTorNavHost()
            }
        }
    }
}

@Composable
fun DeltaTorNavHost() {
    var screen by remember { mutableStateOf("main") }
    var logTarget by remember { mutableStateOf("Main") }
    var logData by remember { mutableStateOf<List<String>>(emptyList()) }

    val mainViewModel: MainViewModel = viewModel()
    val multiViewModel: MultiConnectViewModel = viewModel()
    val settingsViewModel: SettingsViewModel = viewModel()
    val scannerViewModel: BridgeScannerViewModel = viewModel()

    when (screen) {
        "main" -> MainScreen(
            viewModel = mainViewModel,
            onNavigateToMulti = { screen = "multi" },
            onNavigateToScanner = { screen = "scanner" },
            onNavigateToSettings = { screen = "settings" }
        )

        "multi" -> MultiConnectScreen(
            viewModel = multiViewModel,
            onBack = { screen = "main" }
        )

        "settings" -> SettingsScreen(
            viewModel = settingsViewModel,
            onBack = { screen = "main" }
        )

        "scanner" -> BridgeScannerScreen(
            viewModel = scannerViewModel,
            onBack = { screen = "main" }
        )

        "log" -> LogViewerScreen(
            title = logTarget,
            logs = logData,
            onBack = { screen = "main" }
        )
    }
}
