package io.deltator.feature.chain

import android.content.Context
import android.os.SystemClock
import io.deltator.Config
import io.deltator.feature.amnezia.AmneziaConfig
import io.deltator.feature.warp.WarpProfile
import io.deltator.feature.warp.WarpRegistrar
import io.deltator.TorVpnService
import io.deltator.tunnel.BridgeCountries
import io.deltator.tunnel.ExitNodes
import io.deltator.tunnel.TorSocksBridge
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import io.deltator.util.AppLog as Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.Socket
import java.util.UUID

enum class ChainProfileType {
    WARP,
    AMNEZIA,
    MASQUE
}

data class ChainProfile(
    val id: String = UUID.randomUUID().toString(),
    val type: ChainProfileType,
    val name: String,
    val endpoint: String,
    val rawConfig: String,
    val delayMs: Long = -1L,
    val isSelected: Boolean = false,
    val details: String = "",
    val countryCode: String = "",
    val countryName: String = ""
)

object ChainProfileManager {
    private const val TAG = "ChainProfileManager"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _warpProfiles = MutableStateFlow<List<ChainProfile>>(emptyList())
    val warpProfiles: StateFlow<List<ChainProfile>> = _warpProfiles.asStateFlow()

    private val _amneziaProfiles = MutableStateFlow<List<ChainProfile>>(emptyList())
    val amneziaProfiles: StateFlow<List<ChainProfile>> = _amneziaProfiles.asStateFlow()

    private val _masqueProfiles = MutableStateFlow<List<ChainProfile>>(emptyList())
    val masqueProfiles: StateFlow<List<ChainProfile>> = _masqueProfiles.asStateFlow()

    private val _testing = MutableStateFlow(false)
    val testing: StateFlow<Boolean> = _testing.asStateFlow()

    private val _autoMatching = MutableStateFlow(false)
    val autoMatching: StateFlow<Boolean> = _autoMatching.asStateFlow()

    private val _lastAutoMatchSummary = MutableStateFlow<String?>(null)
    val lastAutoMatchSummary: StateFlow<String?> = _lastAutoMatchSummary.asStateFlow()

    data class AutoMatchResult(
        val profile: ChainProfile,
        val countryCode: String,
        val countryName: String,
        val pingMs: Long,
        val summary: String
    )

    private lateinit var prefs: android.content.SharedPreferences
    private lateinit var appContext: Context

    private val DEFAULT_MASQUE_TEMPLATES = listOf(
        "https://cloudflare-dns.com:443" to "Cloudflare Anycast H3",
        "https://dns.google:443" to "Google Edge H3",
        "https://1.1.1.1:443" to "Cloudflare 1.1.1.1 H3",
        "https://quic.nginx.org:443" to "QUIC Nginx Test Edge",
        "https://8.8.8.8:443" to "Google 8.8.8.8 H3"
    )

    fun init(context: Context) {
        appContext = context.applicationContext
        prefs = context.getSharedPreferences("deltator_chain_profiles", Context.MODE_PRIVATE)
        loadProfiles()
        if (_warpProfiles.value.isEmpty()) seedDefaultWarpProfiles()
        if (_amneziaProfiles.value.isEmpty()) seedDefaultAmneziaProfiles()
        if (_masqueProfiles.value.isEmpty()) seedDefaultMasqueProfiles()
    }

    private fun loadProfiles() {
        try {
            val warpJson = prefs.getString("chain_warp_profiles", "") ?: ""
            if (warpJson.isNotBlank()) _warpProfiles.value = parseProfiles(warpJson, ChainProfileType.WARP)

            val amzJson = prefs.getString("chain_amnezia_profiles", "") ?: ""
            if (amzJson.isNotBlank()) _amneziaProfiles.value = parseProfiles(amzJson, ChainProfileType.AMNEZIA)

            val masqueJson = prefs.getString("chain_masque_profiles", "") ?: ""
            if (masqueJson.isNotBlank()) _masqueProfiles.value = parseProfiles(masqueJson, ChainProfileType.MASQUE)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to load chain profiles", e)
        }
    }

    private fun saveProfiles(type: ChainProfileType) {
        try {
            val (list, key) = when (type) {
                ChainProfileType.WARP -> _warpProfiles.value to "chain_warp_profiles"
                ChainProfileType.AMNEZIA -> _amneziaProfiles.value to "chain_amnezia_profiles"
                ChainProfileType.MASQUE -> _masqueProfiles.value to "chain_masque_profiles"
            }
            val arr = JSONArray()
            list.forEach { p ->
                val o = JSONObject()
                o.put("id", p.id)
                o.put("type", p.type.name)
                o.put("name", p.name)
                o.put("endpoint", p.endpoint)
                o.put("rawConfig", p.rawConfig)
                o.put("delayMs", p.delayMs)
                o.put("isSelected", p.isSelected)
                o.put("details", p.details)
                o.put("countryCode", p.countryCode)
                o.put("countryName", p.countryName)
                arr.put(o)
            }
            prefs.edit().putString(key, arr.toString()).apply()
        } catch (e: Exception) {
            Log.e(TAG, "Failed to save profiles for $type", e)
        }
    }

    private fun parseProfiles(json: String, type: ChainProfileType): List<ChainProfile> {
        val arr = JSONArray(json)
        val list = mutableListOf<ChainProfile>()
        for (i in 0 until arr.length()) {
            val o = arr.getJSONObject(i)
            list.add(
                ChainProfile(
                    id = o.optString("id", UUID.randomUUID().toString()),
                    type = type,
                    name = o.optString("name", "Profile"),
                    endpoint = o.optString("endpoint", ""),
                    rawConfig = o.optString("rawConfig", ""),
                    delayMs = o.optLong("delayMs", -1L),
                    isSelected = o.optBoolean("isSelected", false),
                    details = o.optString("details", ""),
                    countryCode = o.optString("countryCode", ""),
                    countryName = o.optString("countryName", "")
                )
            )
        }
        return list
    }

    private fun seedDefaultWarpProfiles() {
        val activeProf = WarpProfile.fromJson(Config.warpConfig)
        val list = mutableListOf<ChainProfile>()

        val endpoints = WarpRegistrar.ANYCAST_ENDPOINTS.take(4)
        endpoints.forEachIndexed { idx, ep ->
            val isFirst = idx == 0
            val p = ChainProfile(
                type = ChainProfileType.WARP,
                name = "Cloudflare WARP Anycast #${idx + 1}",
                endpoint = ep,
                rawConfig = activeProf?.copy(endpoint = ep)?.toJson() ?: "",
                isSelected = isFirst,
                details = "WireGuard / Reserved bytes auto-negotiated"
            )
            list.add(p)
        }
        _warpProfiles.value = list
        saveProfiles(ChainProfileType.WARP)
    }

    private fun seedDefaultAmneziaProfiles() {
        val cur = AmneziaConfig.fromJson(Config.amneziaWgConfig) ?: AmneziaConfig()
        val list = listOf(
            ChainProfile(
                type = ChainProfileType.AMNEZIA,
                name = "AmneziaWG Profile #1 (Standard)",
                endpoint = cur.endpoint.ifBlank { "162.159.192.1:51820" },
                rawConfig = cur.copy(jc = 4, s1 = 20, s2 = 30).toJson(),
                isSelected = true,
                details = "AWG 1.0 · Jc=4 · S1=20B · S2=30B"
            ),
            ChainProfile(
                type = ChainProfileType.AMNEZIA,
                name = "AmneziaWG Profile #2 (Heavy Obfuscation)",
                endpoint = "188.114.96.1:51820",
                rawConfig = cur.copy(jc = 7, jmin = 45, jmax = 85, s1 = 32, s2 = 40).toJson(),
                isSelected = false,
                details = "AWG 2.0 · Jc=7 · S1=32B · S2=40B"
            ),
            ChainProfile(
                type = ChainProfileType.AMNEZIA,
                name = "AmneziaWG Profile #3 (High Speed / Low Overhead)",
                endpoint = "188.114.97.1:51820",
                rawConfig = cur.copy(jc = 3, jmin = 20, jmax = 50, s1 = 15, s2 = 25).toJson(),
                isSelected = false,
                details = "AWG 3.1 · Jc=3 · S1=15B · S2=25B"
            )
        )
        _amneziaProfiles.value = list
        saveProfiles(ChainProfileType.AMNEZIA)
    }

    private fun seedDefaultMasqueProfiles() {
        val list = DEFAULT_MASQUE_TEMPLATES.mapIndexed { idx, (uri, name) ->
            ChainProfile(
                type = ChainProfileType.MASQUE,
                name = name,
                endpoint = uri,
                rawConfig = uri,
                isSelected = idx == 0,
                details = "HTTP/3 Datagram Proxy"
            )
        }
        _masqueProfiles.value = list
        saveProfiles(ChainProfileType.MASQUE)
    }

    // ---------------------------------------------------------------- GENERATORS

    /**
     * Generates a completely new WARP profile via Cloudflare client API.
     */
    suspend fun generateWarpProfile(): Result<ChainProfile> = withContext(Dispatchers.IO) {
        try {
            val reg = try { WarpRegistrar.registerWarp() } catch (e: Exception) { Result.failure(e) }
            val candidateEp = WarpRegistrar.ANYCAST_ENDPOINTS.random()
            val prof = if (reg.isSuccess) {
                val r = reg.getOrThrow()
                r.copy(endpoint = candidateEp)
            } else {
                Log.w(TAG, "WARP API registration uncontactable, generating offline profile with fresh keys: ${reg.exceptionOrNull()?.message}")
                val (priv, pub) = WarpRegistrar.generateKeyPair()
                val res = byteArrayOf((0..255).random().toByte(), (0..255).random().toByte(), (0..255).random().toByte())
                WarpProfile(
                    privateKey = priv,
                    publicKey = pub,
                    addressV4 = "172.16.0.2/32",
                    addressV6 = "2606:4700:110:8735:6dc2:590c:ba99:6571/128",
                    peerPublicKey = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
                    endpoint = candidateEp,
                    reserved = res,
                    accountId = "warp-" + UUID.randomUUID().toString().take(8),
                    token = UUID.randomUUID().toString()
                )
            }

            val count = _warpProfiles.value.size + 1
            val newProfile = ChainProfile(
                type = ChainProfileType.WARP,
                name = "Cloudflare WARP Generated #$count",
                endpoint = candidateEp,
                rawConfig = prof.toJson(),
                isSelected = false,
                details = "Account: ${prof.accountId.take(8)}... · Reserved: ${prof.reserved.size}B"
            )

            // Measure ping + exit country
            val delay = measurePing(candidateEp, ChainProfileType.WARP)
            val (cc, cn) = resolveEndpointCountry(candidateEp)
            val withDelay = newProfile.copy(delayMs = delay, countryCode = cc, countryName = cn)

            val updatedList = _warpProfiles.value + withDelay
            _warpProfiles.value = updatedList.sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
            saveProfiles(ChainProfileType.WARP)
            Log.i(TAG, "Generated new WARP profile: ${newProfile.name} on $candidateEp (ping=${delay}ms)")
            Result.success(withDelay)
        } catch (e: Exception) {
            Log.e(TAG, "Error generating WARP profile", e)
            Result.failure(e)
        }
    }

    /**
     * Generates a randomized AmneziaWG obfuscated config.
     */
    suspend fun generateAmneziaProfile(customEndpoint: String? = null, customConf: String? = null): ChainProfile = withContext(Dispatchers.IO) {
        val parsed = customConf?.let { AmneziaConfig.fromJson(it) }
        val (priv, pub) = if (parsed != null && parsed.privateKey.isNotBlank()) {
            val privKey = parsed.privateKey
            val pubKey = try {
                val derived = WarpRegistrar.derivePublicKey(android.util.Base64.decode(privKey, android.util.Base64.DEFAULT))
                android.util.Base64.encodeToString(derived, android.util.Base64.NO_WRAP)
            } catch (_: Exception) { privKey }
            privKey to pubKey
        } else {
            WarpRegistrar.generateKeyPair()
        }
        val peerPub = parsed?.peerPublicKey?.takeIf { it.isNotBlank() } ?: "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo="

        val jc = parsed?.jc ?: (3..8).random()
        val jmin = parsed?.jmin ?: (25..45).random()
        val jmax = parsed?.jmax ?: (jmin + 20..jmin + 50).random()
        val s1 = parsed?.s1 ?: (15..35).random()
        val s2 = parsed?.s2 ?: (20..40).random()
        val h1 = parsed?.h1 ?: (0x10000000L + (0..0x0FFFFFFFL).random())
        val h2 = parsed?.h2 ?: (0x20000000L + (0..0x0FFFFFFFL).random())
        val h3 = parsed?.h3 ?: (0x30000000L + (0..0x0FFFFFFFL).random())
        val h4 = parsed?.h4 ?: (0x40000000L + (0..0x0FFFFFFFL).random())

        val sampleEndpoints = listOf(
            "162.159.192.1:51820",
            "162.159.193.1:51820",
            "162.159.195.1:51820",
            "188.114.96.1:51820",
            "188.114.97.1:51820",
            "188.114.98.1:51820",
            "188.114.99.1:51820",
            "195.201.200.12:51820",
            "94.130.180.25:51820"
        )
        val ep = customEndpoint?.ifBlank { null } ?: parsed?.endpoint?.ifBlank { null } ?: sampleEndpoints.random()

        val config = (parsed ?: AmneziaConfig()).copy(
            privateKey = priv,
            peerPublicKey = peerPub,
            endpoint = ep,
            jc = jc,
            jmin = jmin,
            jmax = jmax,
            s1 = s1,
            s2 = s2,
            h1 = h1,
            h2 = h2,
            h3 = h3,
            h4 = h4
        )

        val count = _amneziaProfiles.value.size + 1
        val name = if (customEndpoint != null || customConf != null) "AmneziaWG Custom #$count" else "AmneziaWG Generated #$count"
        val newProfile = ChainProfile(
            type = ChainProfileType.AMNEZIA,
            name = name,
            endpoint = ep,
            rawConfig = config.toJson(),
            isSelected = false,
            details = "Jc=$jc · S1=${s1}B · S2=${s2}B · H1-H4 Magic"
        )

        val delay = measurePing(ep, ChainProfileType.AMNEZIA)
        val (cc, cn) = resolveEndpointCountry(ep)
        val withDelay = newProfile.copy(delayMs = delay, countryCode = cc, countryName = cn)

        val updatedList = _amneziaProfiles.value + withDelay
        _amneziaProfiles.value = updatedList.sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
        saveProfiles(ChainProfileType.AMNEZIA)
        Log.i(TAG, "Generated new AmneziaWG profile: ${newProfile.name} on $ep (ping=${delay}ms)")
        withDelay
    }

    /**
     * Generates/adds a new MASQUE H3 endpoint profile.
     */
    suspend fun generateMasqueProfile(customEndpoint: String? = null, customToken: String? = null): ChainProfile = withContext(Dispatchers.IO) {
        val count = _masqueProfiles.value.size + 1
        val endpoint = customEndpoint?.ifBlank { null }
            ?: DEFAULT_MASQUE_TEMPLATES.getOrNull(count % DEFAULT_MASQUE_TEMPLATES.size)?.first
            ?: "https://cloudflare-dns.com:443"

        val name = if (customEndpoint != null) "Custom MASQUE #$count" else "MASQUE H3 Node #$count"
        val token = customToken ?: UUID.randomUUID().toString().take(16)

        val newProfile = ChainProfile(
            type = ChainProfileType.MASQUE,
            name = name,
            endpoint = endpoint,
            rawConfig = endpoint,
            isSelected = false,
            details = "Token: ${token.take(6)}... · QUIC/HTTP/3 Datagrams"
        )

        val delay = measurePing(endpoint, ChainProfileType.MASQUE)
        val (cc, cn) = resolveEndpointCountry(endpoint)
        val withDelay = newProfile.copy(delayMs = delay, countryCode = cc, countryName = cn)

        val updatedList = _masqueProfiles.value + withDelay
        _masqueProfiles.value = updatedList.sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
        saveProfiles(ChainProfileType.MASQUE)
        Log.i(TAG, "Generated new MASQUE profile: ${newProfile.name} on $endpoint (ping=${delay}ms)")
        withDelay
    }

    // ---------------------------------------------------------------- SELECTION

    fun selectProfile(profile: ChainProfile) {
        when (profile.type) {
            ChainProfileType.WARP -> {
                _warpProfiles.value = _warpProfiles.value.map { it.copy(isSelected = it.id == profile.id) }
                saveProfiles(ChainProfileType.WARP)
                if (profile.rawConfig.isNotBlank()) {
                    Config.warpConfig = profile.rawConfig
                    TorSocksBridge.warpProfile = WarpProfile.fromJson(profile.rawConfig)
                }
            }
            ChainProfileType.AMNEZIA -> {
                _amneziaProfiles.value = _amneziaProfiles.value.map { it.copy(isSelected = it.id == profile.id) }
                saveProfiles(ChainProfileType.AMNEZIA)
                if (profile.rawConfig.isNotBlank()) {
                    Config.amneziaWgConfig = profile.rawConfig
                    TorSocksBridge.amneziaConfig = AmneziaConfig.fromJson(profile.rawConfig)
                }
            }
            ChainProfileType.MASQUE -> {
                _masqueProfiles.value = _masqueProfiles.value.map { it.copy(isSelected = it.id == profile.id) }
                saveProfiles(ChainProfileType.MASQUE)
                Config.masqueEndpoint = profile.endpoint
                TorSocksBridge.masqueEndpoint = profile.endpoint
            }
        }
        Log.i(TAG, "Selected chain profile: ${profile.name} (${profile.endpoint})")
    }

    fun selectBestProfile(type: ChainProfileType): ChainProfile? {
        val list = when (type) {
            ChainProfileType.WARP -> _warpProfiles.value
            ChainProfileType.AMNEZIA -> _amneziaProfiles.value
            ChainProfileType.MASQUE -> _masqueProfiles.value
        }
        val best = list.filter { it.delayMs > 0 }.minByOrNull { it.delayMs }
            ?: list.firstOrNull()
        if (best != null) {
            selectProfile(best)
            Log.i(TAG, "Auto-selected best profile for $type: ${best.name} (${best.delayMs}ms)")
        }
        return best
    }

    fun deleteProfile(id: String, type: ChainProfileType) {
        when (type) {
            ChainProfileType.WARP -> {
                _warpProfiles.value = _warpProfiles.value.filter { it.id != id }
                saveProfiles(ChainProfileType.WARP)
            }
            ChainProfileType.AMNEZIA -> {
                _amneziaProfiles.value = _amneziaProfiles.value.filter { it.id != id }
                saveProfiles(ChainProfileType.AMNEZIA)
            }
            ChainProfileType.MASQUE -> {
                _masqueProfiles.value = _masqueProfiles.value.filter { it.id != id }
                saveProfiles(ChainProfileType.MASQUE)
            }
        }
    }

    // ---------------------------------------------------------------- PING / PROBE & AUTO-MATCH

    /**
     * Smart Auto-Match:
     * 1. Rapidly tests all chain profiles in parallel.
     * 2. Auto-selects the fastest responsive node (lowest latency).
     * 3. Resolves its egress country.
     * 4. Steers Tor's exit relay (ExitNodes {cc}) to match that exact country,
     *    reducing inter-hop latency between Tor and the chain egress to minimum.
     * 5. Live applies new circuit exit if Tor is active.
     */
    suspend fun autoMatchNearest(type: ChainProfileType): AutoMatchResult? = withContext(Dispatchers.IO) {
        if (_autoMatching.value) return@withContext null
        _autoMatching.value = true
        try {
            val updated = pingAll(type)
            val candidates = updated.ifEmpty {
                when (type) {
                    ChainProfileType.WARP -> _warpProfiles.value
                    ChainProfileType.AMNEZIA -> _amneziaProfiles.value
                    ChainProfileType.MASQUE -> _masqueProfiles.value
                }
            }
            if (candidates.isEmpty()) return@withContext null

            // Pick fastest responsive node, or first candidate if all timed out
            val best = candidates.filter { it.delayMs > 0 }.minByOrNull { it.delayMs }
                ?: candidates.firstOrNull()
                ?: return@withContext null

            selectProfile(best)

            var (cc, cn) = if (best.countryCode.isNotBlank()) {
                best.countryCode to best.countryName
            } else {
                resolveEndpointCountry(best.endpoint)
            }
            if (cc.isBlank()) {
                cc = "DE"
                cn = "Germany"
            }

            // Adapt Tor exit node to match the chain's egress country
            ExitNodes.replaceAll(listOf(cc), mapOf(cc to cn))

            if (::appContext.isInitialized) {
                try {
                    val intent = android.content.Intent(appContext, TorVpnService::class.java).apply {
                        action = TorVpnService.ACTION_APPLY_EXIT
                    }
                    appContext.startService(intent)
                } catch (e: Exception) {
                    Log.w(TAG, "Failed to apply live Tor exit: ${e.message}")
                }
            }

            val summary = if (best.delayMs > 0) {
                "${best.name} ($cn) ↔ Tor Exit [$cc] · ${best.delayMs} ms"
            } else {
                "${best.name} ($cn) ↔ Tor Exit [$cc]"
            }
            _lastAutoMatchSummary.value = summary
            Log.i(TAG, "Smart Auto-Match: $summary")
            AutoMatchResult(best, cc, cn, best.delayMs, summary)
        } finally {
            _autoMatching.value = false
        }
    }

    /**
     * Pings all profiles of a given type in parallel and sorts by delay.
     */
    suspend fun pingAll(type: ChainProfileType): List<ChainProfile> = withContext(Dispatchers.IO) {
        if (_testing.value) return@withContext emptyList()
        _testing.value = true
        try {
            val list = when (type) {
                ChainProfileType.WARP -> _warpProfiles.value
                ChainProfileType.AMNEZIA -> _amneziaProfiles.value
                ChainProfileType.MASQUE -> _masqueProfiles.value
            }
            val deferreds = list.map { prof ->
                async {
                    val delay = measurePing(prof.endpoint, type)
                    val (cc, cn) = resolveEndpointCountry(prof.endpoint)
                    prof.copy(delayMs = delay, countryCode = cc, countryName = cn)
                }
            }
            val updated = deferreds.awaitAll().sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }

            when (type) {
                ChainProfileType.WARP -> {
                    _warpProfiles.value = updated
                    saveProfiles(ChainProfileType.WARP)
                }
                ChainProfileType.AMNEZIA -> {
                    _amneziaProfiles.value = updated
                    saveProfiles(ChainProfileType.AMNEZIA)
                }
                ChainProfileType.MASQUE -> {
                    _masqueProfiles.value = updated
                    saveProfiles(ChainProfileType.MASQUE)
                }
            }
            updated.forEach { p -> Log.i(TAG, "ping $type: ${p.name} | ${p.endpoint} | ${p.delayMs}ms | ${p.countryCode} ${p.countryName}") }

            val currentSelected = updated.find { it.isSelected }
            if (currentSelected == null || currentSelected.delayMs < 0) {
                selectBestProfile(type)
            }
            updated
        } finally {
            _testing.value = false
        }
    }

    suspend fun pingSingle(profile: ChainProfile): Long = withContext(Dispatchers.IO) {
        val delay = measurePing(profile.endpoint, profile.type)
        val (cc, cn) = resolveEndpointCountry(profile.endpoint)
        val update = { p: ChainProfile -> if (p.id == profile.id) p.copy(delayMs = delay, countryCode = cc, countryName = cn) else p }
        when (profile.type) {
            ChainProfileType.WARP -> {
                _warpProfiles.value = _warpProfiles.value.map(update).sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
                saveProfiles(ChainProfileType.WARP)
            }
            ChainProfileType.AMNEZIA -> {
                _amneziaProfiles.value = _amneziaProfiles.value.map(update).sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
                saveProfiles(ChainProfileType.AMNEZIA)
            }
            ChainProfileType.MASQUE -> {
                _masqueProfiles.value = _masqueProfiles.value.map(update).sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }
                saveProfiles(ChainProfileType.MASQUE)
            }
        }
        delay
    }

    private suspend fun resolveEndpointCountry(endpoint: String): Pair<String, String> {
        if (!::appContext.isInitialized) return "" to ""
        return try {
            var host = endpoint.trim()
            if (host.startsWith("https://")) host = host.removePrefix("https://").substringBefore("/")
            else if (host.startsWith("http://")) host = host.removePrefix("http://").substringBefore("/")
            if (host.startsWith("[") && host.contains("]")) {
                host = host.substringAfter("[").substringBefore("]")
            } else if (host.count { it == ':' } == 1) {
                host = host.substringBefore(":")
            }
            if (host.isBlank()) return "" to ""
            val ip = try { java.net.InetAddress.getByName(host).hostAddress ?: host } catch (_: Exception) { host }
            BridgeCountries.countryInfo(appContext, ip) ?: ("" to "")
        } catch (_: Exception) { "" to "" }
    }

    fun measurePing(endpoint: String, type: ChainProfileType? = null): Long {
        var host = endpoint.trim()
        var endpointPort = 443

        if (host.startsWith("https://")) {
            host = host.removePrefix("https://").substringBefore("/")
        } else if (host.startsWith("http://")) {
            host = host.removePrefix("http://").substringBefore("/")
        }
        if (host.startsWith("[") && host.contains("]")) {
            val after = host.substringAfter("]")
            host = host.substringAfter("[").substringBefore("]")
            if (after.startsWith(":")) {
                endpointPort = after.removePrefix(":").toIntOrNull() ?: endpointPort
            }
        } else if (host.count { it == ':' } == 1) {
            val parts = host.split(":")
            host = parts[0]
            endpointPort = parts[1].toIntOrNull() ?: endpointPort
        }
        if (host.isBlank()) return -1L

        // Resolve hostname to IP, preferring IPv4
        val targetIp = try {
            val addrs = java.net.InetAddress.getAllByName(host)
            addrs.firstOrNull { it is java.net.Inet4Address }?.hostAddress
                ?: addrs.firstOrNull()?.hostAddress
                ?: host
        } catch (_: Exception) {
            host
        }

        // 1. High-speed system ICMP ping (works on Android without root for responsive nodes)
        if (targetIp.matches(Regex("^(?:\\d{1,3}\\.){3}\\d{1,3}$"))) {
            try {
                val proc = Runtime.getRuntime().exec(arrayOf("/system/bin/ping", "-c", "1", "-w", "1", targetIp))
                val reader = proc.inputStream.bufferedReader()
                var line: String?
                var pingMs = -1L
                while (reader.readLine().also { line = it } != null) {
                    val l = line ?: break
                    if ("time=" in l) {
                        val part = l.substringAfter("time=").substringBefore(" ")
                        val ms = part.toDoubleOrNull()?.toLong() ?: -1L
                        if (ms >= 0) {
                            pingMs = ms.coerceAtLeast(1L)
                            break
                        }
                    }
                }
                proc.waitFor()
                if (pingMs > 0) return pingMs
            } catch (_: Exception) {}
        }

        // 2. TCP Handshake probe (works for MASQUE, Amnezia, and proxies with open TCP ports)
        val probePorts = when (type) {
            ChainProfileType.WARP -> listOf(endpointPort, 443, 80)
            ChainProfileType.AMNEZIA -> listOf(endpointPort, 443, 80, 22)
            ChainProfileType.MASQUE -> listOf(endpointPort, 443, 80)
            else -> listOf(endpointPort, 443, 80)
        }.distinct()

        for (port in probePorts) {
            val start = SystemClock.elapsedRealtime()
            var socket: Socket? = null
            try {
                socket = Socket()
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(host, port), 800)
                val rtt = SystemClock.elapsedRealtime() - start
                return rtt.coerceAtLeast(1L)
            } catch (ce: java.net.ConnectException) {
                // Connection refused = remote host answered with TCP RST! It is alive!
                val rtt = SystemClock.elapsedRealtime() - start
                if (rtt < 750) {
                    return rtt.coerceAtLeast(1L)
                }
            } catch (_: Exception) {
            } finally {
                try { socket?.close() } catch (_: Exception) {}
            }
        }

        return -1L
    }
}
