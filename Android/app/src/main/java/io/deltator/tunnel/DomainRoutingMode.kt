package io.deltator.tunnel

/**
 * Domain routing mode for geo-bypass / split routing decisions.
 * BYPASS: listed domains leave the tunnel and go direct.
 * ONLY_VPN: only listed domains use the tunnel (everything else goes direct).
 */
enum class DomainRoutingMode(val value: String) {
    BYPASS("bypass"),
    ONLY_VPN("only_vpn");

    companion object {
        fun fromValue(value: String): DomainRoutingMode {
            return entries.find { it.value == value } ?: BYPASS
        }
    }
}