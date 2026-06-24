package com.deltator.tor.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.ui.theme.*

@Composable
fun StatusBar(
    statusText: String,
    isConnected: Boolean,
    proxyInfo: String = ""
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(CARD)
            .padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = "\u25CF",
            color = if (isConnected) GRN else RED,
            fontSize = 12.sp,
            modifier = Modifier.padding(end = 6.dp)
        )
        Text(
            text = statusText,
            color = FG2,
            fontSize = 12.sp,
            modifier = Modifier.weight(1f)
        )
        if (proxyInfo.isNotEmpty()) {
            Text(
                text = proxyInfo,
                color = GRN,
                fontSize = 10.sp,
                fontFamily = FontFamily.Monospace
            )
        }
    }
}

@Composable
fun StatsRow(label: String, value: String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 16.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = label,
            color = FG2,
            fontSize = 13.sp,
            modifier = Modifier.width(80.dp)
        )
        Text(
            text = value,
            color = GRN,
            fontSize = 13.sp,
            modifier = Modifier.weight(1f)
        )
    }
}
