package io.deltator

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import io.deltator.tunnel.BridgeStore
import io.deltator.tunnel.ExitNodes
import io.deltator.tunnel.TorrcSettings
import io.deltator.util.AppLog
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class DeltaTorApp : Application() {

    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onCreate() {
        super.onCreate()
        Config.init(this)
        AppLog.enabled = Config.loggingEnabled
        TorrcSettings.init(this)
        ExitNodes.init(this)
        createNotificationChannels()
        BridgeStore.refreshState(this)
        appScope.launch { BridgeStore.autoUpdateIfStale(this@DeltaTorApp) }
        appScope.launch { ReleaseChecker.check(this@DeltaTorApp) }
        appScope.launch { InstallCounter.countInstall(this@DeltaTorApp) }
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

    /**
     * Whether the connection log is recorded. On by default, because a log that
     * records nothing is no use to anyone who then has a problem; off is for the
     * user who would rather have the memory and the battery back.
     */
    var loggingEnabled: Boolean
        get() = prefs.getBoolean("logging_enabled", true)
        set(value) = prefs.edit().putBoolean("logging_enabled", value).apply()

    var transportMode: String
        // "auto","vanilla","obfs4","webtunnel","snowflake","direct","custom"
        get() = prefs.getString("transport_mode", "auto") ?: "auto"
        set(value) = prefs.edit().putString("transport_mode", value).apply()

    var customBridges: String
        get() = prefs.getString("custom_bridges", "") ?: ""
        set(value) = prefs.edit().putString("custom_bridges", value).apply()

    /**
     * Which transports take part in auto mode. Stored as a comma separated set so
     * an older install that never wrote the key falls back to the full default.
     * The memory runner is not listed: it only ever joins auto, and only when it
     * has bridges that provably worked.
     */
    val AUTO_TRANSPORT_CHOICES = listOf("vanilla", "obfs4", "webtunnel", "snowflake")
    val AUTO_TRANSPORT_DEFAULTS: Set<String> = AUTO_TRANSPORT_CHOICES.toSet()

    var autoTransports: Set<String>
        get() {
            val raw = prefs.getString("auto_transports", null) ?: return AUTO_TRANSPORT_DEFAULTS
            val stored = raw.split(',').map { it.trim() }.filter { it in AUTO_TRANSPORT_CHOICES }
            // An empty or fully invalid value must not leave auto with nothing to race.
            return stored.toSet().ifEmpty { AUTO_TRANSPORT_DEFAULTS }
        }
        set(value) {
            val clean = value.filter { it in AUTO_TRANSPORT_CHOICES }.toSet()
            prefs.edit()
                .putString("auto_transports", clean.joinToString(","))
                .apply()
        }
}