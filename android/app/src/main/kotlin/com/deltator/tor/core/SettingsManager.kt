package com.deltator.tor.core

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONObject

class SettingsManager(context: Context) {
    private val prefs: SharedPreferences =
        context.getSharedPreferences("deltator_settings", Context.MODE_PRIVATE)

    init {
        if (!prefs.contains("initialized")) {
            loadDefaults()
            prefs.edit().putBoolean("initialized", true).apply()
        }
    }

    private fun loadDefaults() {
        val editor = prefs.edit()
        Config.DEFAULT_CFG.forEach { (key, value) ->
            when (value) {
                is Boolean -> editor.putBoolean(key, value)
                is Int -> editor.putInt(key, value)
                is String -> editor.putString(key, value)
            }
        }
        editor.apply()
    }

    fun getBoolean(key: String): Boolean = prefs.getBoolean(key, Config.DEFAULT_CFG[key] as? Boolean ?: false)
    fun getInt(key: String): Int = prefs.getInt(key, Config.DEFAULT_CFG[key] as? Int ?: 0)
    fun getString(key: String): String = prefs.getString(key, Config.DEFAULT_CFG[key] as? String ?: "") ?: ""

    fun setBoolean(key: String, value: Boolean) = prefs.edit().putBoolean(key, value).apply()
    fun setInt(key: String, value: Int) = prefs.edit().putInt(key, value).apply()
    fun setString(key: String, value: String) = prefs.edit().putString(key, value).apply()

    fun saveLastSuccess(category: String, transport: String, ip: String) {
        prefs.edit()
            .putString("last_success_cat", category)
            .putString("last_success_trans", transport)
            .putString("last_success_ip", ip)
            .apply()
    }

    fun getLastSuccess(): Triple<String, String, String>? {
        val cat = prefs.getString("last_success_cat", "") ?: ""
        val trans = prefs.getString("last_success_trans", "") ?: ""
        val ip = prefs.getString("last_success_ip", "") ?: ""
        return if (cat.isNotEmpty() && trans.isNotEmpty() && ip.isNotEmpty()) {
            Triple(cat, trans, ip)
        } else null
    }

    fun generateTorrc(
        dataDir: String,
        socksPort: Int,
        ctrlPort: Int,
        bridgeLines: List<String>,
        ptDir: String
    ): String {
        val useBridges = if (bridgeLines.isNotEmpty()) "1" else "0"

        val sb = StringBuilder()
        sb.appendLine("Log notice stdout")
        sb.appendLine("DataDirectory $dataDir")
        sb.appendLine("GeoIPFile $dataDir/geoip")
        sb.appendLine("GeoIPv6File $dataDir/geoip6")
        sb.appendLine("SOCKSPort 127.0.0.1:$socksPort")
        sb.appendLine("ControlPort 127.0.0.1:$ctrlPort")
        sb.appendLine("CookieAuthentication 1")
        sb.appendLine("DormantClientTimeout 24 hours")
        sb.appendLine("DormantOnFirstStartup 0")
        sb.appendLine("DormantCanceledByStartup 1")
        sb.appendLine("UseBridges $useBridges")
        sb.appendLine("MaxCircuitDirtiness ${getInt("max_circuit_dirtiness")}")
        sb.appendLine("NewCircuitPeriod ${getInt("new_circuit_period")}")
        sb.appendLine("NumEntryGuards ${getInt("num_entry_guards")}")
        sb.appendLine("AllowNonRFC953Hostnames 1")
        sb.appendLine("EnforceDistinctSubnets 0")
        sb.appendLine("MaxClientCircuitsPending 64")
        sb.appendLine("CircuitBuildTimeout 60")
        sb.appendLine("LearnCircuitBuildTimeout 0")
        sb.appendLine("GuardLifetime 90 days")
        sb.appendLine("NumDirectoryGuards 6")
        sb.appendLine("TokenBucketRefillInterval 10 msec")

        if (getBoolean("dns_over_tor")) {
            sb.appendLine("DNSPort 127.0.0.1:9053")
        }

        if (getBoolean("exit_nodes_enabled")) {
            val countries = getString("exit_nodes_countries")
            if (countries.isNotEmpty()) {
                sb.appendLine("ExitNodes $countries")
                sb.appendLine("StrictNodes ${if (getBoolean("strict_exit_nodes")) "1" else "0"}")
            }
        }

        if (getBoolean("sni_enabled")) {
            val sniHost = getString("sni_host")
            if (sniHost.isNotEmpty()) {
                sb.appendLine("# SNI override active: $sniHost")
            }
        }

        if (getBoolean("exp_connection_padding")) sb.appendLine("ConnectionPadding 1")
        if (getBoolean("exp_reduced_connection_padding")) sb.appendLine("ReducedConnectionPadding 1")

        val cst = getInt("exp_circuit_stream_timeout")
        if (cst > 0) sb.appendLine("CircuitStreamTimeout $cst")

        val st = getInt("exp_socks_timeout")
        if (st > 0) sb.appendLine("SocksTimeout $st")

        if (getBoolean("exp_safe_logging")) sb.appendLine("SafeLogging 1")
        if (getBoolean("exp_avoid_disk_writes")) sb.appendLine("AvoidDiskWrites 1")
        if (getBoolean("exp_hardware_accel")) sb.appendLine("HardwareAccel 1")
        if (getBoolean("exp_client_dns_reject_internal")) sb.appendLine("ClientDNSRejectInternalAddresses 1")

        if (getBoolean("exp_fascist_firewall")) {
            sb.appendLine("FascistFirewall 1")
            val fp = getString("exp_firewall_ports")
            if (fp.isNotEmpty()) sb.appendLine("FirewallPorts $fp")
        }

        val ra = getString("exp_reachable_addresses")
        if (ra.isNotEmpty()) sb.appendLine("ReachableAddresses $ra")

        val nc = getInt("exp_num_cpus")
        if (nc > 0) sb.appendLine("NumCPUs $nc")

        val en = getString("exp_exclude_nodes")
        if (en.isNotEmpty()) sb.appendLine("ExcludeNodes $en")

        val een = getString("exp_exclude_exit_nodes")
        if (een.isNotEmpty()) sb.appendLine("ExcludeExitNodes $een")

        val nesp = getString("exp_no_exit_stream_ports")
        if (nesp.isNotEmpty()) {
            nesp.split(",").forEach { port ->
                val p = port.trim()
                if (p.isNotEmpty()) sb.appendLine("ExitPolicy reject *:$p")
            }
        }

        if (getBoolean("exp_use_entry_guards_as_dir_guards")) sb.appendLine("UseEntryGuardsAsDirGuards 1")

        val pbct = getInt("exp_path_bias_circ_threshold")
        if (pbct > 0) sb.appendLine("PathBiasCircThreshold $pbct")

        if (getBoolean("exp_isolate_dest_addr")) {
            sb.insertLineAfter("SOCKSPort", "IsolateDestAddr")
        }

        if (useBridges == "1") {
            sb.appendLine()
            sb.appendLine("ClientTransportPlugin meek_lite,obfs2,obfs3,obfs4,scramblesuit,webtunnel exec $ptDir/lyrebird")
            sb.appendLine("ClientTransportPlugin snowflake exec $ptDir/lyrebird")
            sb.appendLine("ClientTransportPlugin conjure exec $ptDir/conjure-client -registerURL \"https://registration.refraction.network/api\"")
        }

        if (useBridges == "1" && bridgeLines.isNotEmpty()) {
            sb.appendLine()
            bridgeLines.forEach { line ->
                sb.appendLine("Bridge $line")
            }
        }

        return sb.toString()
    }

    private fun StringBuilder.insertLineAfter(search: String, insert: String) {
        val lines = toString().lines().toMutableList()
        val idx = lines.indexOfFirst { it.startsWith(search) }
        if (idx >= 0) {
            lines.add(idx + 1, insert)
            clear()
            append(lines.joinToString("\n"))
        }
    }

    fun clearAll() {
        prefs.edit().clear().apply()
        loadDefaults()
    }
}
