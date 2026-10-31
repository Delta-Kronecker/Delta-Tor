package io.deltator.tunnel

import android.util.Base64
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL

/**
 * Out-of-Band Bridge Distribution via DNS-over-HTTPS (DoH) TXT records.
 *
 * Proposal #18: Decouples bridge discovery from GitHub Releases and static CDN endpoints.
 * When GitHub or GitLab is blocked, DeltaTor queries trusted DoH resolvers (Cloudflare, Google, Quad9)
 * for TXT records containing encoded bridge lists.
 */
object DoHBridgeFetcher {
    private const val TAG = "DoHBridgeFetcher"
    private const val TIMEOUT_MS = 8000

    private val DOH_PROVIDERS = listOf(
        "https://cloudflare-dns.com/dns-query?name=%s&type=TXT",
        "https://dns.google/resolve?name=%s&type=TXT",
        "https://dns.quad9.net:5053/dns-query?name=%s&type=TXT"
    )

    private const val DEFAULT_FALLBACK_DOMAIN = "bridges.deltator.net"

    suspend fun fetchBridgesViaDoH(
        domain: String = DEFAULT_FALLBACK_DOMAIN
    ): List<String> = withContext(Dispatchers.IO) {
        for (providerTemplate in DOH_PROVIDERS) {
            val endpoint = String.format(providerTemplate, domain)
            try {
                Log.i(TAG, "Querying DoH TXT records from $endpoint")
                val url = URL(endpoint)
                val conn = url.openConnection() as HttpURLConnection
                conn.connectTimeout = TIMEOUT_MS
                conn.readTimeout = TIMEOUT_MS
                conn.setRequestProperty("Accept", "application/dns-json")

                val responseCode = conn.responseCode
                if (responseCode == 200) {
                    val body = conn.inputStream.use { stream ->
                        BufferedReader(InputStreamReader(stream)).readText()
                    }
                    val lines = parseDohJson(body)
                    if (lines.isNotEmpty()) {
                        Log.i(TAG, "Successfully fetched ${lines.size} bridges via DoH from $domain")
                        return@withContext lines
                    }
                }
            } catch (e: Exception) {
                Log.w(TAG, "DoH fetch failed for $endpoint: ${e.message}")
            }
        }
        emptyList()
    }

    private fun parseDohJson(jsonStr: String): List<String> {
        val json = JSONObject(jsonStr)
        val answers = json.optJSONArray("Answer") ?: return emptyList()
        val collected = mutableListOf<String>()

        for (i in 0 until answers.length()) {
            val item = answers.optJSONObject(i) ?: continue
            val type = item.optInt("type")
            // Type 16 is DNS TXT
            if (type == 16) {
                var data = item.optString("data", "").trim()
                if (data.startsWith("\"") && data.endsWith("\"") && data.length >= 2) {
                    data = data.substring(1, data.length - 1)
                }
                // Try Base64 decoding, or fallback to plain text if not base64
                val decoded = try {
                    val bytes = Base64.decode(data, Base64.DEFAULT)
                    String(bytes, Charsets.UTF_8)
                } catch (_: Exception) {
                    data
                }
                decoded.lines().map { it.trim() }.filter { it.isNotEmpty() && !it.startsWith("#") }.forEach {
                    collected.add(it)
                }
            }
        }
        return collected
    }
}
