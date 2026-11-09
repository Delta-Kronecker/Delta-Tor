package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.URL
import java.util.Locale
import javax.net.ssl.HttpsURLConnection

/** Where the traffic leaves the Tor network or proxy chain. */
data class ExitInfo(
    val ip: String,
    val countryCode: String = "",
    val countryName: String = "",
    val city: String = "",
    val asn: String = "",
    val fromNetwork: Boolean = false
) {
    fun label(): String = when {
        countryCode.isNotBlank() && countryName.isNotBlank() -> "$countryCode \u00b7 $countryName"
        countryName.isNotBlank() -> countryName
        countryCode.isNotBlank() -> countryCode
        else -> "unknown"
    }
}

/**
 * Resolves the true public exit IP and country.
 * Always queries through the local SOCKS5 proxy bridge (127.0.0.1:proxyPort)
 * to prevent real IP leaks (since DeltaTor itself is excluded from the VPN TUN
 * to prevent routing loops, so raw direct sockets bypass the VPN).
 *
 * Works for every exit path (direct Tor and Tor→chain/WARP/Amnezia/MASQUE/free
 * nodes) because the probe rides the same SOCKS bridge the apps use.
 */
object ExitLocator {
    private const val TAG = "ExitLocator"

    private const val GEO_URL = "https://api.ip2location.io/"
    private val echoUrls = listOf(
        "https://api.ipify.org/",
        "https://checkip.amazonaws.com/",
        "http://icanhazip.com/",
        "http://ifconfig.me/"
    )

    private val IPV4 = Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}$")
    /** Cloudflare/special markers that are not a geographic country. */
    private val NON_GEO = setOf("T1", "XX", "A1", "A2", "O1")

    suspend fun locate(
        context: Context,
        socksHost: String = "127.0.0.1",
        socksPort: Int = io.deltator.Config.proxyPort,
        timeoutMs: Int = 8000
    ): ExitInfo? {
        val socksProxy = Proxy(Proxy.Type.SOCKS, InetSocketAddress(socksHost, socksPort))
        return probeAll(context, socksProxy, timeoutMs)
    }

    /** Probe without a SOCKS proxy (direct/VPN egress). */
    suspend fun locateDirect(context: Context, timeoutMs: Int = 8000): ExitInfo? =
        probeAll(context, Proxy.NO_PROXY, timeoutMs)

    private suspend fun probeAll(context: Context, proxy: Proxy, timeoutMs: Int): ExitInfo? {
        // Fast probe: Cloudflare trace (returns exact public IP + country marker).
        // `loc=T1` means "Tor" (not geography) — resolve the real country by IP so
        // exit-country matching and display work for Tor and any chained engine.
        try {
            val trace = fetch(proxy, "https://cloudflare.com/cdn-cgi/trace", timeoutMs)
            val ip = trace.lineSequence().find { it.startsWith("ip=") }?.removePrefix("ip=")?.trim()
            val loc = trace.lineSequence().find { it.startsWith("loc=") }?.removePrefix("loc=")?.trim()
            if (!ip.isNullOrBlank() && (IPV4.matches(ip) || ip.contains(':'))) {
                val (cc, name) = resolveCountry(context, ip, loc)
                return ExitInfo(ip = ip, countryCode = cc, countryName = name, fromNetwork = true)
            }
        } catch (e: Exception) {
            Log.d(TAG, "Cloudflare trace probe failed: ${e.message}")
        }

        // Secondary: ip2location.io JSON
        geoLookup(context, proxy, timeoutMs)?.let { return it }

        // Fallback: plain-text echo services + offline GeoIP database
        for (url in echoUrls) {
            val ip = try {
                val body = fetch(proxy, url, timeoutMs)
                body.lineSequence().map { it.trim() }.firstOrNull { IPV4.matches(it) }
            } catch (e: Exception) {
                null
            } ?: continue

            val (cc, name) = resolveCountry(context, ip, null)
            return ExitInfo(ip = ip, countryCode = cc, countryName = name, fromNetwork = false)
        }
        return null
    }

    /**
     * Turn a raw marker [hint] (e.g. Cloudflare `loc`) + [ip] into a real
     * (countryCode, countryName). Non-geo markers like T1 (Tor) fall back to the
     * offline IP database so matching/selection works.
     */
    private suspend fun resolveCountry(context: Context, ip: String, hint: String?): Pair<String, String> {
        val raw = hint?.trim()?.uppercase().orEmpty()
        val isGeo = raw.length == 2 && raw.all { it in 'A'..'Z' } && raw !in NON_GEO
        val offline = try { BridgeCountries.countryInfo(context, ip) } catch (e: Exception) { null }
        return if (isGeo) {
            val display = Locale("", raw).displayCountry
            val name = if (display.isNotBlank() && !display.equals(raw, true)) display else (offline?.second ?: raw)
            raw to name
        } else {
            if (offline != null) offline.first to offline.second else "" to ""
        }
    }

    // --- ip2location.io -------------------------------------------------------

    private suspend fun geoLookup(context: Context, proxy: Proxy, timeoutMs: Int): ExitInfo? = try {
        val body = fetch(proxy, GEO_URL, timeoutMs)
        val ip = json(body, "ip")
        if (ip.isNullOrBlank()) {
            null
        } else {
            val (cc, name) = resolveCountry(context, ip, json(body, "country_code"))
            val apiName = json(body, "country_name").orEmpty()
            ExitInfo(
                ip = ip,
                countryCode = cc,
                countryName = if (name.isNotBlank()) name else apiName,
                city = json(body, "city_name").orEmpty(),
                asn = json(body, "as").orEmpty(),
                fromNetwork = true
            )
        }
    } catch (e: Exception) {
        null
    }

    private fun json(body: String, key: String): String? =
        Regex("\"" + Regex.escape(key) + "\"\\s*:\\s*\"([^\"]*)\"")
            .find(body)?.groupValues?.get(1)

    // --- transport ------------------------------------------------------------

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
