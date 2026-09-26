package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.HttpsURLConnection
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.URL

/** Where the traffic leaves the Tor network. */
data class ExitInfo(
    val ip: String,
    val countryCode: String = "",
    val countryName: String = "",
    val city: String = "",
    val asn: String = "",
    /** True when the country came from ip2location rather than the offline GeoIP db. */
    val fromNetwork: Boolean = false
) {
    /** "BE \u00b7 Belgium", or just the name when the code is unknown. */
    fun label(): String = when {
        countryCode.isNotBlank() && countryName.isNotBlank() -> "$countryCode \u00b7 $countryName"
        countryName.isNotBlank() -> countryName
        countryCode.isNotBlank() -> countryCode
        else -> "unknown"
    }
}

/**
 * Resolves the public exit IP and country as seen through the app's SOCKS5
 * tunnel, i.e. through the Tor circuit rather than the device's own network.
 *
 * Primary source is ip2location.io; when that is rate limited or unreachable it
 * falls back to a plain-HTTP IP echo plus the bundled offline GeoIP database.
 */
object ExitLocator {
    private const val TAG = "ExitLocator"

    private const val GEO_URL = "https://api.ip2location.io/"
    private val echoUrls = listOf(
        "https://api.ipify.org/",
        "http://icanhazip.com/",
        "http://ifconfig.me/"
    )

    private val IPV4 = Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}$")

    /** Full lookup: country straight from the network, or from the offline db. */
    suspend fun locate(
        context: Context,
        socksHost: String,
        socksPort: Int,
        timeoutMs: Int = 15000
    ): ExitInfo? {
        val proxy = Proxy(Proxy.Type.SOCKS, InetSocketAddress(socksHost, socksPort))

        geoLookup(proxy, timeoutMs)?.let { return it }

        for (url in echoUrls) {
            val ip = try {
                val body = fetch(proxy, url, timeoutMs)
                body.lineSequence().map { it.trim() }.firstOrNull { IPV4.matches(it) }
            } catch (e: Exception) {
                Log.w(TAG, "Echo probe $url failed: ${e.message}")
                null
            } ?: continue
            val offline = runCatching { BridgeCountries.countryInfo(context, ip) }.getOrNull()
            return ExitInfo(
                ip = ip,
                countryCode = offline?.first ?: "",
                countryName = offline?.second ?: "",
                fromNetwork = false
            )
        }
        return null
    }

    // --- ip2location.io -------------------------------------------------------

    private fun geoLookup(proxy: Proxy, timeoutMs: Int): ExitInfo? = try {
        val body = fetch(proxy, GEO_URL, timeoutMs)
        val ip = json(body, "ip")
        if (ip.isNullOrBlank()) {
            Log.w(TAG, "ip2location returned no ip: ${body.take(200)}")
            null
        } else {
            ExitInfo(
                ip = ip,
                countryCode = json(body, "country_code").orEmpty(),
                countryName = json(body, "country_name").orEmpty(),
                city = json(body, "city_name").orEmpty(),
                asn = json(body, "as").orEmpty(),
                fromNetwork = true
            )
        }
    } catch (e: Exception) {
        Log.w(TAG, "ip2location lookup failed: ${e.message}")
        null
    }

    private fun json(body: String, key: String): String? =
        Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\"([^\"]*)\"")
            .find(body)?.groupValues?.get(1)

    // --- transport ------------------------------------------------------------

    /**
     * Fetch through the SOCKS5 tunnel. Android's HTTP stack hands the hostname
     * to the SOCKS server, so the DNS lookup also happens on the exit.
     */
    private fun fetch(proxy: Proxy, url: String, timeoutMs: Int): String {
        val conn = URL(url).openConnection(proxy)
        conn.connectTimeout = timeoutMs
        conn.readTimeout = timeoutMs
        conn.setRequestProperty("User-Agent", "DeltaTor/2.0")
        conn.setRequestProperty("Accept", "*/*")
        conn.setRequestProperty("Connection", "close")
        if (conn is HttpsURLConnection) conn.instanceFollowRedirects = false
        return try {
            conn.getInputStream().bufferedReader(Charsets.UTF_8).use { reader: BufferedReader ->
                reader.readText()
            }
        } finally {
            (conn as? HttpURLConnection)?.disconnect()
        }
    }
}
