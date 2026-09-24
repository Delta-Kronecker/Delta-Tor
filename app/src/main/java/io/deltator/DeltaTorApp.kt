package io.deltator

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build

class DeltaTorApp : Application() {

    override fun onCreate() {
        super.onCreate()
        Config.init(this)
        createNotificationChannels()
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
        }
    }
}

/**
 * Persistent user configuration, backed by SharedPreferences.
 */
object Config {
    private lateinit var appContext: Context
    private val prefs get() = appContext.getSharedPreferences("deltator", Context.MODE_PRIVATE)

    const val BRIDGE_SNOWFLAKE = "snowflake"
    const val BRIDGE_SNOWFLAKE_AMP = "snowflake_amp"
    const val BRIDGE_OBFS4 = "obfs4"
    const val BRIDGE_MEEK = "meek"
    const val BRIDGE_DIRECT = "direct"
    const val BRIDGE_CUSTOM = "custom"

    const val DEFAULT_PROXY_PORT = 10880

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    var bridgeType: String
        get() = prefs.getString("bridge_type", BRIDGE_SNOWFLAKE)!!
        set(value) = prefs.edit().putString("bridge_type", value).apply()

    var customBridgeLines: String
        get() = prefs.getString("custom_bridge_lines", "")!!
        set(value) = prefs.edit().putString("custom_bridge_lines", value).apply()

    var proxyPort: Int
        get() = prefs.getInt("proxy_port", DEFAULT_PROXY_PORT)
        set(value) = prefs.edit().putInt("proxy_port", value.coerceIn(1024, 65535)).apply()

    var debugMode: Boolean
        get() = prefs.getBoolean("debug_mode", false)
        set(value) = prefs.edit().putBoolean("debug_mode", value).apply()

    /** Resolve the current selection to bridge lines consumed by SnowflakeBridge. */
    fun bridgeLines(): String = when (bridgeType) {
        BRIDGE_SNOWFLAKE -> ""
        BRIDGE_SNOWFLAKE_AMP -> "SNOWFLAKE_AMP"
        BRIDGE_OBFS4 -> DEFAULT_OBFS4_BRIDGES
        BRIDGE_MEEK -> DEFAULT_MEEK_BRIDGE
        BRIDGE_DIRECT -> "DIRECT"
        else -> customBridgeLines.trim()
    }

    fun transportLabel(): String = when (bridgeType) {
        BRIDGE_SNOWFLAKE -> "Snowflake"
        BRIDGE_SNOWFLAKE_AMP -> "Snowflake (AMP)"
        BRIDGE_OBFS4 -> "obfs4"
        BRIDGE_MEEK -> "Meek (Azure)"
        BRIDGE_DIRECT -> "Direct"
        else -> "Custom"
    }

    // Built-in obfs4 bridges (from Tor Project's /circumvention/builtin API)
    val DEFAULT_OBFS4_BRIDGES = """
        obfs4 51.222.13.177:80 5EDAC3B810E12B01F6FD8050D2FD3E277B289A08 cert=2uplIpLQ0q9+0qMFrK5pkaYRDOe460LL9WHBvatgkuRr/SL31wBOEupaMMJ6koRE6Ld0ew iat-mode=0
        obfs4 37.218.245.14:38224 D9A82D2F9C2F65A18407B1D2B764F130847F8B5D cert=bjRaMrr1BRiAW8IE9U5z27fQaYgOhX1UCmOpg2pFpoMvo6ZgQMzLsaTzzQNTlm7hNcb+Sg iat-mode=0
        obfs4 45.145.95.6:27015 C5B7CD6946FF10C5B3E89691A7D3F2C122D2117C cert=TD7PbUO0/0k6xYHMPW3vJxICfkMZNdkRrb63Zhl5j9dW3iRGiCx0A7mPhe5T2EDzQ35+Zw iat-mode=0
        obfs4 209.148.46.65:443 74FAD13168806246602538555B5521A0383A1875 cert=ssH+9rP8dG2NLDN2XuFw63hIO/9MNNinLmxQDpVa+7kTOa9/m+tGWT1SmSYpQ9uTBGa6Hw iat-mode=0
        obfs4 146.57.248.225:22 10A6CD36A537FCE513A322361547444B393989F0 cert=K1gDtDAIcUfeLqbstggjIw2rtgIKqdIhUlHp82XRqNSq/mtAjp1BIC9vHKJ2FAEpGssTPw iat-mode=0
        obfs4 212.83.43.95:443 BFE712113A72899AD685764B211FACD30FF52C31 cert=ayq0XzCwhpdysn5o0EyDUbmSOx3X/oTEbzDMvczHOdBJKlvIdHHLJGkZARtT4dcBFArPPg iat-mode=1
        obfs4 212.83.43.74:443 39562501228A4D5E27FCA4C0C81A01EE23AE3EE4 cert=PBwr+S8JTVZo6MPdHnkTwXJPILWADLqfMGoVvhZClMq/Urndyd42BwX9YFJHZnBB3H0XCw iat-mode=1
    """.trimIndent()

    // Built-in meek_lite bridge (CDN77 domain fronting, from Tor Browser defaults)
    const val DEFAULT_MEEK_BRIDGE =
        "meek_lite 192.0.2.20:80 url=https://1603026938.rsc.cdn77.org front=www.phpmyadmin.net utls=HelloRandomizedALPN"
}