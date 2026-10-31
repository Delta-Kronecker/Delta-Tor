package io.deltator.core.dns

import io.deltator.util.AppLog as Log
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger

/**
 * High-performance, zero-latency synthetic DNS responder (FakeDNS).
 *
 * Allocates virtual IP addresses from the benchmark range `198.18.0.0/15` (RFC 2544 / RFC 3330)
 * for queried hostnames, responding instantly to local DNS queries without sending external UDP packets.
 *
 * Benefits:
 * - 0ms DNS latency: queries are answered directly in-process.
 * - 100% DNS leak protection: no DNS packets leave the device.
 * - Accurate SNI / domain routing: synthetic IPs are mapped back to domain names for SOCKS5 CONNECT
 *   and split tunneling rules without expensive packet sniffing.
 */
object FakeDns {
    private const val TAG = "FakeDns"

    // 198.18.0.0/15 range: 198.18.0.1 to 198.19.255.254 (131,070 usable synthetic IPs)
    private const val BASE_IP_START = 0xC6120001L // 198.18.0.1
    private const val IP_COUNT = 131070

    private val domainToIp = ConcurrentHashMap<String, String>()
    private val ipToDomain = ConcurrentHashMap<String, String>()
    private val counter = AtomicInteger(0)

    @Volatile
    var enabled: Boolean = true

    /**
     * Check if the provided IP address is within the 198.18.0.0/15 FakeDNS subnet.
     */
    fun isFakeIp(ip: String): Boolean {
        val parts = ip.split('.')
        if (parts.size != 4) return false
        val first = parts[0].toIntOrNull() ?: return false
        val second = parts[1].toIntOrNull() ?: return false
        return first == 198 && (second == 18 || second == 19)
    }

    /**
     * Look up the domain name associated with a synthetic IP.
     */
    fun getDomain(ip: String): String? = ipToDomain[ip]

    /**
     * Look up or allocate a synthetic IP address for a given domain name.
     */
    fun getOrAllocateIp(domain: String): String {
        val cleanDomain = domain.lowercase().trimEnd('.')
        return domainToIp.computeIfAbsent(cleanDomain) {
            val idx = Math.floorMod(counter.getAndIncrement(), IP_COUNT)
            val ipVal = BASE_IP_START + idx
            val b1 = (ipVal shr 24 and 0xFF).toInt()
            val b2 = (ipVal shr 16 and 0xFF).toInt()
            val b3 = (ipVal shr 8 and 0xFF).toInt()
            val b4 = (ipVal and 0xFF).toInt()
            val ipStr = "$b1.$b2.$b3.$b4"
            ipToDomain[ipStr] = cleanDomain
            Log.d(TAG, "Allocated synthetic IP $ipStr for domain $cleanDomain")
            ipStr
        }
    }

    /**
     * Clear all cached mappings (e.g. on tunnel reset).
     */
    fun clear() {
        domainToIp.clear()
        ipToDomain.clear()
        counter.set(0)
    }

    /**
     * Process an RFC 1035 DNS query packet. If it contains an A (IPv4) query,
     * immediately crafts and returns a synthetic DNS answer packet.
     * Returns null if the packet could not be parsed or is not an A query.
     */
    fun handleDnsQuery(queryBytes: ByteArray): ByteArray? {
        if (!enabled || queryBytes.size < 12) return null
        return try {
            val txId0 = queryBytes[0]
            val txId1 = queryBytes[1]
            val qdCount = ((queryBytes[4].toInt() and 0xFF) shl 8) or (queryBytes[5].toInt() and 0xFF)
            if (qdCount < 1) return null

            // Parse question domain name
            var pos = 12
            val domainBuilder = StringBuilder()
            while (pos < queryBytes.size) {
                val len = queryBytes[pos].toInt() and 0xFF
                pos++
                if (len == 0) break
                if (pos + len > queryBytes.size) return null
                if (domainBuilder.isNotEmpty()) domainBuilder.append('.')
                for (i in 0 until len) {
                    domainBuilder.append(queryBytes[pos + i].toInt().toChar())
                }
                pos += len
            }

            if (pos + 4 > queryBytes.size) return null
            val qType = ((queryBytes[pos].toInt() and 0xFF) shl 8) or (queryBytes[pos + 1].toInt() and 0xFF)
            val qClass = ((queryBytes[pos + 2].toInt() and 0xFF) shl 8) or (queryBytes[pos + 3].toInt() and 0xFF)
            pos += 4

            val domain = domainBuilder.toString().lowercase()
            if (domain.isBlank()) return null

            if (qClass != 1) return null

            // Type 28 = AAAA (IPv6), Type 65 = HTTPS (RFC 9460)
            if (qType == 28 || qType == 65) {
                // Return NOERROR with 0 answers (NODATA) in 0ms:
                // - AAAA: Tells OS/browser there is no IPv6, forcing immediate IPv4 synthetic connection
                //   without waiting for IPv6 timeouts or blackholing through Tor.
                // - HTTPS (Type 65): Prevents QUIC/HTTP3 pre-negotiation hints via DNS, ensuring
                //   standard reliable TLS over TCP.
                val out = ByteArrayOutputStream()
                val dos = DataOutputStream(out)
                dos.writeByte(txId0.toInt())
                dos.writeByte(txId1.toInt())
                dos.writeShort(0x8180) // Standard query response, No error
                dos.writeShort(1)      // QDCOUNT: 1
                dos.writeShort(0)      // ANCOUNT: 0 (NODATA)
                dos.writeShort(0)      // NSCOUNT: 0
                dos.writeShort(0)      // ARCOUNT: 0

                // Question section copy
                val questionLen = pos - 12
                dos.write(queryBytes, 12, questionLen)
                dos.flush()
                return out.toByteArray()
            }

            // Type 1 = A record (IPv4)
            if (qType != 1) {
                return null
            }

            val fakeIp = getOrAllocateIp(domain)
            val ipOctets = fakeIp.split('.').map { it.toInt().toByte() }
            if (ipOctets.size != 4) return null

            // Build DNS response packet
            val out = ByteArrayOutputStream()
            val dos = DataOutputStream(out)

            // Header
            dos.writeByte(txId0.toInt())
            dos.writeByte(txId1.toInt())
            dos.writeShort(0x8180) // Flags: Standard query response, No error
            dos.writeShort(1)      // QDCOUNT: 1
            dos.writeShort(1)      // ANCOUNT: 1
            dos.writeShort(0)      // NSCOUNT: 0
            dos.writeShort(0)      // ARCOUNT: 0

            // Question section copy
            val questionLen = pos - 12
            dos.write(queryBytes, 12, questionLen)

            // Answer section
            dos.writeShort(0xC00C) // Name pointer to question at offset 12
            dos.writeShort(1)      // TYPE: A
            dos.writeShort(1)      // CLASS: IN
            dos.writeInt(60)       // TTL: 60s
            dos.writeShort(4)      // RDLENGTH: 4 bytes
            for (b in ipOctets) {
                dos.writeByte(b.toInt())
            }

            dos.flush()
            out.toByteArray()
        } catch (e: Exception) {
            Log.d(TAG, "Failed to craft FakeDNS response: ${e.message}")
            null
        }
    }
}
