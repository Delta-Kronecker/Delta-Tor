package com.deltator.tor.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.deltator.tor.ui.theme.*

@Composable
fun ProgressCard(progress: Int, label: String = "Progress:") {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(CARD)
            .padding(horizontal = 14.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = label,
            color = FG2,
            fontSize = 12.sp,
            modifier = Modifier.padding(end = 8.dp)
        )
        Text(
            text = "$progress%",
            color = FG,
            fontSize = 12.sp,
            modifier = Modifier.padding(end = 8.dp)
        )
        LinearProgressIndicator(
            progress = { progress / 100f },
            modifier = Modifier
                .weight(1f)
                .height(6.dp),
            color = ACC,
            trackColor = BTN
        )
    }
}
