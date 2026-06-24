package com.deltator.tor.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

val DeltaTypography = Typography(
    headlineMedium = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 20.sp,
        color = ACC
    ),
    titleMedium = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 16.sp,
        color = FG
    ),
    titleSmall = TextStyle(
        fontWeight = FontWeight.Bold,
        fontSize = 14.sp,
        color = FG
    ),
    bodyMedium = TextStyle(
        fontSize = 14.sp,
        color = FG
    ),
    bodySmall = TextStyle(
        fontSize = 12.sp,
        color = FG2
    ),
    labelMedium = TextStyle(
        fontSize = 12.sp,
        color = FG
    ),
    labelSmall = TextStyle(
        fontSize = 10.sp,
        color = FG2
    )
)
