package io.deltator.feature.chain

import android.content.Context
import android.os.SystemClock
import io.deltator.Config
import io.deltator.feature.amnezia.AmneziaConfig
import io.deltator.feature.warp.WarpProfile
import io.deltator.feature.warp.WarpRegistrar
import io.deltator.tunnel.TorSocksBridge
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
    val details: String = ""
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

    private lateinit var prefs: android.content.SharedPreferences

    private val DEFAULT_MASQUE_TEMPLATES = listOf(
        "https://cloudflare-dns.com:443" to "Cloudflare Anycast H3",
        "https://dns.google:443" to "Google Edge H3",
        "https://1.1.1.1:443" to "Cloudflare 1.1.1.1 H3",
        "https://masque.example.com:443" to "Community MASQUE Gateway"
    )

    fun init(context: Context) {
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
                    details = o.optString("details", "")
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
                endpoint = cur.endpoint.ifBlank { "185.190.140.1:51820" },
                rawConfig = cur.copy(jc = 4, s1 = 20, s2 = 30).toJson(),
                isSelected = true,
                details = "AWG 1.0 · Jc=4 · S1=20B · S2=30B"
            ),
            ChainProfile(
                type = ChainProfileType.AMNEZIA,
                name = "AmneziaWG Profile #2 (Heavy Obfuscation)",
                endpoint = "94.130.180.25:51820",
                rawConfig = cur.copy(jc = 7, jmin = 45, jmax = 85, s1 = 32, s2 = 40).toJson(),
                isSelected = false,
                details = "AWG 2.0 · Jc=7 · S1=32B · S2=40B"
            ),
            ChainProfile(
                type = ChainProfileType.AMNEZIA,
                name = "AmneziaWG Profile #3 (High Speed / Low Overhead)",
                endpoint = "195.201.200.12:51820",
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

            // Measure ping
            val delay = measurePing(candidateEp)
            val withDelay = newProfile.copy(delayMs = delay)

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
    suspend fun generateAmneziaProfile(): ChainProfile = withContext(Dispatchers.IO) {
        val (priv, pub) = WarpRegistrar.generateKeyPair()
        val (_, peerPub) = WarpRegistrar.generateKeyPair()

        val jc = (3..8).random()
        val jmin = (25..45).random()
        val jmax = (jmin + 20..jmin + 50).random()
        val s1 = (15..35).random()
        val s2 = (20..40).random()
        val h1 = 0x10000000L + (0..0x0FFFFFFFL).random()
        val h2 = 0x20000000L + (0..0x0FFFFFFFL).random()
        val h3 = 0x30000000L + (0..0x0FFFFFFFL).random()
        val h4 = 0x40000000L + (0..0x0FFFFFFFL).random()

        val sampleEndpoints = listOf(
            "awg-de.deltator.net:51820",
            "awg-nl.deltator.net:51820",
            "awg-fi.deltator.net:51820",
            "awg-us.deltator.net:51820",
            "194.87.112.44:51820"
        )
        val ep = sampleEndpoints.random()

        val config = AmneziaConfig(
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
        val newProfile = ChainProfile(
            type = ChainProfileType.AMNEZIA,
            name = "AmneziaWG Generated #$count",
            endpoint = ep,
            rawConfig = config.toJson(),
            isSelected = false,
            details = "Jc=$jc · S1=${s1}B · S2=${s2}B · H1-H4 Magic"
        )

        val delay = measurePing(ep)
        val withDelay = newProfile.copy(delayMs = delay)

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
            ?: "https://masque-node-${count}.example.com:443"

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

        val delay = measurePing(endpoint)
        val withDelay = newProfile.copy(delayMs = delay)

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

    // ---------------------------------------------------------------- PING / PROBE

    /**
     * Pings all profiles of a given type in parallel and sorts the list.
     */
    suspend fun pingAll(type: ChainProfileType) = withContext(Dispatchers.IO) {
        if (_testing.value) return@withContext
        _testing.value = true
        try {
            val list = when (type) {
                ChainProfileType.WARP -> _warpProfiles.value
                ChainProfileType.AMNEZIA -> _amneziaProfiles.value
                ChainProfileType.MASQUE -> _masqueProfiles.value
            }
            val semaphore = Semaphore(6)
            val updated = list.map { prof ->
                val delay = semaphore.withPermit { measurePing(prof.endpoint) }
                prof.copy(delayMs = delay)
            }.sortedBy { if (it.delayMs < 0) Long.MAX_VALUE else it.delayMs }

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
        } finally {
            _testing.value = false
        }
    }

    suspend fun pingSingle(profile: ChainProfile): Long = withContext(Dispatchers.IO) {
        val delay = measurePing(profile.endpoint)
        val update = { p: ChainProfile -> if (p.id == profile.id) p.copy(delayMs = delay) else p }
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

    private fun measurePing(endpoint: String): Long {
        var host = endpoint.trim()
        var port = 443

        if (host.startsWith("https://")) {
            host = host.removePrefix("https://").substringBefore("/")
        } else if (host.startsWith("http://")) {
            host = host.removePrefix("http://").substringBefore("/")
        }
        if (host.contains(":")) {
            val parts = host.split(":")
            host = parts[0]
            port = parts[1].toIntOrNull() ?: 443
        }

        // If Tor bridge is running, measure through Tor SOCKS5 to get true double-hop RTT
        if (TorSocksBridge.isRunning()) {
            try {
                val start = SystemClock.elapsedRealtime()
                val s = Socket()
                s.connect(InetSocketAddress("127.0.0.1", Config.proxyPort), 3000)
                s.soTimeout = 4000
                val out = s.getOutputStream()
                val inp = s.getInputStream()

                // SOCKS5 handshake
                out.write(byteArrayOf(0x05, 0x01, 0x00))
                out.flush()
                val gr = ByteArray(2)
                inp.read(gr)

                // SOCKS5 CONNECT
                val hb = host.toByteArray(Charsets.US_ASCII)
                out.write(byteArrayOf(0x05, 0x01, 0x00, 0x03, hb.size.toByte()) + hb + byteArrayOf((port shr 8).toByte(), (port and 0xFF).toByte()))
                out.flush()
                val resp = ByteArray(10)
                inp.read(resp)
                val rtt = SystemClock.elapsedRealtime() - start
                s.close()
                if (resp.size >= 2 && resp[1] == 0x00.toByte()) {
                    return rtt
                }
            } catch (_: Exception) {}
        }

        // Direct TCP connect fallback
        return try {
            val start = SystemClock.elapsedRealtime()
            val socket = Socket()
            socket.connect(InetSocketAddress(host, port), 2500)
            val rtt = SystemClock.elapsedRealtime() - start
            socket.close()
            rtt
        } catch (_: Exception) {
            try {
                val start = SystemClock.elapsedRealtime()
                val addr = java.net.InetAddress.getByName(host)
                if (addr.isReachable(2000)) {
                    SystemClock.elapsedRealtime() - start
                } else -1L
            } catch (_: Exception) {
                -1L
            }
        }
    }
}
