package kittoku.osc

import kittoku.osc.preference.AUTH_PROTOCOl_PAP
import kittoku.osc.preference.AUTH_PROTOCOL_MSCHAPv2


/** What one SSTP session is asked to do: run the device's real VPN or only
 *  establish the negotiation up to IPCP as a reachability probe. */
internal enum class SstpMode { RUN, PROBE }

/**
 * Every value the vendored SSTP core reads. This replaces the
 * SharedPreferences access of the original app so the core can be driven from
 * DeltaTor's own settings engine.
 */
internal class SstpParams(
    val mode: SstpMode,
    /** VPN Gate server hostname, never its IP: the name travels into the SOCKS5
     *  CONNECT so Tor resolves it remotely, exactly like every other hop. */
    val hostname: String,
    /** Server TLS port; a host line without a port arrives here as the default. */
    val port: Int,
    /** The local hop the tunnel is chained through: Tor's SOCKS bridge. */
    val proxyHostname: String,
    val proxyPort: Int,
    /** VPN Gate's well-known credentials for its Virtual Hub "VPN". */
    val username: String,
    val password: String,
    val mru: Int = 1400,
    val mtu: Int = 1400,
    val authProtocols: Set<String> = setOf(AUTH_PROTOCOl_PAP, AUTH_PROTOCOL_MSCHAPv2),
    val authTimeout: Long = 30_000,
    val pppIPv4Enabled: Boolean = true,
    val pppIPv6Enabled: Boolean = false,
    /** VPN Gate serves self-signed certificates; verification stays off by
     *  default, mirroring the original client. */
    val doVerifyCert: Boolean = false,
    val doVerifyHostname: Boolean = false,
    /** Excluded from the chain tunnel so the app (and with it Tor's own
     *  outbound links) never loops back into the tun it created. */
    val ownPackageName: String? = null,
)
