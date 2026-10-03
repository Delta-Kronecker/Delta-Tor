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
     * Whether the connection log is recorded.
     *
     * Off by default. Recording every Tor line of every connect attempt across
     * three Tor cores is the most expensive thing in the app that the user never
     * asked for, and nobody misses a log they never turned on. The bootstrap
     * percentage and the failure reason still appear on screen without it, since
     * Tor's output is read either way; only the kept history is off.
     */
    var loggingEnabled: Boolean
        get() = prefs.getBoolean("logging_enabled", false)
        set(value) = prefs.edit().putBoolean("logging_enabled", value).apply()

    /**
     * Which transport a fresh install connects with.
     *
     * Webtunnel rather than auto, because auto is not a safe first impression: it
     * races three bridge lists, and on the censored networks this app exists for,
     * vanilla and obfs4 are the two most likely to be blocked, so the user watches
     * three runners crawl before the one that works is even tried. Webtunnel goes
     * through an HTTPS CONNECT to a CDN-fronted endpoint, which is the shape most
     * likely to be reachable, and it fails honestly and quickly when it is not.
     *
     * Only the fallback moves. An install that already has a stored choice keeps
     * it, because nobody wants a preference they set silently rewritten.
     */
    const val DEFAULT_TRANSPORT_MODE = "webtunnel"

    var transportMode: String
        // "auto","vanilla","obfs4","webtunnel","snowflake","direct","custom"
        get() = prefs.getString("transport_mode", DEFAULT_TRANSPORT_MODE) ?: DEFAULT_TRANSPORT_MODE
        set(value) = prefs.edit().putString("transport_mode", value).apply()

    /**
     * Run Tor as a plain local SOCKS5 proxy and never bring up the VPN interface.
     *
     * Off by default, because the VPN is the reason most people install this: with
     * it on, no app's traffic moves unless the app says so. Proxy mode is for the
     * cases the VPN cannot serve -- a browser or a desktop tool that takes a SOCKS
     * address, or a user who would rather decide per application than hand the
     * whole device over.
     *
     * The engine side is identical either way. Tor still bootstraps through the
     * same transports and still listens on [proxyPort]; the only difference is
     * that nothing calls VpnService.Builder.establish(), so the system draws no
     * tunnel and no traffic is captured.
     */
    var proxyOnlyMode: Boolean
        get() = prefs.getBoolean("proxy_only_mode", false)
        set(value) = prefs.edit().putBoolean("proxy_only_mode", value).apply()

    /**
     * Which installed applications bypass the tunnel, by package name.
     *
     * Empty means "everything goes through Tor", which is the only safe default:
     * a split-tunnel list that starts populated would silently leak the traffic of
     * whatever the user did not look at. An app named here is excluded with
     * VpnService.Builder.addDisallowedApplication, so its traffic leaves the
     * device directly and unencrypted.
     *
     * Persisted as a set because the list is consulted on every connect and
     * rewritten in full whenever it changes; there is no incremental case.
     */
    var splitTunnelExcluded: Set<String>
        get() = prefs.getStringSet("split_tunnel_excluded", emptySet()) ?: emptySet()
        set(value) = prefs.edit().putStringSet("split_tunnel_excluded", value).apply()

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

    /**
     * Snowflake is deliberately off by default, and this is the reason the whole
     * distinction exists: it fronts Tor through a volunteer proxy in a browser, so
     * it is both the slowest to bootstrap and the one that is not always reachable.
     * Racing it by default means every connect pays for a fourth runner that mostly
     * loses, and on a network where the other three are blocked it can be the only
     * thing that works. So it is one tap away in settings, and the connect flow
     * tells the user about that tap if a long auto race goes nowhere.
     *
     * Note this is only the default: an install whose stored set already includes
     * snowflake keeps it, because silently dropping a transport the user picked on
     * purpose would be worse than leaving one out.
     */
    val AUTO_TRANSPORT_DEFAULTS: Set<String> =
        setOf("vanilla", "obfs4", "webtunnel")

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