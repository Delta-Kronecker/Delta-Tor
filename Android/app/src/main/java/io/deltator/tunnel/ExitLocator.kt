package io.deltator.tunnel

import android.content.Context
import io.deltator.util.AppLog as Log
import java.io.BufferedReader
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.Proxy
import java.net.URL
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

    suspend fun locate(
        context: Context,
        socksHost: String = "127.0.0.1",
        socksPort: Int = io.deltator.Config.proxyPort,
        timeoutMs: Int = 8000
    ): ExitInfo? {
        val socksProxy = Proxy(Proxy.Type.SOCKS, InetSocketAddress(socksHost, socksPort))
        return probeAll(context, socksProxy, timeoutMs)
    }

    private suspend fun probeAll(context: Context, proxy: Proxy, timeoutMs: Int): ExitInfo? {
        // Fast probe: Cloudflare trace (returns exact public IP + ISO country code)
        try {
            val trace = fetch(proxy, "https://cloudflare.com/cdn-cgi/trace", timeoutMs)
            val ip = trace.lineSequence().find { it.startsWith("ip=") }?.removePrefix("ip=")?.trim()
            val loc = trace.lineSequence().find { it.startsWith("loc=") }?.removePrefix("loc=")?.trim()
            if (!ip.isNullOrBlank() && IPV4.matches(ip)) {
                val cInfo = try { BridgeCountries.countryInfo(context, ip) } catch (e: Exception) { null }
                val cName = if (!loc.isNullOrBlank()) {
                    val display = java.util.Locale("", loc).displayCountry
                    if (display.isNotBlank() && display != loc) display
                    else cInfo?.second ?: ""
                } else {
                    cInfo?.second ?: ""
                }
                return ExitInfo(
                    ip = ip,
                    countryCode = loc ?: "",
                    countryName = cName,
                    fromNetwork = true
                )
            }
        } catch (e: Exception) {
            Log.d(TAG, "Cloudflare trace probe failed: ${e.message}")
        }

        // Secondary: ip2location.io JSON
        geoLookup(proxy, timeoutMs)?.let { return it }

        // Fallback: plain-text echo services + offline GeoIP database
        for (url in echoUrls) {
            val ip = try {
                val body = fetch(proxy, url, timeoutMs)
                body.lineSequence().map { it.trim() }.firstOrNull { IPV4.matches(it) }
            } catch (e: Exception) {
                null
            } ?: continue

            val offline = try { BridgeCountries.countryInfo(context, ip) } catch (e: Exception) { null }
            val code = offline?.first ?: ""
            val name = offline?.second ?: if (code.isNotBlank()) java.util.Locale("", code).displayCountry else ""
            return ExitInfo(
                ip = ip,
                countryCode = code,
                countryName = name,
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
