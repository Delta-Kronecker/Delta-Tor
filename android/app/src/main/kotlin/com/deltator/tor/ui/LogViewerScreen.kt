package com.deltator.tor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.ui.theme.*

@Composable
fun LogViewerScreen(
    title: String,
    logs: List<String>,
    onBack: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(BG)
    ) {
        // Header
        Box(Modifier.fillMaxWidth().background(ACC).height(3.dp))
        Row(
            Modifier.fillMaxWidth().background(CARD).padding(10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("\u25C0 Back", color = ACC, fontSize = 14.sp,
                modifier = Modifier.clickable(onClick = onBack).padding(end = 16.dp))
            Text("Log \u2014 $title", color = ACC, fontSize = 14.sp)
        }

        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(PANEL)
                .padding(10.dp)
                .verticalScroll(rememberScrollState())
        ) {
            if (logs.isEmpty()) {
                Text("No log yet. Start the connection first.", color = FG2, fontSize = 12.sp)
            }
            logs.forEach { line ->
                Text(
                    text = line,
                    color = when {
                        "[err]" in line.lowercase() || "error" in line.lowercase() -> RED
                        "[warn]" in line.lowercase() || "warn" in line.lowercase() -> YLW
                        "[notice]" in line.lowercase() || "bootstrapped" in line.lowercase() -> GRN
                        else -> FG2
                    },
                    fontSize = 10.sp
                )
            }
        }
    }
}
