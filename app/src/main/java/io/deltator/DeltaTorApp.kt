package io.deltator

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import io.deltator.tunnel.BridgeStore
import io.deltator.tunnel.TorrcSettings
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class DeltaTorApp : Application() {

    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onCreate() {
        super.onCreate()
        Config.init(this)
        TorrcSettings.init(this)
        createNotificationChannels()
        BridgeStore.refreshState(this)
        appScope.launch { BridgeStore.autoUpdateIfStale(this@DeltaTorApp) }
        appScope.launch { ReleaseChecker.check(this@DeltaTorApp) }
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val manager = getSystemService(NotificationManager::class.java)
            val vpn = NotificationChannel(
                TorVpnService.CHANNEL_VPN_STATUS,
                getString(R.string.channel_vpn_status),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = getString(R.string.channel_vpn_status_desc)
                setShowBadge(false)
            }
            manager.createNotificationChannel(vpn)

            manager.createNotificationChannel(
                NotificationChannel(
                    TorVpnService.CHANNEL_UPDATES,
                    getString(R.string.channel_updates),
                    NotificationManager.IMPORTANCE_DEFAULT
                ).apply {
                    description = getString(R.string.channel_updates_desc)
                    setShowBadge(true)
                }
            )
        }
    }
}

/**
 * Persistent user configuration, backed by SharedPreferences.
 */
object Config {
    private lateinit var appContext: Context
    private val prefs get() = appContext.getSharedPreferences("deltator", Context.MODE_PRIVATE)

    const val DEFAULT_PROXY_PORT = 9050

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    var proxyPort: Int
        get() = prefs.getInt("proxy_port", DEFAULT_PROXY_PORT)
        set(value) = prefs.edit().putInt("proxy_port", value.coerceIn(1024, 65535)).apply()

    var debugMode: Boolean
        get() = prefs.getBoolean("debug_mode", false)
        set(value) = prefs.edit().putBoolean("debug_mode", value).apply()
}