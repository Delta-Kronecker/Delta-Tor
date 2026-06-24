package com.deltator.tor.core

object Config {
    const val SOCKS_PORT = 9050
    const val CTRL_PORT = 9051
    const val HTTP_PROXY_PORT = 19052

    const val CHECK_HOST = "www.gstatic.com"
    const val CHECK_PATH = "/generate_204"

    fun slotPorts(index: Int) = Triple(
        9061 + index,
        9071 + index,
        19061 + index
    )

    val AUTO_SEQUENCE = listOf(
        Triple("Tested & Active", "obfs4", "IPv4"),
        Triple("Tested & Active", "vanilla", "IPv4"),
        Triple("Tested & Active", "webtunnel", "IPv4"),
        Triple("Fresh (72h)", "obfs4", "IPv4"),
        Triple("Fresh (72h)", "vanilla", "IPv4"),
        Triple("Fresh (72h)", "webtunnel", "IPv4"),
        Triple("Full Archive", "obfs4", "IPv4"),
        Triple("Full Archive", "vanilla", "IPv4"),
        Triple("Full Archive", "webtunnel", "IPv4")
    )

    val BRIDGE_DATA = listOf(
        BridgeEntry("Tested & Active", "obfs4", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_tested.txt"),
        BridgeEntry("Tested & Active", "webtunnel", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_tested.txt"),
        BridgeEntry("Tested & Active", "vanilla", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_tested.txt"),
        BridgeEntry("Fresh (72h)", "obfs4", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_72h.txt"),
        BridgeEntry("Fresh (72h)", "obfs4", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6_72h.txt"),
        BridgeEntry("Fresh (72h)", "webtunnel", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_72h.txt"),
        BridgeEntry("Fresh (72h)", "webtunnel", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6_72h.txt"),
        BridgeEntry("Fresh (72h)", "vanilla", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_72h.txt"),
        BridgeEntry("Fresh (72h)", "vanilla", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6_72h.txt"),
        BridgeEntry("Full Archive", "obfs4", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4.txt"),
        BridgeEntry("Full Archive", "obfs4", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/obfs4_ipv6.txt"),
        BridgeEntry("Full Archive", "webtunnel", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel.txt"),
        BridgeEntry("Full Archive", "webtunnel", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/webtunnel_ipv6.txt"),
        BridgeEntry("Full Archive", "vanilla", "IPv4",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla.txt"),
        BridgeEntry("Full Archive", "vanilla", "IPv6",
            "https://raw.githubusercontent.com/Delta-Kronecker/Tor-Bridges-Collector/refs/heads/main/bridge/vanilla_ipv6.txt")
    )

    val FRESH_DATA = BRIDGE_DATA.filter { it.category == "Fresh (72h)" }

    val DEFAULT_CFG = mapOf(
        "auto_connect_timeout" to 180,
        "bridges_in_torrc" to 100,
        "shuffle_bridges" to true,
        "dns_over_tor" to false,
        "max_circuit_dirtiness" to 1800,
        "new_circuit_period" to 10,
        "num_entry_guards" to 15,
        "keep_alive_enabled" to true,
        "keep_alive_interval" to 120,
        "watchdog_enabled" to true,
        "watchdog_interval" to 30,
        "exit_nodes_enabled" to false,
        "exit_nodes_countries" to "{nl},{de},{fr},{ch},{at},{se},{no},{fi},{is}",
        "strict_exit_nodes" to false,
        "auto_proxy_on_connect" to false,
        "sni_enabled" to false,
        "sni_host" to "www.google.com",
        "custom_bridges" to "",
        "use_custom_bridges" to false,
        "exp_connection_padding" to false,
        "exp_reduced_connection_padding" to false,
        "exp_circuit_stream_timeout" to 0,
        "exp_socks_timeout" to 0,
        "exp_safe_logging" to false,
        "exp_avoid_disk_writes" to false,
        "exp_hardware_accel" to false,
        "exp_client_dns_reject_internal" to false,
        "exp_fascist_firewall" to false,
        "exp_firewall_ports" to "80,443",
        "exp_reachable_addresses" to "",
        "exp_num_cpus" to 0,
        "exp_exclude_nodes" to "",
        "exp_exclude_exit_nodes" to "",
        "exp_use_entry_guards_as_dir_guards" to false,
        "exp_path_bias_circ_threshold" to 0,
        "exp_isolate_dest_addr" to false,
        "exp_isolate_dest_port" to false,
        "exp_no_exit_stream_ports" to ""
    )
}

data class BridgeEntry(
    val category: String,
    val transport: String,
    val ip: String,
    val url: String
)

data class SlotDef(
    val name: String,
    val source: String,
    val category: String? = null,
    val transport: String? = null,
    val ip: String? = null,
    val noBridge: Boolean = false
)
