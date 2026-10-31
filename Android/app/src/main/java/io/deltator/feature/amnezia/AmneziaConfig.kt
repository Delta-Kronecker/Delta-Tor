package io.deltator.feature.amnezia

import org.json.JSONObject

/**
 * Configuration model for AmneziaWG (AWG 1.0 - 3.1).
 *
 * Proposal #1:
 * Extends standard WireGuard with anti-DPI obfuscation parameters:
 * - Jc: Junk packet count sent before/during handshake
 * - Jmin / Jmax: Range of junk packet payload sizes (randomized)
 * - S1 / S2: Obfuscation bytes prepended to Handshake Initiation (S1) and Handshake Response (S2)
 * - H1 - H4: Custom protocol magic header values replacing standard WireGuard type indicators (1, 2, 3, 4)
 */
data class AmneziaConfig(
    val privateKey: String = "",
    val address: String = "10.8.0.2/24",
    val dns: String = "1.1.1.1",
    val peerPublicKey: String = "",
    val presharedKey: String? = null,
    val endpoint: String = "",
    val allowedIps: String = "0.0.0.0/0, ::/0",
    // DPI parameters
    val jc: Int = 4,
    val jmin: Int = 40,
    val jmax: Int = 70,
    val s1: Int = 20,
    val s2: Int = 30,
    val h1: Long = 0x12345678L,
    val h2: Long = 0x23456789L,
    val h3: Long = 0x3456789AL,
    val h4: Long = 0x456789ABL
) {
    fun toJson(): String {
        val obj = JSONObject()
        obj.put("privateKey", privateKey)
        obj.put("address", address)
        obj.put("dns", dns)
        obj.put("peerPublicKey", peerPublicKey)
        obj.put("presharedKey", presharedKey ?: "")
        obj.put("endpoint", endpoint)
        obj.put("allowedIps", allowedIps)
        obj.put("jc", jc)
        obj.put("jmin", jmin)
        obj.put("jmax", jmax)
        obj.put("s1", s1)
        obj.put("s2", s2)
        obj.put("h1", h1)
        obj.put("h2", h2)
        obj.put("h3", h3)
        obj.put("h4", h4)
        return obj.toString()
    }

    fun toConf(): String = buildString {
        appendLine("[Interface]")
        appendLine("PrivateKey = $privateKey")
        appendLine("Address = $address")
        if (dns.isNotBlank()) appendLine("DNS = $dns")
        appendLine("Jc = $jc")
        appendLine("Jmin = $jmin")
        appendLine("Jmax = $jmax")
        appendLine("S1 = $s1")
        appendLine("S2 = $s2")
        appendLine("H1 = $h1")
        appendLine("H2 = $h2")
        appendLine("H3 = $h3")
        appendLine("H4 = $h4")
        appendLine()
        appendLine("[Peer]")
        appendLine("PublicKey = $peerPublicKey")
        if (!presharedKey.isNullOrBlank()) appendLine("PresharedKey = $presharedKey")
        appendLine("Endpoint = $endpoint")
        appendLine("AllowedIPs = $allowedIps")
    }

    companion object {
        fun fromJson(jsonStr: String): AmneziaConfig? = try {
            val obj = JSONObject(jsonStr)
            AmneziaConfig(
                privateKey = obj.optString("privateKey", ""),
                address = obj.optString("address", "10.8.0.2/24"),
                dns = obj.optString("dns", "1.1.1.1"),
                peerPublicKey = obj.optString("peerPublicKey", ""),
                presharedKey = obj.optString("presharedKey", "").takeIf { it.isNotBlank() },
                endpoint = obj.optString("endpoint", ""),
                allowedIps = obj.optString("allowedIps", "0.0.0.0/0, ::/0"),
                jc = obj.optInt("jc", 4),
                jmin = obj.optInt("jmin", 40),
                jmax = obj.optInt("jmax", 70),
                s1 = obj.optInt("s1", 20),
                s2 = obj.optInt("s2", 30),
                h1 = obj.optLong("h1", 0x12345678L),
                h2 = obj.optLong("h2", 0x23456789L),
                h3 = obj.optLong("h3", 0x3456789AL),
                h4 = obj.optLong("h4", 0x456789ABL)
            )
        } catch (_: Exception) {
            null
        }

        fun parseConf(confText: String): AmneziaConfig {
            var privKey = ""
            var addr = "10.8.0.2/24"
            var dnsServer = "1.1.1.1"
            var peerPub = ""
            var psk: String? = null
            var ep = ""
            var allowed = "0.0.0.0/0, ::/0"
            var jc = 4
            var jmin = 40
            var jmax = 70
            var s1 = 20
            var s2 = 30
            var h1 = 0x12345678L
            var h2 = 0x23456789L
            var h3 = 0x3456789AL
            var h4 = 0x456789ABL

            confText.lines().forEach { rawLine ->
                val line = rawLine.trim()
                if (line.isEmpty() || line.startsWith("#")) return@forEach
                val parts = line.split("=", limit = 2).map { it.trim() }
                if (parts.size == 2) {
                    val k = parts[0].lowercase()
                    val v = parts[1]
                    when (k) {
                        "privatekey" -> privKey = v
                        "address" -> addr = v
                        "dns" -> dnsServer = v
                        "publickey" -> peerPub = v
                        "presharedkey" -> psk = v
                        "endpoint" -> ep = v
                        "allowedips" -> allowed = v
                        "jc" -> jc = v.toIntOrNull() ?: jc
                        "jmin" -> jmin = v.toIntOrNull() ?: jmin
                        "jmax" -> jmax = v.toIntOrNull() ?: jmax
                        "s1" -> s1 = v.toIntOrNull() ?: s1
                        "s2" -> s2 = v.toIntOrNull() ?: s2
                        "h1" -> h1 = v.toLongOrNull() ?: h1
                        "h2" -> h2 = v.toLongOrNull() ?: h2
                        "h3" -> h3 = v.toLongOrNull() ?: h3
                        "h4" -> h4 = v.toLongOrNull() ?: h4
                    }
                }
            }

            return AmneziaConfig(
                privateKey = privKey,
                address = addr,
                dns = dnsServer,
                peerPublicKey = peerPub,
                presharedKey = psk,
                endpoint = ep,
                allowedIps = allowed,
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
        }
    }
}
