package com.deltator.tor

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build

class DeltaTorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        createNotificationChannels()
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java)
            val torChannel = NotificationChannel(
                "tor_service",
                "Tor Connection",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Tor connection status"
            }
            manager.createNotificationChannel(torChannel)
        }
    }
}
